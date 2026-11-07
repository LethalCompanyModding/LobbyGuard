using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using MonoMod.RuntimeDetour;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Random = System.Random;

namespace LobbyGuard.LiveTest;

// Fuzzing mode, on when LIVETEST_FUZZ_SECONDS is set on the host. It records what the real client sends, mutates it and
// feeds the mutants to the host's receive path. Every kick is fine; an exception LobbyGuard didn't turn into a kick, a slow
// receive or a frozen frame is a failure, logged with the mutant's bytes. The last mutant is kept in a file in case the host dies.
//   LIVETEST_FUZZ_SECONDS  run length, from the first mutant
//   LIVETEST_FUZZ_RATE     mutants per second (default 20)
//   LIVETEST_FUZZ_SEED     random seed (default: the clock; logged)
//   LIVETEST_FUZZ_WARMUP   seconds of recording before the first mutant (default 30)
//   LIVETEST_FUZZ_FREEZE   frame time in seconds that counts as a freeze (default 2)
internal sealed class Fuzzer
{
    private const int HeaderSize = 16;
    private const int MaxPacket = 16384;
    private const int CorpusPerType = 40;
    private const float SettleSeconds = 12;
    private const float RejoinWait = 120;
    private const float SuspectWait = 10;

    private static readonly uint[] Edge = [0, 1, 2, 0x7F, 0x80, 0xFF, 0x100, 1000, 0xFFFF, 0x10000, 0x10000000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF];

    private static readonly byte[][] Evil =
    [
        [0xFF, 0xFF, 0xFF, 0x7F], [0xFF, 0xFF, 0xFF, 0xFF], [0, 0, 0, 0x80], [0xF0, 0xFF, 0xFF, 0xFF, 0x7F], [0xF9, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF],
        [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], [0], [0xFF], [1, 0, 0, 0, 0, 0, 0, 0],
    ];

    private delegate void DataEvent(NetworkConnectionManager self, ulong transportId, ref ArraySegment<byte> payload, float receiveTime);

    private static readonly Dictionary<uint, List<byte[]>> Corpus = new();
    private static readonly HashSet<string> Seen = new();
    private static readonly List<byte[]> Packets = new();
    private static readonly List<string> Exceptions = new();
    private static Hook? hook;
    private static bool injecting;

    private readonly int seconds = Env("LIVETEST_FUZZ_SECONDS", 0);
    private readonly int rate = Env("LIVETEST_FUZZ_RATE", 20);
    private readonly int warmup = Env("LIVETEST_FUZZ_WARMUP", 30);
    private readonly float freeze = Env("LIVETEST_FUZZ_FREEZE", 2);
    private readonly int seed = Env("LIVETEST_FUZZ_SEED", Environment.TickCount);
    private readonly Random rng;
    private readonly string failFile = Path.Combine(Paths.BepInExRootPath, "livetest-fuzz-failures.log");
    private readonly string lastFile = Path.Combine(Paths.BepInExRootPath, "livetest-fuzz-last.hex");
    private readonly Queue<string> ring = new();
    private readonly List<(float At, long Index, string Hex, string Detail)> suspects = new();
    private readonly SortedDictionary<string, int> kicks = new();
    private long sent;
    private int failures;

    public Fuzzer()
    {
        rng = new Random(seed);
    }

