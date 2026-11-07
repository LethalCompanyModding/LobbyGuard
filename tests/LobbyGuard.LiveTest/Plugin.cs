using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

namespace LobbyGuard.LiveTest;

// Test-only. On the host it feeds each malformed payload to the host's own receive path as if a joined client had sent
// it, then checks that LobbyGuard kicked that client. On the client it rejoins the LAN lobby after every kick.
// Start the game with the same arguments as DevLaunch: --lan --host, or --lan --join <address>.
[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInProcess("Lethal Company.exe")]
public class Plugin : BaseUnityPlugin
{
    private const float KickWait = 10;
    private const float RejoinWait = 120;
    private const float SettleSeconds = 12;

    internal static ManualLogSource Log { get; private set; } = null!;

    private readonly List<string> lobbyGuardLines = new();
    private Hook? menuHook;
    private string? address;
    private bool wasConnected;
    private MonoBehaviour runner = null!;

    // The plugin's own object stops running coroutines once the game loads its first scene, so they run on one of ours.
    private sealed class Runner : MonoBehaviour
    {
    }

    private void Awake()
    {
        Log = Logger;
        GameObject holder = new("LobbyGuard LiveTest") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(holder);
        runner = holder.AddComponent<Runner>();
        string[] args = Environment.GetCommandLineArgs();
        int join = Array.IndexOf(args, "--join");
        address = join >= 0 && join + 1 < args.Length ? args[join + 1] : null;
        if (args.Contains("--host"))
        {
            BepInEx.Logging.Logger.Listeners.Add(new Capture(lobbyGuardLines));
            Log.LogInfo("LIVETEST ready host");
            runner.StartCoroutine(RunHost());
        }
        else if (address != null)
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            menuHook = new Hook(typeof(MenuManager).GetMethod(nameof(MenuManager.Start), hidden), (Action<Action<MenuManager>, MenuManager>)OnMenu);
            Log.LogInfo("LIVETEST ready client");
        }
    }

    private void Update()
    {
        if (address != null && NetworkManager.Singleton is { IsConnectedClient: true })
        {
            wasConnected = true;
        }
    }

    // The game goes back to the menu after a kick. DevLaunch only joins once, so this joins again.
    private void OnMenu(Action<MenuManager> orig, MenuManager menu)
    {
        orig(menu);
        if (wasConnected && !menu.isInitScene)
        {
            wasConnected = false;
            Log.LogInfo("LIVETEST client disconnected, rejoining");
            runner.StartCoroutine(Rejoin(menu));
        }
    }

    private IEnumerator Rejoin(MenuManager menu)
    {
        NetworkManager.Singleton.GetComponent<UnityTransport>().ConnectionData.Address = address;
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            yield return new WaitForSeconds(2f);
            menu.menuNotification.SetActive(false);
            menu.StartAClient();
            menu.SetLoadingScreen(isLoading: true);
            float giveUp = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < giveUp)
            {
                if (NetworkManager.Singleton.IsConnectedClient)
                {
                    Log.LogInfo($"LIVETEST client rejoined on attempt {attempt}");
                    yield break;
                }
                yield return null;
            }
            NetworkManager.Singleton.Shutdown();
        }
        Log.LogWarning("LIVETEST client could not rejoin");
    }

    private static ulong? Remote()
    {
        NetworkManager manager = NetworkManager.Singleton;
        return manager is { IsServer: true } ? manager.ConnectedClientsIds.Where(id => id != manager.LocalClientId).Cast<ulong?>().FirstOrDefault() : null;
    }

    private IEnumerator RunHost()
    {
        List<(string Name, bool Pass, string Detail)> results = new();
        yield return runner.StartCoroutine(Until(() => Remote() != null, 600));
        if (Remote() == null)
        {
            Log.LogWarning("LIVETEST summary 0 PASS 1 FAIL: no client joined");
            yield break;
        }
        yield return new WaitForSeconds(SettleSeconds * 2);
        foreach (Case c in Cases.All)
        {
            ulong? target = Remote();
            if (target is not ulong id)
            {
                results.Add((c.Name, false, "no client to test"));
                break;
            }
            int seen = lobbyGuardLines.Count;
            NetworkManager manager = NetworkManager.Singleton;
            string? error = null;
            try
            {
                c.Inject(manager, id);
            }
            catch (Exception e)
            {
                error = $"{e.GetType().Name}: {e.Message}";
            }
            if (c.Kind == Kind.Direct)
            {
                string? problem = error ?? c.Verify?.Invoke(lobbyGuardLines.Skip(seen).ToList());
                results.Add((c.Name, problem == null, problem ?? "ok"));
                Log.LogInfo($"LIVETEST {c.Name} {(problem == null ? "PASS" : "FAIL")} {problem ?? "ok"}");
                continue;
            }
            bool kicks = c.Kind == Kind.Kick;
            yield return runner.StartCoroutine(Until(() => kicks ? !manager.ConnectedClients.ContainsKey(id) : lobbyGuardLines.Skip(seen).Any(l => l.Contains(c.Reason)), KickWait));
            bool gone = !manager.ConnectedClients.ContainsKey(id);
            string? line = lobbyGuardLines.Skip(seen).FirstOrDefault(l => l.StartsWith("Disconnected ") && l.Contains(c.Reason));
            bool pass = line != null && gone == kicks;
            string detail = line ?? $"LobbyGuard logged: {string.Join(" | ", lobbyGuardLines.Skip(seen))}";
            if (error != null)
            {
                detail += $" (injection threw {error})";
            }
            results.Add((c.Name, pass, detail));
            Log.LogInfo($"LIVETEST {c.Name} {(pass ? "PASS" : "FAIL")} {detail}");
            if (!kicks || !gone)
            {
                continue;
            }
            Cooldowns.Clear();
            yield return runner.StartCoroutine(Until(() => Remote() is ulong next && next != id, RejoinWait));
            if (Remote() is not ulong again || again == id)
            {
                Log.LogWarning("LIVETEST client did not rejoin, stopping");
                break;
            }
            yield return new WaitForSeconds(SettleSeconds);
        }
        int failed = results.Count(r => !r.Pass) + (results.Count < Cases.All.Count ? Cases.All.Count - results.Count : 0);
        Log.LogInfo($"LIVETEST summary {results.Count(r => r.Pass)} PASS {failed} FAIL of {Cases.All.Count}");
    }

    private IEnumerator Until(Func<bool> done, float seconds)
    {
        float end = Time.realtimeSinceStartup + seconds;
        float next = 0;
        while (!done() && Time.realtimeSinceStartup < end)
        {
            if (Time.realtimeSinceStartup > next)
            {
                next = Time.realtimeSinceStartup + 15;
                Log.LogInfo($"LIVETEST waiting ({(int)(end - Time.realtimeSinceStartup)} s left, server {NetworkManager.Singleton?.IsServer}, clients {NetworkManager.Singleton?.ConnectedClientsIds.Count})");
            }
            yield return null;
        }
    }

    private sealed class Capture(List<string> lines) : ILogListener
    {
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (eventArgs.Source.SourceName == "LobbyGuard")
            {
                lines.Add(eventArgs.Data.ToString());
            }
        }

        public void Dispose()
        {
        }
    }
}

