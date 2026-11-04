using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
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

    private static readonly Dictionary<ulong, (float Start, int Packets, int Messages)> rates = new();
    private static readonly Dictionary<ulong, Joining> joining = new();
    private static readonly HashSet<uint> hostOnly = [];
    private static NetworkMessageManager? hostOnlyFor;

    private Hook? hook;
    private Hook? messageHook;

    private delegate void DataEvent(NetworkConnectionManager self, ulong transportId, ref ArraySegment<byte> payload, float receiveTime);

    private delegate void HandleMessage(NetworkMessageManager self, in NetworkMessageHeader header, FastBufferReader reader, ulong senderId, float timestamp, int serializedHeaderSize);

    private void Awake()
    {
        Log = Logger;
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        hook = new Hook(typeof(NetworkConnectionManager).GetMethod(nameof(NetworkConnectionManager.DataEventHandler), any),
            typeof(Plugin).GetMethod(nameof(OnData), any));
        messageHook = new Hook(typeof(NetworkMessageManager).GetMethod(nameof(NetworkMessageManager.HandleMessage), any),
            typeof(Plugin).GetMethod(nameof(OnMessage), any));
        Lengths.HookAll();
        Approval.Hook();
        Voice.Hook();
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
        uint connectRequest = self.MessageManager.GetMessageType(typeof(ConnectionRequestMessage));
        string? why = Batch.Check(payload, Hash, connectRequest, self.NetworkManager.NetworkConfig.ConnectionApproval, JoiningState(self, clientId), HostOnly(self.MessageManager)) ??
            (Flooding(transportId, BitConverter.ToUInt16(payload.Array!, payload.Offset + 2)) ? "too many packets or messages" : null);
        if (why == null)
        {
            orig(self, transportId, ref payload, receiveTime);
            return;
        }
        rates.Remove(transportId);
        joining.Remove(clientId);
        Kick(self.NetworkManager, clientId, why);
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
        orig(self, in header, reader, senderId, timestamp, serializedHeaderSize);
    }

    private static Joining? JoiningState(NetworkConnectionManager self, ulong clientId)
    {
        if (!self.PendingClients.ContainsKey(clientId))
        {
            joining.Remove(clientId);
            return null;
        }
        if (!joining.TryGetValue(clientId, out Joining? state))
        {
            joining[clientId] = state = new Joining();
        }
        return state;
    }

    private static HashSet<uint> HostOnly(NetworkMessageManager messages)
    {
        if (hostOnlyFor != messages)
        {
            hostOnlyFor = messages;
            hostOnly.Clear();
            foreach (Type type in hostOnlyMessages)
            {
                if (messages.m_MessageTypes.TryGetValue(type, out uint id))
                {
                    hostOnly.Add(id);
                }
            }
        }
        return hostOnly;
    }

    // Names the player by SteamID in online games, so the host can block them. LAN games only have Netcode's number.
    internal static void Kick(NetworkManager manager, ulong clientId, string why)
    {
        ulong steamId = SteamIdOf(manager, clientId);
        Log.LogWarning($"Disconnected {(steamId != 0 ? $"Steam user {steamId}" : $"client {clientId}")}: {why}");
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
        float now = Time.realtimeSinceStartup;
        (float start, int packets, int total) = rates.TryGetValue(transportId, out (float Start, int Packets, int Messages) rate) &&
            now - rate.Start < 1 ? rate : (now, 0, 0);
        rates[transportId] = (start, ++packets, total += messages);
        return packets > MaxPacketsPerSecond || total > MaxMessagesPerSecond;
    }
}
