using Xunit;

namespace LobbyGuard.Tests;

public sealed class CooldownsTests
{
    [Fact]
    public void KeepsAKickedPlayerOutForAMinuteThenLetsThemIn()
    {
        Cooldowns cooldowns = new();
        Assert.Equal(0, cooldowns.Remaining(7, 100));
        Assert.Equal(60, cooldowns.Add(7, 100));
        Assert.Equal(60, cooldowns.Remaining(7, 100));
        Assert.Equal(10, cooldowns.Remaining(7, 150));
        Assert.Equal(0, cooldowns.Remaining(7, 160));
    }

    [Fact]
    public void DoublesTheWaitForEachFurtherKick()
    {
        Cooldowns cooldowns = new();
        Assert.Equal(60, cooldowns.Add(7, 0));
        Assert.Equal(120, cooldowns.Add(7, 70));
        Assert.Equal(240, cooldowns.Add(7, 200));
        Assert.Equal(240, cooldowns.Remaining(7, 200));
    }

    [Fact]
    public void KeepsPlayersApartAndClearsForANewSession()
    {
        Cooldowns cooldowns = new();
        cooldowns.Add(1, 0);
        cooldowns.Add(1, 0);
        Assert.Equal(60, cooldowns.Add(2, 0));
        cooldowns.Clear();
        Assert.Equal(0, cooldowns.Remaining(1, 0));
        Assert.Equal(60, cooldowns.Add(1, 0));
    }

    [Fact]
    public void ManyKicksDontOverflow()
    {
        Cooldowns cooldowns = new();
        for (int i = 0; i < 100; i++)
        {
            Assert.True(cooldowns.Add(1, 0) > 0);
        }
    }
}
