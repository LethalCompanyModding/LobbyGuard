using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LobbyGuard.LiveTest;

// A case is skipped, not failed, when the lobby has no object to aim its call at (an enemy, a vehicle, the company desk).
internal sealed class CaseSkipped(string why) : Exception(why);

// One case per rule in LobbyGuard's RpcRules table (the table's source is compiled into this plugin), each sending the
// host the call a vanilla client never makes, as the joined client. By default a case runs for the first rule of each
// kind that has an object in the lobby; set LOBBYGUARD_LIVETEST_ALL_RPCS=1 to run every rule, which takes a rejoin per
// disconnect. Calls that are only dropped need no rejoin and always run.
internal static class RpcCases
{
    private const int FarAway = 1000;

    private static GrabbableObject? item;

    public static IReadOnlyList<Case> Build()
    {
        bool all = Environment.GetEnvironmentVariable("LOBBYGUARD_LIVETEST_ALL_RPCS") == "1";
        List<Case> cases = [];
        HashSet<string> kinds = [];
        foreach ((string key, RpcRule rule) in RpcRules.Table.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            foreach ((string kind, Case c) in For(key, rule))
            {
                if (Target(key) == null && !(Type(key) == typeof(GrabbableObject) && StartOfRound.Instance != null))
                {
                    continue;
                }
                if (c.Kind == Kind.Kick && !all && !kinds.Add(kind))
                {
                    continue;
                }
                cases.Add(c);
            }
        }
        cases.Add(new Case("rpc-vanilla-play", "", Vanilla, Kind.Vanilla));
        return cases;
    }

    private static IEnumerable<(string Kind, Case Case)> For(string key, RpcRule rule)
    {
        string name = $"rpc-{key}";
        if (rule.Never)
        {
            yield return ("never", Kicks($"{name}-never", key, key, (self, _) => Send(key, self, Zeros(key, -1))));
        }
        if (rule.SelfPlayer.Length > 0)
        {
            int i = rule.SelfPlayer[0];
            yield return ("self-player", Kicks($"{name}-other-player", key, key, (self, _) => Send(key, self, Zeros(key, i, Index(self) + 1))));
        }
        if (rule.SelfClient.Length > 0)
        {
            int i = rule.SelfClient[0];
            yield return ("self-client", Kicks($"{name}-other-client", key, key, (self, _) => Send(key, self, Zeros(key, i, self + 1))));
        }
        if (rule.Bounds.Length > 0)
        {
            Bound b = rule.Bounds[0];
            yield return ("bounds", Kicks($"{name}-out-of-bounds", key, key, (self, _) => Send(key, self, Zeros(key, b.Param, b.Max + 1, rule.SelfPlayer, self))));
        }
        if (rule.Purchase != Purchase.None)
        {
            yield return ($"purchase-{rule.Purchase}", Kicks($"{name}-negative-credits", key, key, (self, _) => Send(key, self, Zeros(key, rule.CreditsParam, -1))));
        }
        if (rule.ConnectedClient.Length > 0)
        {
            int i = rule.ConnectedClient[0];
            yield return ("drop", Drops($"{name}-unconnected-client", key, (self, _) => Send(key, self, Zeros(key, i, 99999UL))));
        }
        if (rule.TargetOwnedBySender)
        {
            yield return ("drop", Drops($"{name}-not-the-owner", key, (self, _) => Send(key, self, Zeros(key, -1))));
        }
        if (rule.MaxDistance > 0)
        {
            yield return ("drop", FarCase($"{name}-too-far", key, rule));
        }
        if (rule.RateMax > 0 && key != "StartOfRound.StartGameServerRpc")
        {
            yield return ("drop", Drops($"{name}-too-often", key + " too often", (self, _) =>
            {
                byte[][] calls = Enumerable.Range(0, rule.RateMax + 1).Select(_ => Message(key, Zeros(key, -1))).ToArray();
                Cases.Receive(NetworkManager.Singleton, self, Cases.Batch((ushort)calls.Length, calls.SelectMany(b => b).ToArray()));
            }));
        }
    }

