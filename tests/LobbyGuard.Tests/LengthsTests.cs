using System;
using System.Reflection;
using Xunit;

namespace LobbyGuard.Tests;

public sealed class LengthsTests
{
    [Fact]
    public void LengthMustFitWhatsLeft()
    {
        Assert.True(Lengths.Fits(3, 4, 12));
        Assert.True(Lengths.Fits(0, 4, 0));
        Assert.False(Lengths.Fits(4, 4, 12));
        Assert.False(Lengths.Fits(-1, 1, 100));
        Assert.False(Lengths.Fits(int.MaxValue, 8, int.MaxValue));
    }

    [Fact]
    public void ATypeThatCantLoadIsSkipped()
    {
        Assert.True(Lengths.Derives(typeof(string), typeof(object)));
        Assert.False(Lengths.Derives(new Unloadable(), typeof(object)));
    }

    // Fails the way Mono does on a type naming an assembly that isn't installed.
    private sealed class Unloadable() : TypeDelegator(typeof(object))
    {
        public override Type UnderlyingSystemType => throw new TypeLoadException("Could not load file or assembly 'Missing'");
    }
}
