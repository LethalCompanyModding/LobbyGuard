using System;
using System.Linq;
using Xunit;

namespace LobbyGuard.Tests;

public sealed class OpusTests
{
    private static byte[] Packet(params int[] bytes) => bytes.Select(b => (byte)b).ToArray();

    private static byte[] Zeros(int toc, int length) => [(byte)toc, .. new byte[length]];

    [Fact]
    public void EveryFramingTheDecoderTakes()
    {
        // SILK, Hybrid and CELT at their frame lengths, one frame each.
        Assert.Equal(960, Opus.Samples(Zeros(0x08, 40)));
        Assert.Equal(2880, Opus.Samples(Zeros(0x18, 40)));
        Assert.Equal(480, Opus.Samples(Zeros(0x70, 40)));
        Assert.Equal(120, Opus.Samples(Zeros(0x80, 40)));
        Assert.Equal(960, Opus.Samples(Zeros(0xF8, 0)));
        Assert.Equal(960, Opus.Samples(Zeros(0xF8, 1275)));
        // Two equal frames, then two of any length.
        Assert.Equal(1920, Opus.Samples(Zeros(0xF9, 2550)));
        Assert.Equal(1920, Opus.Samples(Packet(0xFA, 3, 1, 2, 3, 4, 5)));
        Assert.Equal(1920, Opus.Samples([0xFA, 252, 1, .. new byte[256]]));
        // Any number of frames up to 120 ms, equal or not, with padding.
        Assert.Equal(2880, Opus.Samples(Packet(0xFB, 0x03, 1, 2, 3, 4, 5, 6)));
        Assert.Equal(5760, Opus.Samples(Packet(0x83, 48)));
        Assert.Equal(1920, Opus.Samples(Packet(0xFB, 0xC2, 2, 1, 0xAA, 0xBB, 0, 0)));
        Assert.Equal(960, Opus.Samples([0xFB, 0x41, 255, 0, 7, .. new byte[254]]));
    }

    [Fact]
    public void BrokenFramingFails()
    {
        Assert.Equal(-1, Opus.Samples([]));
        Assert.Equal(-1, Opus.Samples(Zeros(0xF8, 1276)));
        Assert.Equal(-1, Opus.Samples(Zeros(0xF9, 3)));
        Assert.Equal(-1, Opus.Samples(Zeros(0xF9, 2552)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFA, 16, 1)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFA, 252)));
        Assert.Equal(-1, Opus.Samples([0xFA, 0, .. new byte[1276]]));
        Assert.Equal(-1, Opus.Samples(Packet(0xFB)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFB, 0x00)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFB, 0x07)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFB, 0x02, 1, 2, 3)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFB, 0x41, 16, 1)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFB, 0x41, 255)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFB, 0x82, 5, 1)));
        Assert.Equal(-1, Opus.Samples(Packet(0xFB, 0x82, 252)));
    }

    [Fact]
    public void RandomBytesNeverThrow()
    {
        Random random = new(1);
        byte[] buffer = new byte[1400];
        for (int i = 0; i < 20000; i++)
        {
            random.NextBytes(buffer);
            int samples = Opus.Samples(buffer.AsSpan(0, random.Next(buffer.Length)));
            Assert.True(samples == -1 || samples is >= 120 and <= 5760);
        }
    }
}