    private static Case Kicks(string name, string key, string reason, Action<ulong, NetworkManager> send) =>
        new(name, reason, (m, c) => send(c, m));

    private static Case Drops(string name, string reason, Action<ulong, NetworkManager> send) =>
        new(name, reason, (m, c) => send(c, m), Kind.Drop);

    // The damage call from the host's own player's reach: the host's player is moved away until the call has been handled.
    private static Case FarCase(string name, string key, RpcRule rule)
    {
        Vector3 home = default;
        PlayerControllerB Victim() => StartOfRound.Instance.allPlayerScripts[0];
        return new Case(name, key, (m, c) =>
        {
            home = Victim().transform.position;
            Victim().transform.position = home + Vector3.right * FarAway;
            Send(key, c, Zeros(key, 2, Index(c), rule.SelfPlayer, c));
        }, Kind.Drop, null, () => Victim().transform.position = home);
    }

    // Normal play: buying an item at its price, a chat line, the ship lights, picking up and putting down an item the
    // player owns. None of it may trip a rule.
    private static void Vanilla(NetworkManager manager, ulong client)
    {
        Terminal terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
        int price = terminal.buyableItemsList[0].creditsWorth;
        int sale = terminal.itemSalesPercentages[0];
        int cost = (int)(price * (sale / 100f));
        GrabbableObject held = Item(manager);
        held.NetworkObject.ChangeOwnership(client);
        Send("Terminal.BuyItemsServerRpc", client, [.. Present(true), .. Ints(1, 0), .. Packed(terminal.groupCredits - cost), .. Packed(0)]);
        Send("ShipLights.SetShipLightsServerRpc", client, [0]);
        Send("ShipLights.SetShipLightsServerRpc", client, [1]);
        Send("GrabbableObject.GrabServerRpc", client, [], held);
        Send("GrabbableObject.DiscardItemRpc", client, [], held);
    }

    private static byte[] Present(bool present) => [(byte)(present ? 1 : 0)];

    private static byte[] Ints(int count, int value)
    {
        using FastBufferWriter writer = new(16, Allocator.Temp);
        writer.WriteValueSafe(new[] { value }.Take(count).ToArray());
        return writer.ToArray();
    }

    private static byte[] Packed(int value)
    {
        using FastBufferWriter writer = new(8, Allocator.Temp);
        BytePacker.WriteValueBitPacked(writer, value);
        return writer.ToArray();
    }

    private static int Index(ulong client) => StartOfRound.Instance.ClientPlayerList[client];

    private static Type? Type(string key)
    {
        string name = key.Split('.')[0];
        return typeof(StartOfRound).Assembly.GetTypes().FirstOrDefault(t => t.Name == name);
    }

    private static NetworkBehaviour? Target(string key) =>
        Type(key) is { } type && typeof(NetworkBehaviour).IsAssignableFrom(type) ? UnityEngine.Object.FindObjectOfType(type) as NetworkBehaviour : null;

    private static GrabbableObject Item(NetworkManager manager)
    {
        if (item != null)
        {
            return item;
        }
        Item prefab = StartOfRound.Instance.allItemsList.itemsList.First(i => i.spawnPrefab != null && i.spawnPrefab.GetComponent<GrabbableObject>() != null);
        GameObject spawned = UnityEngine.Object.Instantiate(prefab.spawnPrefab, StartOfRound.Instance.elevatorTransform);
        item = spawned.GetComponent<GrabbableObject>();
        item.NetworkObject.Spawn();
        return item;
    }

    // The arguments of a call to `key`, every one zero or false except `set`, written the way NGO's generated code does,
    // up to and including the last one a rule reads (`last`, or none for -1). A player index named `own` in a
    // second argument is filled in with the sender's, so only the argument under test is wrong.
    private static byte[] Zeros(string key, int last, object? set = null, int[]? own = null, ulong self = 0)
    {
        MethodInfo method = Rpc(key);
        ParameterInfo[] parameters = method.GetParameters();
        using FastBufferWriter writer = new(256, Allocator.Temp);
        for (int i = 0; i <= last && i < parameters.Length; i++)
        {
            object? value = i == last ? set : own != null && own.Contains(i) ? Index(self) : null;
            Write(writer, parameters[i].ParameterType, value);
        }
        return writer.ToArray();
    }