internal enum Kind
{
    // The client is disconnected and has to rejoin.
    Kick,
    // The host logs the kick of a stand-in client, and the real one stays.
    Fake,
    // The check runs in the host's own code and Verify judges the result.
    Direct,
}

internal sealed class Case(string Name, string Reason, Action<NetworkManager, ulong> Inject, Kind Kind = Kind.Kick, Func<List<string>, string?>? Verify = null)
{
    public string Name { get; } = Name;

    public string Reason { get; } = Reason;

    public Action<NetworkManager, ulong> Inject { get; } = Inject;

    public Kind Kind { get; } = Kind;

    public Func<List<string>, string?>? Verify { get; } = Verify;
}

internal static class Cases
{
    private const ushort Magic = 0x1160;
    private const int HeaderSize = 16;

    public static readonly IReadOnlyList<Case> All =
    [
        new("short-packet", "packet too short", (m, c) => Receive(m, c, new byte[4])),
        new("not-netcode", "not a Netcode packet", (m, c) => Receive(m, c, Batch(0, [], magic: 0x1234)) ),
        new("hash-mismatch", "packet hash doesn't match", (m, c) => Receive(m, c, Batch(1, Message(Type<SceneEventMessage>(m), []), badHash: true))),
        new("bad-message-header", "bad message header", (m, c) => Receive(m, c, Batch(1, [0xFF]))),
        new("message-bigger-than-packet", "message bigger than its packet", (m, c) => Receive(m, c, Batch(1, Header(Type<SceneEventMessage>(m), 1000)))),
        new("host-only-message", "sent a message only the host can send", (m, c) => Receive(m, c, Batch(1, Message(Type<ClientRpcMessage>(m), [])))),
        new("second-join-request", "asked to join a second time", (m, c) => Receive(m, c, Batch(1, Message(Type<ConnectionRequestMessage>(m), [])))),
        new("host-only-scene-event", "sent a scene change only the host can send", (m, c) => Receive(m, c, Batch(1, Message(Type<SceneEventMessage>(m), [(byte)SceneEventType.Load])))),
        new("second-synchronize-complete", "finished joining a second time", (m, c) => Receive(m, c, Batch(1, Message(Type<SceneEventMessage>(m), [8])))),
        new("client-rpc-as-server-rpc", "a ClientRpc only the host can call", (m, c) => Receive(m, c, Batch(1, Rpc(m, 1568596901u, [1])))),
        new("rpc-string-longer-than-message", "array or string longer than its message", (m, c) => Receive(m, c, Batch(1, Rpc(m, 2787681914u, [1, 0xF0, 0xFF, 0xFF, 0x7F])))),
        new("too-many-early-messages", "too many messages before being accepted", (m, c) => AsPending(m, 9001, Batch(33, Repeat(Message(Type<SceneEventMessage>(m), []), 33))), Kind.Fake),
        new("nested-serializer-length", "", (m, c) => NestedLength(), Kind.Direct, _ => nested),
        new("message-flood", "too many packets or messages", (m, c) => Receive(m, c, Batch(20001, Repeat(Message(Type<SceneEventMessage>(m), []), 20001)))),
        new("packet-flood", "too many packets or messages", (m, c) =>
        {
            for (int i = 0; i < 2100 && m.ConnectedClients.ContainsKey(c); i++)
            {
                Receive(m, c, Batch(0, []));
            }
        }),
        ..VoiceCases.All(),
        new("error-flood", "too many errors from their messages", (m, c) => Receive(m, c, Batch(150, Repeat(UnknownRpc(m), 150)))),
        new("deferred-flood", "too many messages for objects that don't exist", (m, c) => Receive(m, c, Batch(600, Repeat(MissingObjectRpc(m), 600)))),
        new("lan-has-no-cooldown", "", (m, c) => { }, Kind.Direct, lines => lines.Any(l => l.Contains("rejoin for")) ? "LobbyGuard set a cooldown for a client with no SteamID" : null),
    ];