    public static bool Enabled => Env("LIVETEST_FUZZ_SECONDS", 0) > 0;

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    // Has to be in place before the client joins, so the join messages are in the corpus too.
    public static void Install()
    {
        hook = new Hook(typeof(NetworkConnectionManager).GetMethod(nameof(NetworkConnectionManager.DataEventHandler)),
            typeof(Fuzzer).GetMethod(nameof(OnData), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
        Application.logMessageReceived += (condition, stack, type) =>
        {
            if (injecting && type == LogType.Exception)
            {
                Exceptions.Add($"{condition}\n{stack}");
            }
        };
    }

    private static void OnData(DataEvent orig, NetworkConnectionManager self, ulong transportId, ref ArraySegment<byte> payload, float receiveTime)
    {
        if (!injecting && payload.Count is > 0 and <= MaxPacket)
        {
            try
            {
                Record(payload.ToArray());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"LIVETEST fuzz could not record a packet: {e.Message}");
            }
        }
        orig(self, transportId, ref payload, receiveTime);
    }

    private static void Record(byte[] packet)
    {
        if (Packets.Count < 200 && Seen.Add(Convert.ToBase64String(packet)))
        {
            Packets.Add(packet);
        }
        foreach ((uint type, byte[] message) in Split(packet))
        {
            if (!Corpus.TryGetValue(type, out List<byte[]>? list))
            {
                Corpus[type] = list = new List<byte[]>();
            }
            if (list.Count < CorpusPerType && Seen.Add(Convert.ToBase64String(message)))
            {
                list.Add(message);
            }
        }
    }

    // Header and payload of each message in a batch, as the sender wrote them.
    private static List<(uint Type, byte[] Message)> Split(byte[] packet)
    {
        List<(uint, byte[])> result = new();
        int position = HeaderSize;
        while (position < packet.Length)
        {
            using FastBufferReader reader = new(new ArraySegment<byte>(packet, position, packet.Length - position), Allocator.Temp);
            ByteUnpacker.ReadValueBitPacked(reader, out uint type);
            ByteUnpacker.ReadValueBitPacked(reader, out uint size);
            long length = reader.Position + size;
            if (length > packet.Length - position)
            {
                break;
            }
            result.Add((type, packet.Skip(position).Take((int)length).ToArray()));
            position += (int)length;
        }
        return result;
    }

    private static int RemoteCount()
    {
        NetworkManager manager = NetworkManager.Singleton;
        return manager is { IsServer: true } ? manager.ConnectedClientsIds.Count(id => id != manager.LocalClientId) : 0;
    }

    private static ulong? Remote()
    {
        NetworkManager manager = NetworkManager.Singleton;
        return manager is { IsServer: true } ? manager.ConnectedClientsIds.Where(id => id != manager.LocalClientId).Cast<ulong?>().FirstOrDefault() : null;
    }

    public IEnumerator Run(List<string> lobbyGuardLines)
    {
        Plugin.Log.LogInfo($"LIVETEST fuzz seed {seed}, {seconds} s, {rate}/s, failures in {failFile}");
        File.WriteAllText(failFile, "");
        float wait = Time.realtimeSinceStartup + 600;
        while (RemoteCount() != 1 && Time.realtimeSinceStartup < wait)
        {
            yield return null;
        }
        if (RemoteCount() != 1)
        {
            Fail("no-client", -1, "", "no client joined");
            Summary(0);
            yield break;
        }
        yield return new WaitForSeconds(warmup);
        Plugin.Log.LogInfo($"LIVETEST fuzz corpus {Packets.Count} packets, messages by type: {string.Join(", ", Corpus.OrderBy(p => p.Key).Select(p => $"{p.Key}x{p.Value.Count}"))}");
        float start = Time.realtimeSinceStartup;
        float end = start + seconds;
        float nextReport = start + 60;
        float credit = 0;
        int lines = lobbyGuardLines.Count;
        while (Time.realtimeSinceStartup < end)
        {
            NetworkManager manager = NetworkManager.Singleton;
            ulong? id = Remote();
            CountKicks(lobbyGuardLines, ref lines);
            if (id == null || RemoteCount() != 1)
            {
                suspects.Clear();
                float giveUp = Time.realtimeSinceStartup + RejoinWait;
                while (RemoteCount() != 1 && Time.realtimeSinceStartup < giveUp)
                {
                    yield return null;
                }
                if (RemoteCount() != 1)
                {
                    Fail("no-rejoin", sent, "", $"the client did not rejoin within {RejoinWait} s");
                    break;
                }
                yield return new WaitForSeconds(SettleSeconds);
                Cooldowns.Clear();
                lines = lobbyGuardLines.Count;
                continue;
            }
            float dt = Time.unscaledDeltaTime;
            if (dt > freeze)
            {
                Fail("frame-freeze", sent, "", $"a frame took {dt:F1} s; the last mutants follow", string.Join("\n", ring));
            }
            credit = Math.Min(credit + rate * dt, rate);
            int count = (int)credit;
            credit -= count;
            for (int i = 0; i < count && manager.ConnectedClients.ContainsKey(id.Value); i++)
            {
                SendOne(manager, id.Value);
            }
            ExpireSuspects();
            if (Time.realtimeSinceStartup > nextReport)
            {
                nextReport += 60;
                Plugin.Log.LogInfo($"LIVETEST fuzz progress {sent} mutants, {kicks.Values.Sum()} kicks, {failures} failures");
            }
            yield return null;
        }
        yield return new WaitForSeconds(SuspectWait + 1);
        CountKicks(lobbyGuardLines, ref lines);
        ExpireSuspects();
        Summary(Time.realtimeSinceStartup - start);
    }

    private void CountKicks(List<string> lobbyGuardLines, ref int from)
    {
        for (; from < lobbyGuardLines.Count; from++)
        {
            string line = lobbyGuardLines[from];
            if (line.StartsWith("Disconnected "))
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                string reason = colon >= 0 ? line.Substring(colon + 2) : line;
                kicks[reason] = kicks.TryGetValue(reason, out int n) ? n + 1 : 1;
            }
        }
    }

