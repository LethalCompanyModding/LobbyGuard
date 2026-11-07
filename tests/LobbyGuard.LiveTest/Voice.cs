using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Dissonance;
using Dissonance.Audio.Codecs;
using Dissonance.Integrations.Unity_NFGO;
using Dissonance.Networking;
using Dissonance.Networking.Client;
using Unity.Netcode;
using UnityEngine;

namespace LobbyGuard.LiveTest;

// Voice chat packets as Dissonance's own writer makes them, handed to the host's voice server as if the client sent them.
// Only the one field under test is wrong.
internal static class VoiceCases
{
    private static NfgoServer Server => UnityEngine.Object.FindObjectOfType<NfgoCommsNetwork>().Server;

    public static IEnumerable<Case> All()
    {
        yield return new Case("voice-bad-codec", "bad voice codec settings", (m, c) =>
            Send(c, (server, info) => Packet().WriteHandshakeRequest("livetest-bad-codec", new CodecSettings(Codec.Opus, 961, 48000)).Written));
        yield return new Case("voice-state-for-another-player", "voice state for another player", (m, c) =>
            Send(c, (server, info) => Packet().WriteClientState(server.SessionId, info.PlayerName + "x", info.PlayerId, info.CodecSettings, Rooms()).Written));
        yield return new Case("voice-as-another-player", "voice sent as another player", (m, c) =>
            Send(c, (server, info) => Relay(server, info, [info], Voice(server, (ushort)(info.PlayerId + 1), new byte[10]))));
        yield return new Case("voice-oversized-frame", "broken voice frame", (m, c) =>
            Send(c, (server, info) => Relay(server, info, [info], Voice(server, info.PlayerId, new byte[2000]))));
        yield return new Case("voice-to-a-player-twice", "voice sent to a player twice", (m, c) =>
            Send(c, (server, info) => Relay(server, info, [info, info], Voice(server, info.PlayerId, new byte[10]))));
        yield return new Case("voice-text-as-another-player", "voice text sent as another player", (m, c) =>
            Send(c, (server, info) => Relay(server, info, [info],
                Packet().WriteTextPacket(server.SessionId, (ushort)(info.PlayerId + 1), ChannelType.Room, 0, "hi").Written)));
        yield return new Case("voice-host-only-message", "voice message only the host sends", (m, c) =>
            Send(c, (server, info) => Relay(server, info, [info], Packet().WriteRemoveClient(server.SessionId, info.PlayerId).Written)));
    }

    private static PacketWriter Packet() => new(new byte[4096]);

    private static ReadOnlyCollection<string> Rooms() => new(new List<string>());

    private static ArraySegment<byte> Voice(NfgoServer server, ushort sender, byte[] frame) =>
        Packet().WriteVoiceData(server.SessionId, sender, 0, 0, new List<OpenChannel>(), new ArraySegment<byte>(frame)).Written;

    private static ArraySegment<byte> Relay(NfgoServer server, ClientInfo<NfgoConn> self, List<ClientInfo<NfgoConn>> to, ArraySegment<byte> inner) =>
        Packet().WriteRelay(server.SessionId, to, inner, false).Written;

    private static void Send(ulong client, Func<NfgoServer, ClientInfo<NfgoConn>, ArraySegment<byte>> build)
    {
        NfgoServer server = Server;
        NfgoConn from = new(client);
        if (!server._clients.TryFindClientByConnection(from, out ClientInfo<NfgoConn>? info) || info == null)
        {
            throw new InvalidOperationException("the client has not joined voice chat");
        }
        server.NetworkReceivedPacket(from, build(server, info));
    }
}