    private static void Write(FastBufferWriter writer, System.Type type, object? value)
    {
        if (type == typeof(int))
        {
            BytePacker.WriteValueBitPacked(writer, value is int i ? i : 0);
        }
        else if (type == typeof(ulong))
        {
            BytePacker.WriteValueBitPacked(writer, value is ulong u ? u : value is int n ? (ulong)n : 0UL);
        }
        else if (type == typeof(bool))
        {
            writer.WriteValueSafe(value is true);
        }
        else if (type == typeof(float))
        {
            writer.WriteValueSafe(0f);
        }
        else if (type == typeof(Vector3))
        {
            writer.WriteValueSafe(Vector3.zero);
        }
        else if (type == typeof(Quaternion))
        {
            writer.WriteValueSafe(Quaternion.identity);
        }
        else if (type == typeof(NetworkObjectReference))
        {
            writer.WriteValueSafe(new NetworkObjectReference(item != null ? item.NetworkObject : StartOfRound.Instance.NetworkObject));
        }
        else if (type == typeof(int[]))
        {
            writer.WriteValueSafe(true);
            writer.WriteValueSafe(new[] { 0 });
        }
        else
        {
            throw new CaseSkipped($"no writer for {type.Name}");
        }
    }

    // The method a table key names, in the game's own assembly.
    private static MethodInfo Rpc(string key)
    {
        string[] part = key.Split('.');
        System.Type type = Type(key) ?? throw new CaseSkipped($"no type {part[0]}");
        return type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).First(m => m.Name == part[1]);
    }

    private static void Send(string key, ulong client, byte[] arguments, GrabbableObject? on = null) =>
        Cases.Receive(NetworkManager.Singleton, client, Cases.Batch(1, Message(key, arguments, on)));

    private static byte[] Message(string key, byte[] arguments, GrabbableObject? on = null)
    {
        NetworkManager manager = NetworkManager.Singleton;
        NetworkBehaviour behaviour = on ?? Target(key) ?? (Type(key) == typeof(GrabbableObject) ? Item(manager) : throw new CaseSkipped($"no {key.Split('.')[0]} in the lobby"));
        uint id = HandlerId(behaviour.GetType(), Rpc(key));
        using FastBufferWriter body = new(Math.Max(arguments.Length, 1), Allocator.Temp);
        body.WriteBytes(arguments);
        ServerRpcMessage rpc = new()
        {
            Metadata = new RpcMetadata { NetworkObjectId = behaviour.NetworkObjectId, NetworkBehaviourId = behaviour.NetworkBehaviourId, NetworkRpcMethodId = id },
            WriteBuffer = body,
        };
        using FastBufferWriter writer = new(arguments.Length + 64, Allocator.Temp);
        rpc.Serialize(writer, 0);
        return Cases.Message(manager.MessageManager.GetMessageType(typeof(ServerRpcMessage)), writer.ToArray());
    }

    // NGO registers a handler per RPC as __rpc_handler_<id>, and the handler calls the RPC method.
    private static uint HandlerId(System.Type type, MethodInfo rpc)
    {
        foreach ((uint id, NetworkBehaviour.RpcReceiveHandler handler) in NetworkBehaviour.__rpc_func_table[type])
        {
            byte[]? il = handler.Method.GetMethodBody()?.GetILAsByteArray();
            for (int i = 0; il != null && i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 && il[i] != 0x6F)
                {
                    continue;
                }
                try
                {
                    if (handler.Method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == rpc)
                    {
                        return id;
                    }
                }
                catch (Exception e) when (e is ArgumentException or TypeLoadException or System.IO.FileNotFoundException or MemberAccessException)
                {
                }
            }
        }
        throw new CaseSkipped($"{type.Name} has no handler for {rpc.Name}");
    }
}
