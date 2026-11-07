using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;
using Netcode.Transports.Facepunch;
using Steamworks;
using Steamworks.Data;
using Unity.Netcode;
using UnityEngine;

namespace LobbyGuard;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInProcess("Lethal Company.exe")]
public class Plugin : BaseUnityPlugin
{
    // A player's messages shouldn't cause errors at all. This leaves room for a mod that's broken for them.
    private const int MaxErrorsPerSecond = 100;

    // A joining client's RPCs can wait a moment for their objects. NGO keeps a copy of each one, and clearing them out
    // takes stack space for each object they name.
    private const int MaxDeferredPerSecond = 500;

    // Netcode sends a client's packets once per channel per frame, so this leaves room for a very high frame rate.
    private const int MaxPacketsPerSecond = 2000;

    // A packet can hold thousands of tiny messages, and NGO logs an error for each one it can't use.
    private const int MaxMessagesPerSecond = 20000;

    internal static ManualLogSource Log { get; private set; } = null!;

    // Only the host sends these, but NGO runs them if a client does, because the host is a client too.
    private static readonly Type[] hostOnlyMessages =
    [
        typeof(AnticipationCounterSyncPongMessage), typeof(ChangeOwnershipMessage), typeof(ClientConnectedMessage),
        typeof(ClientDisconnectedMessage), typeof(ClientRpcMessage), typeof(ConnectionApprovedMessage),
        typeof(CreateObjectMessage), typeof(DestroyObjectMessage), typeof(DisconnectReasonMessage),
        typeof(ParentSyncMessage), typeof(TimeSyncMessage),
    ];

    private static readonly Rates rates = new();
    private static ConfigEntry<bool>? logPeakRates;
    private static readonly Dictionary<ulong, Client> clients = new();
    private static readonly Dictionary<ulong, (float Start, int Count)> errors = new();
    private static readonly Dictionary<ulong, (float Start, int Count)> deferred = new();
    private static Rules? rules;
    private static NetworkMessageManager? rulesFor;

    // Who sent the message being handled, for the errors it causes. 0 is the host.
    internal static ulong handling;
    private static string? lastError;

    private Hook? hook;
    private Hook? messageHook;
    private Hook? deferHook;
    private Hook? disconnectHook;

    private delegate void DataEvent(NetworkConnectionManager self, ulong transportId, ref ArraySegment<byte> payload, float receiveTime);

    private delegate void HandleMessage(NetworkMessageManager self, in NetworkMessageHeader header, FastBufferReader reader, ulong senderId, float timestamp, int serializedHeaderSize);

    private delegate void ClientGone(NetworkConnectionManager self, ulong clientId);

    private delegate void DeferMessage(DeferredMessageManager self, IDeferredNetworkMessageManager.TriggerType trigger, ulong key, FastBufferReader reader, ref NetworkContext context);

