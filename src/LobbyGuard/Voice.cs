using System;
using System.Collections.Generic;
using System.Reflection;
using Dissonance;
using Dissonance.Audio.Codecs;
using Dissonance.Integrations.Unity_NFGO;
using Dissonance.Networking;
using Dissonance.Networking.Server;
using MonoMod.RuntimeDetour;
using Unity.Netcode;

namespace LobbyGuard;

// Voice chat sizes its decoder buffers from the codec settings each player sends, and the host passes them on to
// everyone. The game only ever sends Opus at 48 kHz with one of Opus's frame sizes, or Dissonance's fixed Identity
// settings, so the host drops anything else, or anything cut short, and disconnects whoever sent it. The host also
// relays each player's voice to the others without reading it, and a frame the decoder can't read throws on every
// player who hears it, so the host checks each frame against the sender's settings first.
//
// Players also send each other text, and their connection details, through the host. Only voice and text are theirs to
// send, and the host would otherwise pass on the messages only it sends, like one that removes a player.
//
// The hooks sit on the host's void handlers. The readers themselves return structs, and a detour on a struct method
// that returns a struct gets its arguments swapped on Windows.
internal static class Voice
{
    private static readonly int[] opusFrameSizes = { 120, 240, 480, 960, 1920, 2880 };

    // Players copy each frame into a buffer this size, and a bigger one drops their voice connection.
    internal const int MaxFrameBytes = 1024;

    private static readonly List<ushort> destinations = new();

    private delegate void Orig(BroadcastingClientCollection<NfgoConn> self, NfgoConn source, ref PacketReader reader);

    private delegate void RelayOrig(ServerRelay<NfgoConn> self, ref PacketReader reader, bool reliable, NfgoConn source);

    private delegate void ReceiveOrig(NfgoServer self, ulong sender, FastBufferReader stream);

    private delegate void PacketOrig(BaseServer<NfgoServer, NfgoClient, NfgoConn> self, NfgoConn source, ArraySegment<byte> data);

    private static Hook? handshake;
    private static Hook? state;
    private static Hook? relay;
    private static Hook? receive;
    private static Hook? packet;

