using System.IO;
using BepInEx.Configuration;
using Xunit;

namespace LobbyGuard.Tests;

public sealed class LimitsTests
{
    [Fact]
    public void DefaultsAreTheShippedLimits()
    {
        Assert.Equal(100, Limits.MaxErrorsPerSecond);
        Assert.Equal(500, Limits.MaxDeferredPerSecond);
        Assert.Equal(2000, Limits.MaxPacketsPerSecond);
        Assert.Equal(20000, Limits.MaxMessagesPerSecond);
        Assert.Equal(32, Limits.MaxEarlyMessages);
        Assert.Equal(1024, Limits.MaxConnectionData);
        Assert.Equal(60, Limits.FirstWaitSeconds);
        Assert.Equal(2, Limits.WaitMultiplier);
    }

    [Fact]
    public void ValuesOutsideTheRangeAreClamped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lobbyguard-{System.Guid.NewGuid():N}.cfg");
        try
        {
            ConfigFile config = new(path, false);
            ConfigEntry<int> entry = Limits.Int(config, "Limits", "Test", 10, 1, 20, "x");
            Assert.Equal(10, entry.Value);
            entry.Value = 500;
            Assert.Equal(20, entry.Value);
            entry.Value = -3;
            Assert.Equal(1, entry.Value);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