    private void Awake()
    {
        Log = Logger;
        logPeakRates = Config.Bind("Debug", "LogPeakRates", false,
            "Log each player's busiest second so far, in packets and messages, to set the flood limits from real lobbies.");
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        hook = new Hook(typeof(NetworkConnectionManager).GetMethod(nameof(NetworkConnectionManager.DataEventHandler), any),
            typeof(Plugin).GetMethod(nameof(OnData), any));
        messageHook = new Hook(typeof(NetworkMessageManager).GetMethod(nameof(NetworkMessageManager.HandleMessage), any),
            typeof(Plugin).GetMethod(nameof(OnMessage), any));
        deferHook = new Hook(typeof(DeferredMessageManager).GetMethod(nameof(DeferredMessageManager.DeferMessage), any),
            typeof(Plugin).GetMethod(nameof(OnDefer), any));
        disconnectHook = new Hook(typeof(NetworkConnectionManager).GetMethod(nameof(NetworkConnectionManager.OnClientDisconnectFromServer), any),
            typeof(Plugin).GetMethod(nameof(OnClientGone), any));
        Application.logMessageReceived += OnLog;
        Rpcs.Hook();
        Lengths.HookAll();
        Approval.Hook();
        Voice.Hook();
        Playback.Hook();
        Vanilla.Hook();
        Log.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} loaded");
    }

    private static void OnData(DataEvent orig, NetworkConnectionManager self, ulong transportId, ref ArraySegment<byte> payload, float receiveTime)
    {
        if (!self.LocalClient.IsServer)
        {
            orig(self, transportId, ref payload, receiveTime);
            return;
        }
        // NGO takes data from a transport id it doesn't know as the host's own. A kicked client's leftover packets
        // arrive that way.
        if (!self.TransportIdToClientIdMap.TryGetValue(transportId, out ulong clientId))
        {
            return;
        }
        string? why = Batch.Check(payload, Hash, RulesFor(self), ClientState(self, clientId)) ??
            (Flooding(transportId, BitConverter.ToUInt16(payload.Array!, payload.Offset + 2)) ? "too many packets or messages" : null);
        if (why == null)
        {
            orig(self, transportId, ref payload, receiveTime);
            return;
        }
        rates.Remove(transportId);
        clients.Remove(clientId);
        Kick(self.NetworkManager, clientId, $"{why} ({Contents(self.MessageManager, payload)})");
    }

    // What the packet held, so the log shows what a player tried. A bad packet can name any number of types.
    private static string Contents(NetworkMessageManager messages, ArraySegment<byte> payload)
    {
        Type[] names = messages.m_ReverseTypeMap;
        return string.Join(", ", Batch.Types(payload).GroupBy(type => type).Take(10)
            .Select(g => $"{(g.Key < names.Length ? names[g.Key]?.Name : null) ?? $"type {g.Key}"} x{g.Count()}"));
    }

    // A kicked client's queued messages still run, and NGO doesn't check them. A queued join request would add it back.
    private static void OnMessage(HandleMessage orig, NetworkMessageManager self, in NetworkMessageHeader header, FastBufferReader reader, ulong senderId, float timestamp, int serializedHeaderSize)
    {
        if (senderId != NetworkManager.ServerClientId && self.m_Owner is NetworkManager { IsServer: true } manager &&
            !manager.ConnectionManager.PendingClients.ContainsKey(senderId) && !manager.ConnectionManager.ConnectedClients.ContainsKey(senderId))
        {
            reader.Dispose();
            return;
        }
        ulong outer = handling;
        handling = senderId;
        lastError = null;
        try
        {
            orig(self, in header, reader, senderId, timestamp, serializedHeaderSize);
        }
        finally
        {
            handling = outer;
        }
        if (lastError != null && Over(errors, senderId, MaxErrorsPerSecond) && self.m_Owner is NetworkManager { IsServer: true } host)
        {
            errors.Remove(senderId);
            Kick(host, senderId, $"too many errors from their messages, the last: {lastError}");
        }
    }

    // Client numbers can come around again, and the next player with this one starts clean.
    private static void OnClientGone(ClientGone orig, NetworkConnectionManager self, ulong clientId)
    {
        clients.Remove(clientId);
        errors.Remove(clientId);
        deferred.Remove(clientId);
        orig(self, clientId);
    }

    private static void OnLog(string message, string stackTrace, LogType type)
    {
        if (handling != NetworkManager.ServerClientId && type is LogType.Error or LogType.Exception)
        {
            lastError = message.Length > 200 ? message.Substring(0, 200) : message;
        }
    }

    private static void OnDefer(DeferMessage orig, DeferredMessageManager self, IDeferredNetworkMessageManager.TriggerType trigger, ulong key, FastBufferReader reader, ref NetworkContext context)
    {
        if (context.SenderId != NetworkManager.ServerClientId && context.SystemOwner is NetworkManager { IsServer: true } manager &&
            Over(deferred, context.SenderId, MaxDeferredPerSecond))
        {
            deferred.Remove(context.SenderId);
            Kick(manager, context.SenderId, "too many messages for objects that don't exist");
            return;
        }
        orig(self, trigger, key, reader, ref context);
    }

    private static bool Over(Dictionary<ulong, (float Start, int Count)> counts, ulong clientId, int limit)
    {
        float now = Time.realtimeSinceStartup;
        (float start, int count) = counts.TryGetValue(clientId, out (float Start, int Count) seen) && now - seen.Start < 1 ? seen : (now, 0);
        counts[clientId] = (start, ++count);
        return count > limit;
    }

    private static Client ClientState(NetworkConnectionManager self, ulong clientId)
    {
        if (!clients.TryGetValue(clientId, out Client? client))
        {
            clients[clientId] = client = new Client();
        }
        client.Accepted = !self.PendingClients.ContainsKey(clientId);
        return client;
    }

    private static Rules RulesFor(NetworkConnectionManager self)
    {
        NetworkMessageManager messages = self.MessageManager;
        if (rules == null || rulesFor != messages)
        {
            rulesFor = messages;
            rules = new Rules
            {
                ConnectRequest = messages.GetMessageType(typeof(ConnectionRequestMessage)),
                SceneEvent = messages.GetMessageType(typeof(SceneEventMessage)),
                Approval = self.NetworkManager.NetworkConfig.ConnectionApproval,
            };
            foreach (Type type in hostOnlyMessages)
            {
                if (messages.m_MessageTypes.TryGetValue(type, out uint id))
                {
                    rules.HostOnly.Add(id);
                }
            }
        }
        return rules;
    }

    // Names the player by SteamID in online games, so the host can block them. LAN games only have Netcode's number.
    internal static void Kick(NetworkManager manager, ulong clientId, string why)
    {
        ulong steamId = SteamIdOf(manager, clientId);
        Log.LogWarning($"Disconnected {(steamId != 0 ? $"Steam user {steamId}" : $"client {clientId}")}: {why}");
        if (steamId != 0)
        {
            Approval.Kicked(steamId);
        }
        manager.DisconnectClient(clientId);
    }

    // The SteamID Steam proved for a client's connection, or 0 off Steam. Not the transport's Client.steamId: Facepunch's
    // ConnectionInfo.Identity returns the remote address, so that is 0 for every client.
    internal static ulong SteamIdOf(NetworkManager manager, ulong clientId)
    {
        ConnectionInfo info = default;
        return manager.NetworkConfig.NetworkTransport is FacepunchTransport steam &&
            steam.connectedClients.TryGetValue(manager.ConnectionManager.ClientIdToTransportId(clientId), out FacepunchTransport.Client player) &&
            SteamNetworkingSockets.Internal.GetConnectionInfo(player.connection, ref info) && info.identity.IsSteamId
            ? info.identity.SteamId.Value
            : 0;
    }

    private static unsafe ulong Hash(ArraySegment<byte> body)
    {
        fixed (byte* p = body.Array)
        {
            return XXHash.Hash64(p + body.Offset, body.Count);
        }
    }

    private static bool Flooding(ulong transportId, int messages)
    {
        (int packets, int total) = rates.Add(transportId, messages, Time.realtimeSinceStartup, out (int Packets, int Messages)? peak);
        if (peak is { } busiest && logPeakRates?.Value == true)
        {
            Log.LogInfo($"Busiest second from transport {transportId} so far: {busiest.Packets} packets, {busiest.Messages} messages");
        }
        return packets > MaxPacketsPerSecond || total > MaxMessagesPerSecond;
    }
}
