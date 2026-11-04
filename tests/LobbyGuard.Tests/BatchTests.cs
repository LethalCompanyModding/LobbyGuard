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

    private static string? Check(ArraySegment<byte> packet) => Batch.Check(packet, Hash, ConnectRequest, joining: new Joining());

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
        var state = new Joining();
        Assert.Null(Batch.Check(request, Hash, ConnectRequest, joining: state));
        Assert.Equal("asked to join a second time", Batch.Check(request, Hash, ConnectRequest, joining: state));
        Assert.Equal("asked to join a second time", Batch.Check(request, Hash, ConnectRequest));
    }

    [Fact]
    public void LimitsMessagesBeforeBeingAccepted()
    {
        ArraySegment<byte> early = Message(ConnectRequest + 1, [1]);
        var state = new Joining();
        for (int i = 0; i < Batch.MaxEarlyMessages; i++)
        {
            Assert.Null(Batch.Check(early, Hash, ConnectRequest, joining: state));
        }
        Assert.Equal("too many messages before being accepted", Batch.Check(early, Hash, ConnectRequest, joining: state));
        Assert.Null(Batch.Check(early, Hash, ConnectRequest));
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
        const uint destroy = 9;
        var hostOnly = new HashSet<uint> { destroy };
        ArraySegment<byte> packet = Message(destroy, [1]);
        const string why = "sent a message only the host can send";
        Assert.Equal(why, Batch.Check(packet, Hash, ConnectRequest, hostOnly: hostOnly));
        Assert.Equal(why, Batch.Check(packet, Hash, ConnectRequest, joining: new Joining(), hostOnly: hostOnly));
    }
}
