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

    private static Hook? handshake;
    private static Hook? state;
    private static Hook? relay;

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
            copy.ReadHandshakeRequest(out _, out CodecSettings settings);
            return settings;
        }))
        {
            orig(self, source, ref reader);
        }
    }

    private static void State(Orig orig, BroadcastingClientCollection<NfgoConn> self, NfgoConn source, ref PacketReader reader)
    {
        PacketReader copy = reader;
        if (Check(source, () => copy.ReadClientStateHeader().CodecSettings))
        {
            orig(self, source, ref reader);
        }
    }

    private static void Relay(RelayOrig orig, ServerRelay<NfgoConn> self, ref PacketReader reader, bool reliable, NfgoConn source)
    {
        PacketReader copy = reader;
        string? why;
        try
        {
            why = RelayedVoice(self, ref copy, source);
        }
        catch (Exception)
        {
            why = "broken voice packet";
        }
        if (why == null)
        {
            orig(self, ref reader, reliable, source);
        }
        else
        {
            Reject(source, why);
        }
    }

    // Only voice is read here. Everything else goes on as the game would relay it.
    private static string? RelayedVoice(ServerRelay<NfgoConn> self, ref PacketReader reader, NfgoConn source)
    {
        destinations.Clear();
        reader.ReadRelay(destinations, out ArraySegment<byte> data);
        PacketReader inner = new(data);
        if (!inner.ReadPacketHeader(out MessageTypes type) || type != MessageTypes.VoiceData)
        {
            return null;
        }
        inner.ReadUInt32();
        inner.ReadVoicePacketHeader1(out ushort sender);
        inner.ReadVoicePacketHeader2(out _, out _, out ushort channels);
        for (int i = 0; i < channels; i++)
        {
            inner.ReadVoicePacketChannel(out _, out _);
        }
        ArraySegment<byte> frame = inner.ReadByteSegment();
        // Players decode a frame with the settings of the player it names, so it has to name the one who sent it.
        if (!self._peers.TryFindClientByConnection(source, out ClientInfo<NfgoConn>? info) || info == null || info.PlayerId != sender)
        {
            return "voice sent as another player";
        }
        return FrameAllowed((byte)info.CodecSettings.Codec, info.CodecSettings.FrameSize, frame) ? null : "broken voice frame";
    }

    // Reads the settings from a copy of the packet, so the game still reads it from the start.
    private static bool Check(NfgoConn source, Func<CodecSettings> read)
    {
        string why;
        try
        {
            CodecSettings settings = read();
            if (Allowed((byte)settings.Codec, settings.FrameSize, settings.SampleRate))
            {
                return true;
            }
            why = "bad voice codec settings";
        }
        catch (Exception)
        {
            why = "broken voice packet";
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
