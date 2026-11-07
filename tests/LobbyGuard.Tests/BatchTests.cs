using System;
using System.Collections.Generic;
using Xunit;

namespace LobbyGuard.Tests;

public sealed class BatchTests
{
    private static ulong Hash(ArraySegment<byte> body)
    {
        ulong sum = 17;
        foreach (byte b in body)
        {
            sum = sum * 31 + b;
        }
        return sum;
    }

    private const uint SceneEvent = 12;

    private const uint Destroy = 9;

    private static readonly Rules rules = new() { ConnectRequest = ConnectRequest, SceneEvent = SceneEvent, HostOnly = new HashSet<uint> { Destroy } };

    private static string? Check(ArraySegment<byte> packet) => Check(packet, new Client());

    private static string? Check(ArraySegment<byte> packet, Client client) => Batch.Check(packet, Hash, rules, client);

    private const uint ConnectRequest = 7;

    // Two version pairs, a config hash, then connection data that says it holds dataLength bytes and holds 3.
    private static byte[] ConnectionRequest(int dataLength)
    {
        var body = new List<byte>(Packed(4));
        for (uint i = 0; i < 2; i++)
        {
            body.AddRange(BitConverter.GetBytes(0xABC0 + i));
            body.AddRange(Packed(2));
        }
        body.AddRange(BitConverter.GetBytes(0x1234UL));
        body.AddRange(BitConverter.GetBytes(dataLength));
        body.AddRange(new byte[] { 1, 2, 3 });
        return body.ToArray();
    }

    private static ArraySegment<byte> Message(uint type, byte[] body) => Packet(1, Packed(type), Packed((uint)body.Length), body);

    private static byte[] Packed(uint value)
    {
        if (value >= 1u << 29)
        {
            byte[] five = new byte[5];
            five[0] = 5;
            BitConverter.GetBytes(value).CopyTo(five, 1);
            return five;
        }
        int n = value < 1u << 5 ? 1 : value < 1u << 13 ? 2 : value < 1u << 21 ? 3 : 4;
        return BitConverter.GetBytes(value << 3 | (uint)n)[..n];
    }

    private static ArraySegment<byte> Packet(ushort count, params byte[][] parts)
    {
        var body = new List<byte>();
        foreach (byte[] part in parts)
        {
            body.AddRange(part);
        }
        byte[] packet = new byte[16 + body.Count];
        BitConverter.GetBytes((ushort)0x1160).CopyTo(packet, 0);
        BitConverter.GetBytes(count).CopyTo(packet, 2);
        BitConverter.GetBytes(packet.Length).CopyTo(packet, 4);
        BitConverter.GetBytes(Hash(body.ToArray())).CopyTo(packet, 8);
        body.CopyTo(packet, 16);
        return new ArraySegment<byte>(packet);
    }

    [Fact]
    public void PassesWhatNgoWrites()
    {
        Assert.Null(Check(Packet(2, Packed(3), Packed(2), [1, 2], Packed(4000), Packed(0))));
    }

    [Fact]
    public void RejectsBrokenPackets()
    {
        Assert.Equal("message bigger than its packet", Check(Packet(1, Packed(3), Packed(0x80000000))));
        Assert.Equal("message bigger than its packet", Check(Packet(1, Packed(3), Packed(3), [1, 2])));
        Assert.Equal("bad message header", Check(Packet(65535, [0])));
        Assert.Equal("bad message header", Check(Packet(2, Packed(3), Packed(0))));
        Assert.Equal("packet too short", Check(new ArraySegment<byte>(new byte[8])));

        ArraySegment<byte> tampered = Packet(1, Packed(3), Packed(1), [9]);
        tampered.Array![^1] = 8;
        Assert.Equal("packet hash doesn't match", Check(tampered));
        ArraySegment<byte> foreign = Packet(0);
        foreign.Array![0] = 0;
        Assert.Equal("not a Netcode packet", Check(foreign));
    }

    [Fact]
    public void ChecksConnectionData()
    {
        Assert.Null(Check(Message(ConnectRequest, ConnectionRequest(3))));
        Assert.Equal("bad connection request", Check(Message(ConnectRequest, ConnectionRequest(int.MaxValue))));
        Assert.Equal("bad connection request", Check(Message(ConnectRequest, ConnectionRequest(-1))));
        Assert.Null(Check(Message(ConnectRequest + 1, ConnectionRequest(int.MaxValue))));
    }

    [Fact]
    public void AllowsOneJoinRequest()
    {
        ArraySegment<byte> request = Message(ConnectRequest, ConnectionRequest(3));
        var client = new Client();
        Assert.Null(Check(request, client));
        Assert.Equal("asked to join a second time", Check(request, client));
        Assert.Equal("asked to join a second time", Check(request, new Client { Accepted = true }));
    }

    [Fact]
    public void LimitsMessagesBeforeBeingAccepted()
    {
        ArraySegment<byte> early = Message(ConnectRequest + 1, [1]);
        var client = new Client();
        for (int i = 0; i < Limits.MaxEarlyMessages; i++)
        {
            Assert.Null(Check(early, client));
        }
        Assert.Equal("too many messages before being accepted", Check(early, client));
        Assert.Null(Check(early, new Client { Accepted = true }));
    }

    [Fact]
    public void LimitsConnectionData()
    {
        byte[] body = ConnectionRequest(Limits.MaxConnectionData + 1);
        Assert.Equal("bad connection request", Check(Message(ConnectRequest, [.. body, .. new byte[Limits.MaxConnectionData]])));
        Assert.Null(Check(Message(ConnectRequest, [.. ConnectionRequest(Limits.MaxConnectionData), .. new byte[Limits.MaxConnectionData]])));
    }

    [Fact]
    public void AllowsOnlyTheSceneEventsAClientSends()
    {
        var client = new Client { Accepted = true };
        Assert.Null(Check(Message(SceneEvent, [6]), client));
        Assert.Null(Check(Message(SceneEvent, [6]), client));
        Assert.Null(Check(Message(SceneEvent, [7]), client));
        Assert.Null(Check(Message(SceneEvent, [8]), client));
        Assert.Equal("finished joining a second time", Check(Message(SceneEvent, [8]), client));
        Assert.Equal("sent a scene change only the host can send", Check(Message(SceneEvent, [0]), new Client { Accepted = true }));
        Assert.Equal("sent a scene change only the host can send", Check(Message(SceneEvent, [10]), new Client { Accepted = true }));
    }

    [Fact]
    public void ListsTheTypesItCanRead()
    {
        Assert.Equal([3u, 4000u], Batch.Types(Packet(2, Packed(3), Packed(2), [1, 2], Packed(4000), Packed(0))));
        Assert.Equal([3u], Batch.Types(Packet(2, Packed(3), Packed(0), [0])));
        Assert.Empty(Batch.Types(new ArraySegment<byte>(new byte[8])));
    }

    [Fact]
    public void RejectsHostOnlyMessagesAtAnyStage()
    {
        ArraySegment<byte> packet = Message(Destroy, [1]);
        const string why = "sent a message only the host can send";
        Assert.Equal(why, Check(packet, new Client { Accepted = true }));
        Assert.Equal(why, Check(packet, new Client()));
    }
}