    private static string? nested;

    // A client that has not been accepted yet, which no real one still is by the time the test can send anything.
    private static void AsPending(NetworkManager manager, ulong id, byte[] payload)
    {
        NetworkConnectionManager connections = manager.ConnectionManager;
        connections.m_PendingClients[id] = new PendingClient { ClientId = id, ConnectionState = PendingClient.State.PendingConnection };
        connections.TransportIdToClientIdMap[id] = id;
        connections.ClientIdToTransportIdMap[id] = id;
        try
        {
            ArraySegment<byte> data = new(payload);
            connections.DataEventHandler(id, ref data, Time.realtimeSinceStartup);
        }
        finally
        {
            connections.m_PendingClients.Remove(id);
            connections.TransportIdToClientIdMap.Remove(id);
            connections.ClientIdToTransportIdMap.Remove(id);
        }
    }

    // No vanilla RPC takes a length-bearing INetworkSerializable (SerializedList is the only one, and nothing sends it), so
    // this reads one the way an RPC would: a message that claims far more ulongs than it holds.
    private static unsafe void NestedLength()
    {
        using FastBufferWriter writer = new(16, Allocator.Temp);
        writer.WriteValueSafe(0x10000000);
        FastBufferReader reader = new(writer, Allocator.Temp);
        try
        {
            BufferSerializerReader inner = new(reader);
            BufferSerializer<BufferSerializerReader> serializer = new(inner);
            SerializedList list = default;
            ((INetworkSerializable)list).NetworkSerialize(serializer);
            nested = "the read went through";
        }
        catch (OverflowException e) when (e.Message == "array or string longer than its message")
        {
            nested = null;
        }
        catch (Exception e)
        {
            nested = $"NGO failed on its own: {e.GetType().Name}: {e.Message}";
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static byte[] UnknownRpc(NetworkManager manager) => RpcTo(manager, HUDManager.Instance.NetworkObjectId, 0xFFFFFFFEu);

    private static byte[] MissingObjectRpc(NetworkManager manager) => RpcTo(manager, 0x7FFFFFF0, 1);

    // Hands a payload to the host's receive path under the client's own transport id, as NGO does for real data.
    public static void Receive(NetworkManager manager, ulong client, byte[] payload)
    {
        NetworkConnectionManager connections = manager.ConnectionManager;
        ArraySegment<byte> data = new(payload);
        connections.DataEventHandler(connections.ClientIdToTransportId(client), ref data, Time.realtimeSinceStartup);
    }

    private static uint Type<T>(NetworkManager manager) where T : INetworkMessage => manager.MessageManager.GetMessageType(typeof(T));

    private static byte[] Packed(uint value)
    {
        using FastBufferWriter writer = new(8, Allocator.Temp);
        BytePacker.WriteValueBitPacked(writer, value);
        return writer.ToArray();
    }

    private static byte[] Header(uint type, uint size) => Packed(type).Concat(Packed(size)).ToArray();

    private static byte[] Message(uint type, byte[] payload) => Header(type, (uint)payload.Length).Concat(payload).ToArray();

    private static byte[] Repeat(byte[] message, int times) => Enumerable.Range(0, times).SelectMany(_ => message).ToArray();

    // A ServerRpc message the way NGO writes one, for a registered method of the object's first behaviour.
    private static byte[] Rpc(NetworkManager manager, uint method, byte[] arguments) =>
        RpcTo(manager, HUDManager.Instance.NetworkObjectId, method, arguments);

    private static byte[] RpcTo(NetworkManager manager, ulong objectId, uint method, byte[]? arguments = null)
    {
        arguments ??= [];
        HUDManager hud = HUDManager.Instance;
        using FastBufferWriter body = new(Math.Max(arguments.Length, 1), Allocator.Temp);
        body.WriteBytes(arguments);
        ServerRpcMessage rpc = new()
        {
            Metadata = new RpcMetadata { NetworkObjectId = objectId, NetworkBehaviourId = hud.NetworkBehaviourId, NetworkRpcMethodId = method },
            WriteBuffer = body,
        };
        using FastBufferWriter writer = new(arguments.Length + 64, Allocator.Temp);
        rpc.Serialize(writer, 0);
        return Message(Type<ServerRpcMessage>(manager), writer.ToArray());
    }

    // NGO's batch: magic, message count, total size, then a hash of the messages.
    private static unsafe byte[] Batch(ushort count, byte[] messages, ushort magic = Magic, bool badHash = false)
    {
        byte[] packet = new byte[HeaderSize + messages.Length];
        Array.Copy(messages, 0, packet, HeaderSize, messages.Length);
        ulong hash;
        fixed (byte* body = packet)
        {
            hash = XXHash.Hash64(body + HeaderSize, messages.Length);
        }
        BitConverter.GetBytes(magic).CopyTo(packet, 0);
        BitConverter.GetBytes(count).CopyTo(packet, 2);
        BitConverter.GetBytes(packet.Length).CopyTo(packet, 4);
        BitConverter.GetBytes(badHash ? ~hash : hash).CopyTo(packet, 8);
        return packet;
    }
}

// LobbyGuard keeps a kicked Steam player out for a while. That's off in LAN games, but clearing it keeps a Steam run
// from locking out the test client.
internal static class Cooldowns
{
    public static void Clear()
    {
        Type? approval = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("LobbyGuard.Approval")).FirstOrDefault(t => t != null);
        object? table = approval?.GetField("cooldowns", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        table?.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(table, null);
    }
}
