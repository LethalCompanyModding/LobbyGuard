using System;
using System.Linq;
using System.Text;
using Xunit;

namespace LobbyGuard.Tests;

public sealed class PacketsTests
{
    private static byte[] U16(int value) => [(byte)(value >> 8), (byte)value];

    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Str(string? value) =>
        value == null ? U16(0) : [.. U16(Encoding.UTF8.GetByteCount(value) + 1), .. Encoding.UTF8.GetBytes(value)];

    private static byte[] Text(byte type, int sender, string? text) => [type, .. U16(sender), .. U16(4), .. Str(text)];

    private static byte[] State(string? name, int id, uint frameSize, params string?[] rooms) =>
        [.. Str(name), .. U16(id), 1, .. U32(frameSize), .. U32(48000), .. U16(rooms.Length), .. rooms.SelectMany(Str)];

    private static byte[] Delta(int peer, string? room) => [1, .. U16(peer), .. Str(room)];

    [Fact]
    public void TextComesFromItsSender()
    {
        Assert.Null(Packets.Text(Text(0, 3, "hi"), 3));
        Assert.Null(Packets.Text(Text(1, 3, null), 3));
        Assert.Equal("voice text sent as another player", Packets.Text(Text(0, 4, "hi"), 3));
        Assert.Equal("voice text to nobody", Packets.Text(Text(2, 3, "hi"), 3));
        Assert.Equal("broken voice packet", Packets.Text(Text(0, 3, "hi").AsSpan(..^1), 3));
        Assert.Equal("broken voice packet", Packets.Text([0, 0, 3], 3));
    }

    [Fact]
    public void StateIsTheSendersOwn()
    {
        Assert.Null(Packets.ClientState(State("me", 2, 960, "Global", "Ship"), 2, "me"));
        Assert.Null(Packets.ClientState(State("me", 2, 960), 2, "me"));
        Assert.Equal("voice state for another player", Packets.ClientState(State("me", 5, 960), 2, "me"));
        Assert.Equal("voice state for another player", Packets.ClientState(State("you", 2, 960), 2, "me"));
        Assert.Equal("bad voice codec settings", Packets.ClientState(State("me", 2, uint.MaxValue), 2, "me"));
        Assert.Equal("broken voice packet", Packets.ClientState(State("me", 2, 960, "Global", null), 2, "me"));
        Assert.Equal("broken voice packet", Packets.ClientState(State("me", 2, 960, "Global").AsSpan(..^1), 2, "me"));
        Assert.Equal("broken voice packet", Packets.ClientState(State(null, 2, 960), 2, "me"));
    }

    [Fact]
    public void RoomChangesAreTheSendersOwn()
    {
        Assert.Null(Packets.DeltaChannelState(Delta(2, "Global"), 2));
        Assert.Equal("voice state for another player", Packets.DeltaChannelState(Delta(7, "Global"), 2));
        Assert.Equal("broken voice packet", Packets.DeltaChannelState(Delta(2, null), 2));
        Assert.Equal("broken voice packet", Packets.DeltaChannelState(Delta(2, "Global").AsSpan(..^2), 2));
    }

    [Fact]
    public void EachPlayerNamedOnce()
    {
        Assert.False(Packets.Repeats([]));
        Assert.False(Packets.Repeats([1, 2, 3]));
        Assert.True(Packets.Repeats([1, 2, 1]));
        Assert.True(Packets.Repeats(Enumerable.Repeat((ushort)4, 255).ToList()));
    }
}