    private void SendOne(NetworkManager manager, ulong client)
    {
        byte[] packet = Make(manager);
        string hex = Hex(packet);
        long index = ++sent;
        File.WriteAllText(lastFile, $"{index} {hex}");
        ring.Enqueue(hex.Length > 400 ? hex.Substring(0, 400) : hex);
        while (ring.Count > 20)
        {
            ring.Dequeue();
        }
        int before = Exceptions.Count;
        Exception? thrown = null;
        Stopwatch watch = Stopwatch.StartNew();
        injecting = true;
        try
        {
            Cases.Receive(manager, client, packet);
        }
        catch (Exception e)
        {
            thrown = e;
        }
        finally
        {
            injecting = false;
        }
        watch.Stop();
        if (watch.ElapsedMilliseconds > 1000)
        {
            Fail("slow-receive", index, hex, $"receiving took {watch.ElapsedMilliseconds} ms");
        }
        if (thrown != null)
        {
            suspects.Add((Time.realtimeSinceStartup, index, hex, $"thrown out of the receive path: {thrown}"));
        }
        else if (Exceptions.Count > before)
        {
            suspects.Add((Time.realtimeSinceStartup, index, hex, $"logged: {Exceptions[before]}"));
        }
        Exceptions.Clear();
    }

    // An exception is fine when a kick follows it; one the client survives for SuspectWait seconds is a failure.
    private void ExpireSuspects()
    {
        foreach (var suspect in suspects.Where(s => Time.realtimeSinceStartup - s.At > SuspectWait).ToList())
        {
            suspects.Remove(suspect);
            Fail("exception-without-kick", suspect.Index, suspect.Hex, suspect.Detail);
        }
    }

    private void Fail(string kind, long index, string hex, string detail, string? extra = null)
    {
        failures++;
        string first = detail.Split('\n')[0];
        File.AppendAllText(failFile, $"FAIL {kind} seed={seed} mutant={index}\n{detail}\n{extra}{(extra != null ? "\n" : "")}hex={hex}\n\n");
        Plugin.Log.LogWarning($"LIVETEST fuzz FAIL {kind} mutant {index} seed {seed}: {first} hex={(hex.Length > 2000 ? hex.Substring(0, 2000) + "..." : hex)}");
    }

