using System;
using System.Collections.Generic;
using System.Reflection;
using Dissonance.Audio.Codecs;
using Dissonance.Audio.Codecs.Identity;
using Dissonance.Audio.Codecs.Opus;
using Dissonance.Audio.Playback;
using Dissonance.Networking;
using Dissonance.Networking.Client;
using MonoMod.RuntimeDetour;

namespace LobbyGuard;

// A host without LobbyGuard passes on voice frames the decoder can't read. Decoding runs on Unity's audio thread and
// nothing there catches the error, so a broken frame plays as silence instead.
internal static class Playback
{
    private delegate int OpusOrig(OpusDecoder self, EncodedBuffer input, ArraySegment<float> output);

    private delegate int IdentityOrig(IdentityDecoder self, EncodedBuffer input, ArraySegment<float> output);

    private delegate void PrepareOrig(BufferedDecoder self, SessionContext context);

    private delegate void ReceiveOrig(PeerVoiceReceiver self, ref PacketReader reader, DateTime utcNow);

    private static readonly Dictionary<IVoiceDecoder, string> speakers = new();
    private static readonly HashSet<string> logged = new();

    private static Hook? opus;
    private static Hook? identity;
    private static Hook? prepare;
    private static Hook? receive;

    public static void Hook()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        opus = new Hook(typeof(OpusDecoder).GetMethod(nameof(OpusDecoder.Decode), any),
            typeof(Playback).GetMethod(nameof(DecodeOpus), any));
        identity = new Hook(typeof(IdentityDecoder).GetMethod(nameof(IdentityDecoder.Decode), any),
            typeof(Playback).GetMethod(nameof(DecodeIdentity), any));
        prepare = new Hook(typeof(BufferedDecoder).GetMethod(nameof(BufferedDecoder.Prepare), any),
            typeof(Playback).GetMethod(nameof(Prepare), any));
        receive = new Hook(typeof(PeerVoiceReceiver).GetMethod(nameof(PeerVoiceReceiver.ReceivePacket), any),
            typeof(Playback).GetMethod(nameof(Receive), any));
    }

    // Dissonance needs exactly a frame's worth of samples back, and a valid packet of another length throws as well.
    private static int DecodeOpus(OpusOrig orig, OpusDecoder self, EncodedBuffer input, ArraySegment<float> output)
    {
        try
        {
            int samples = orig(self, input, output);
            if (samples == output.Count)
            {
                return samples;
            }
        }
        catch (OpusNative.OpusException)
        {
        }
        return Silence(self, output);
    }

    private static int DecodeIdentity(IdentityOrig orig, IdentityDecoder self, EncodedBuffer input, ArraySegment<float> output)
    {
        if (input.Encoded is { } frame && !input.PacketLost && frame.Count != output.Count * 4)
        {
            return Silence(self, output);
        }
        return orig(self, input, output);
    }

    // Names the decoder's speaker for the log, since the decoder doesn't know who it plays.
    private static void Prepare(PrepareOrig orig, BufferedDecoder self, SessionContext context)
    {
        lock (speakers)
        {
            speakers[self._decoder] = context.PlayerName;
        }
        orig(self, context);
    }

    private static void Receive(ReceiveOrig orig, PeerVoiceReceiver self, ref PacketReader reader, DateTime utcNow)
    {
        PacketReader copy = reader;
        int bytes;
        try
        {
            copy.ReadVoicePacketHeader2(out _, out _, out ushort channels);
            for (int i = 0; i < channels; i++)
            {
                copy.ReadVoicePacketChannel(out _, out _);
            }
            bytes = copy.ReadByteSegment().Count;
        }
        catch (Exception)
        {
            bytes = 0;
        }
        if (bytes > Voice.MaxFrameBytes)
        {
            Warn(self.Name);
            return;
        }
        orig(self, ref reader, utcNow);
    }

    private static int Silence(IVoiceDecoder decoder, ArraySegment<float> output)
    {
        Array.Clear(output.Array!, output.Offset, output.Count);
        string? name;
        lock (speakers)
        {
            speakers.TryGetValue(decoder, out name);
        }
        Warn(name ?? "a player");
        return output.Count;
    }

    private static void Warn(string name)
    {
        lock (logged)
        {
            if (!logged.Add(name))
            {
                return;
            }
        }
        Plugin.Log.LogWarning($"Muted a broken voice frame from {name}. Their voice may cut out.");
    }
}