    public static void Hook()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Type clients = typeof(BroadcastingClientCollection<NfgoConn>);
        handshake = new Hook(clients.GetMethod(nameof(BroadcastingClientCollection<NfgoConn>.ProcessHandshakeRequest), any),
            typeof(Voice).GetMethod(nameof(Handshake), any));
        state = new Hook(clients.GetMethod(nameof(BroadcastingClientCollection<NfgoConn>.ProcessClientState), any),
            typeof(Voice).GetMethod(nameof(State), any));
        relay = new Hook(typeof(ServerRelay<NfgoConn>).GetMethod(nameof(ServerRelay<NfgoConn>.ProcessPacketRelay), any),
            typeof(Voice).GetMethod(nameof(Relay), any));
        receive = new Hook(typeof(NfgoServer).GetMethod(nameof(NfgoServer.NamedMessageHandler), any),
            typeof(Voice).GetMethod(nameof(Receive), any));
        packet = new Hook(typeof(BaseServer<NfgoServer, NfgoClient, NfgoConn>).GetMethod(nameof(NfgoServer.NetworkReceivedPacket), any),
            typeof(Voice).GetMethod(nameof(Packet), any));
    }

    public static bool Allowed(byte codec, uint frameSize, int sampleRate) => codec switch
    {
        (byte)Codec.Opus => sampleRate == 48000 && Array.IndexOf(opusFrameSizes, (int)frameSize) >= 0,
        (byte)Codec.Identity => sampleRate == 44100 && frameSize == 441,
        _ => false,
    };

    public static bool FrameAllowed(byte codec, uint frameSize, ReadOnlySpan<byte> frame) => frame.Length <= MaxFrameBytes && codec switch
    {
        (byte)Codec.Opus => Opus.Samples(frame) == frameSize,
        (byte)Codec.Identity => frame.Length == frameSize * 4,
        _ => false,
    };

    private static void Handshake(Orig orig, BroadcastingClientCollection<NfgoConn> self, NfgoConn source, ref PacketReader reader)
    {
        PacketReader copy = reader;
        if (Check(source, () =>
        {
            copy.ReadHandshakeRequest(out string? name, out CodecSettings settings);
            if (!Allowed((byte)settings.Codec, settings.FrameSize, settings.SampleRate))
            {
                return "bad voice codec settings";
            }
            // The game would hand over that player's place, and the voice that goes with it.
            return self.TryGetClientInfoByName(name, out ClientInfo<NfgoConn>? taken) && taken != null && taken.IsConnected && !taken.Connection.Equals(source)
                ? "joined voice as another player"
                : null;
        }))
        {
            orig(self, source, ref reader);
        }
    }

    private static void State(Orig orig, BroadcastingClientCollection<NfgoConn> self, NfgoConn source, ref PacketReader reader)
    {
        PacketReader copy = reader;
        if (Check(source, () => self.TryFindClientByConnection(source, out ClientInfo<NfgoConn>? info) && info != null
            ? Packets.ClientState(copy.Unread, info.PlayerId, info.PlayerName)
            : "voice state before joining voice"))
        {
            orig(self, source, ref reader);
        }
    }

    // NFGO sizes its buffer from the length a player sends, before it reads any of the packet.
    private static unsafe void Receive(ReceiveOrig orig, NfgoServer self, ulong sender, FastBufferReader stream)
    {
        int remaining = stream.Length - stream.Position - sizeof(uint);
        if (remaining >= 0 && !Lengths.Fits(*(int*)stream.GetUnsafePtrAtCurrentPosition(), 1, remaining))
        {
            Reject(new NfgoConn(sender), "voice packet longer than its message");
            return;
        }
        orig(self, sender, stream);
    }

    // The host passes on a player's room changes, which name the player they're for.
    private static void Packet(PacketOrig orig, BaseServer<NfgoServer, NfgoClient, NfgoConn> self, NfgoConn source, ArraySegment<byte> data)
    {
        PacketReader copy = new(data);
        if (Check(source, () =>
        {
            if (!copy.ReadPacketHeader(out MessageTypes type) || type != MessageTypes.DeltaChannelState)
            {
                return null;
            }
            copy.ReadUInt32();
            return self._clients.TryFindClientByConnection(source, out ClientInfo<NfgoConn>? info) && info != null
                ? Packets.DeltaChannelState(copy.Unread, info.PlayerId)
                : "voice state before joining voice";
        }))
        {
            orig(self, source, data);
        }
    }

    private static void Relay(RelayOrig orig, ServerRelay<NfgoConn> self, ref PacketReader reader, bool reliable, NfgoConn source)
    {
        PacketReader copy = reader;
        ArraySegment<byte> data = default;
        string? why;
        try
        {
            why = Relayed(self, ref copy, source, out data);
        }
        catch (Exception)
        {
            why = "broken voice packet";
        }
        if (why != null)
        {
            Reject(source, why);
            return;
        }
        // The game logs a warning for each player it can't find, and a packet can name 255. A player who just left is
        // still named for a moment.
        if (destinations.RemoveAll(id => !self._peers.TryGetClientInfoById(id, out _)) > 0)
        {
            PacketWriter writer = new(new byte[3 + 2 * destinations.Count + data.Count]);
            writer.Write((byte)destinations.Count);
            foreach (ushort id in destinations)
            {
                writer.Write(id);
            }
            writer.Write(data);
            PacketReader known = new(writer.Written);
            orig(self, ref known, reliable, source);
            return;
        }
        orig(self, ref reader, reliable, source);
    }

    private static string? Relayed(ServerRelay<NfgoConn> self, ref PacketReader reader, NfgoConn source, out ArraySegment<byte> data)
    {
        destinations.Clear();
        reader.ReadRelay(destinations, out data);
        if (Packets.Repeats(destinations))
        {
            return "voice sent to a player twice";
        }
        PacketReader inner = new(data);
        if (!inner.ReadPacketHeader(out MessageTypes type))
        {
            return "broken voice packet";
        }
        // The game drops these itself.
        if (type == MessageTypes.HandshakeP2P)
        {
            return null;
        }
        if (type != MessageTypes.VoiceData && type != MessageTypes.TextData)
        {
            return "voice message only the host sends";
        }
        if (!self._peers.TryFindClientByConnection(source, out ClientInfo<NfgoConn>? info) || info == null)
        {
            return "voice sent before joining voice";
        }
        inner.ReadUInt32();
        if (type == MessageTypes.TextData)
        {
            return Packets.Text(inner.Unread, info.PlayerId);
        }
        inner.ReadVoicePacketHeader1(out ushort sender);
        inner.ReadVoicePacketHeader2(out _, out _, out ushort channels);
        for (int i = 0; i < channels; i++)
        {
            inner.ReadVoicePacketChannel(out _, out _);
        }
        ArraySegment<byte> frame = inner.ReadByteSegment();
        // Players decode a frame with the settings of the player it names, so it has to name the one who sent it.
        if (info.PlayerId != sender)
        {
            return "voice sent as another player";
        }
        return FrameAllowed((byte)info.CodecSettings.Codec, info.CodecSettings.FrameSize, frame) ? null : "broken voice frame";
    }

    // Reads a copy of the packet, so the game still reads it from the start.
    private static bool Check(NfgoConn source, Func<string?> read)
    {
        string? why;
        try
        {
            why = read();
        }
        catch (Exception)
        {
            why = "broken voice packet";
        }
        if (why == null)
        {
            return true;
        }
        Reject(source, why);
        return false;
    }

    private static void Reject(NfgoConn source, string why)
    {
        NetworkManager? manager = NetworkManager.Singleton;
        if (manager != null && manager.IsServer && source.ClientId != manager.LocalClientId)
        {
            Plugin.Kick(manager, source.ClientId, why);
        }
        else
        {
            Plugin.Log.LogWarning($"Dropped a voice packet from this game: {why}");
        }
    }
}