    private void Summary(float elapsed)
    {
        foreach (KeyValuePair<string, int> kick in kicks)
        {
            Plugin.Log.LogInfo($"LIVETEST fuzz kicks {kick.Value}: {kick.Key}");
        }
        Plugin.Log.LogInfo($"LIVETEST summary fuzz {sent} mutants in {elapsed:F0} s ({(elapsed > 0 ? sent / elapsed : 0):F1}/s), {kicks.Values.Sum()} kicks, seed {seed}, {failures} FAIL");
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    private byte[] Make(NetworkManager manager)
    {
        int roll = rng.Next(100);
        if (Corpus.Count == 0 || roll >= 45 && roll < 70)
        {
            return Cap(Structured(manager));
        }
        if (roll < 45)
        {
            return Cap(FromCorpus());
        }
        if (roll < 85 && Packets.Count > 0)
        {
            return Cap(Mutate(Packets[rng.Next(Packets.Count)]));
        }
        return Cap(HeaderEdit(Cases.Batch((ushort)1, FromCorpusMessages(1).SelectMany(m => m).ToArray())));
    }

    private static byte[] Cap(byte[] packet) => packet.Length > MaxPacket ? packet.Take(MaxPacket).ToArray() : packet;

    private IEnumerable<byte[]> FromCorpusMessages(int count)
    {
        List<byte[]> all = Corpus.Values.SelectMany(l => l).ToList();
        for (int i = 0; i < count && all.Count > 0; i++)
        {
            yield return all[rng.Next(all.Count)];
        }
    }

    // Recorded messages with a few byte mutations each, in a batch with a valid hash so they reach the message handlers.
    private byte[] FromCorpus()
    {
        List<byte[]> messages = FromCorpusMessages(rng.Next(1, 5)).Select(MutateMessage).ToList();
        if (messages.Count > 0 && rng.Next(8) == 0)
        {
            messages.Add(messages[rng.Next(messages.Count)]);
        }
        ushort count = (ushort)messages.Count;
        if (rng.Next(10) == 0)
        {
            count = (ushort)Pick(Edge);
        }
        return Cases.Batch(count, messages.SelectMany(m => m).ToArray());
    }

    private byte[] MutateMessage(byte[] message)
    {
        byte[] result = message;
        for (int n = rng.Next(1, 4); n-- > 0;)
        {
            result = rng.Next(10) switch
            {
                0 => SwapType(result),
                1 => EditSize(result),
                _ => ByteMutation(result),
            };
        }
        return result;
    }

    // A recorded packet with its bytes mutated and the hash made valid again (most of the time).
    private byte[] Mutate(byte[] packet)
    {
        byte[] body = packet.Length > HeaderSize ? packet.Skip(HeaderSize).ToArray() : [];
        for (int n = rng.Next(1, 4); n-- > 0;)
        {
            body = ByteMutation(body);
        }
        byte[] result = Cases.Batch(BitConverter.ToUInt16(packet, 2), body);
        if (rng.Next(5) == 0)
        {
            result = HeaderEdit(result);
        }
        return result;
    }

    // Magic, count and size are edited after the hash was made; the hash only covers the body, so it stays valid.
    private byte[] HeaderEdit(byte[] packet)
    {
        byte[] result = (byte[])packet.Clone();
        switch (rng.Next(3))
        {
            case 0:
                BitConverter.GetBytes((ushort)Pick(Edge)).CopyTo(result, 2);
                break;
            case 1:
                BitConverter.GetBytes((int)Pick(Edge)).CopyTo(result, 4);
                break;
            default:
                result[rng.Next(HeaderSize)] ^= (byte)(1 << rng.Next(8));
                break;
        }
        return result;
    }

    private byte[] ByteMutation(byte[] data)
    {
        if (data.Length == 0)
        {
            return RandomBytes(rng.Next(1, 9));
        }
        int at = rng.Next(data.Length);
        switch (rng.Next(7))
        {
            case 0:
                byte[] flipped = (byte[])data.Clone();
                flipped[at] ^= (byte)(1 << rng.Next(8));
                return flipped;
            case 1:
                return data.Take(at).Concat(RandomBytes(rng.Next(1, 9))).Concat(data.Skip(at)).ToArray();
            case 2:
                return data.Take(at).Concat(data.Skip(at + rng.Next(1, 9))).ToArray();
            case 3:
            case 4:
                // Lengths are packed ints or little-endian ints, so overwrite with values that claim too much.
                byte[] evil = Evil[rng.Next(Evil.Length)];
                return data.Take(at).Concat(evil).Concat(data.Skip(at + evil.Length)).ToArray();
            case 5:
                return data.Take(at).ToArray();
            default:
                int times = rng.Next(2, 50);
                int length = Math.Min(data.Length - at, rng.Next(1, 64));
                return data.Take(at + length).Concat(Enumerable.Repeat(data.Skip(at).Take(length), times).SelectMany(x => x)).Concat(data.Skip(at + length)).ToArray();
        }
    }

    private byte[] SwapType(byte[] message)
    {
        (uint _, uint size, byte[] payload) = Parse(message);
        return Cases.Header(Pick(Corpus.Count > 0 && rng.Next(2) == 0 ? Corpus.Keys.ToArray() : [(uint)rng.Next(0, 40)]), size).Concat(payload).ToArray();
    }

    private byte[] EditSize(byte[] message)
    {
        (uint type, uint size, byte[] payload) = Parse(message);
        uint edited = rng.Next(3) == 0 ? size + 1 : rng.Next(3) == 0 ? (uint)Math.Max(0, (int)size - 1) : Pick(Edge);
        return Cases.Header(type, edited).Concat(payload).ToArray();
    }

    private static (uint Type, uint Size, byte[] Payload) Parse(byte[] message)
    {
        using FastBufferReader reader = new(new ArraySegment<byte>(message), Allocator.Temp);
        ByteUnpacker.ReadValueBitPacked(reader, out uint type);
        ByteUnpacker.ReadValueBitPacked(reader, out uint size);
        return (type, size, message.Skip(reader.Position).ToArray());
    }

    // Messages built from NGO's own types: a ServerRpc to a spawned object's real method id, or any message type id with
    // field-shaped payloads.
    private byte[] Structured(NetworkManager manager)
    {
        List<byte[]> messages = new();
        for (int n = rng.Next(1, 4); n-- > 0;)
        {
            uint types = manager.MessageManager.MessageHandlerCount;
            messages.Add(rng.Next(2) == 0 ? ServerRpc(manager) : Cases.Message(rng.Next(12) == 0 ? (uint)rng.Next(0, 300) : (uint)rng.Next(0, (int)types), Fields()));
        }
        return Cases.Batch((ushort)messages.Count, messages.SelectMany(m => m).ToArray());
    }

    private byte[] ServerRpc(NetworkManager manager)
    {
        NetworkObject[] objects = manager.SpawnManager.SpawnedObjects.Values.ToArray();
        NetworkObject? target = objects.Length > 0 ? objects[rng.Next(objects.Length)] : null;
        ulong objectId = target != null && rng.Next(10) != 0 ? target.NetworkObjectId : Pick(Edge);
        uint behaviour = target != null && target.ChildNetworkBehaviours.Count > 0 && rng.Next(10) != 0 ? (uint)rng.Next(target.ChildNetworkBehaviours.Count) : Pick(Edge);
        uint method = Pick(Edge);
        if (target != null && target.ChildNetworkBehaviours.Count > behaviour && NetworkBehaviour.__rpc_func_table.TryGetValue(target.ChildNetworkBehaviours[(int)behaviour].GetType(), out var table) && table.Count > 0 && rng.Next(10) != 0)
        {
            method = table.Keys.ElementAt(rng.Next(table.Count));
        }
        byte[] arguments = Fields();
        using FastBufferWriter body = new(Math.Max(arguments.Length, 1), Allocator.Temp);
        body.WriteBytes(arguments);
        ServerRpcMessage rpc = new()
        {
            Metadata = new RpcMetadata { NetworkObjectId = objectId, NetworkBehaviourId = (ushort)behaviour, NetworkRpcMethodId = method },
            WriteBuffer = body,
        };
        using FastBufferWriter writer = new(arguments.Length + 64, Allocator.Temp);
        rpc.Serialize(writer, 0);
        return Cases.Message(manager.MessageManager.GetMessageType(typeof(ServerRpcMessage)), writer.ToArray());
    }

    // A run of fields a handler might read: plain and packed ints, strings and arrays with a length in front.
    private byte[] Fields()
    {
        List<byte> bytes = new();
        for (int n = rng.Next(0, 10); n-- > 0;)
        {
            switch (rng.Next(7))
            {
                case 0:
                    bytes.Add((byte)rng.Next(256));
                    break;
                case 1:
                    bytes.AddRange(BitConverter.GetBytes((ushort)Pick(Edge)));
                    break;
                case 2:
                    bytes.AddRange(BitConverter.GetBytes(Pick(Edge)));
                    break;
                case 3:
                    bytes.AddRange(BitConverter.GetBytes((ulong)Pick(Edge) << (rng.Next(2) * 32) | Pick(Edge)));
                    break;
                case 4:
                    bytes.AddRange(Cases.Packed(Pick(Edge)));
                    break;
                case 5:
                    bytes.AddRange(Cases.Packed(rng.Next(3) == 0 ? Pick(Edge) : (uint)rng.Next(0, 20)));
                    bytes.AddRange(RandomBytes(rng.Next(0, 20)));
                    break;
                default:
                    bytes.AddRange(RandomBytes(rng.Next(0, 65)));
                    break;
            }
        }
        return bytes.ToArray();
    }

    private uint Pick(uint[] values) => values[rng.Next(values.Length)];

    private byte[] RandomBytes(int count)
    {
        byte[] bytes = new byte[count];
        rng.NextBytes(bytes);
        return bytes;
    }
}
