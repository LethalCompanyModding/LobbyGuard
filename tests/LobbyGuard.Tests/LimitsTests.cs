using System.IO;
using BepInEx.Configuration;
using Xunit;

namespace LobbyGuard.Tests;

// Limits is static, and a test that binds it changes what the others see.
[Collection("Limits")]
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

    [Fact]
    public void RpcRuleDefaultsAreTheTableValues()
    {
        Assert.Equal((8, 4f), Limits.RpcRate(RpcRules.For("ShipLights.SetShipLightsServerRpc")!));
        Assert.Equal((6, 3f), Limits.RpcRate(RpcRules.For("HUDManager.AddTextMessageServerRpc")!));
        Assert.Equal((4, 5f), Limits.RpcRate(RpcRules.For("Terminal.BuyItemsServerRpc")!));
        Assert.Equal((1, 600f), Limits.RpcRate(RpcRules.For("TimeOfDay.SetShipLeaveEarlyServerRpc")!));
        Assert.Equal((300, 1f), Limits.RpcRate(RpcRules.EnemySync));
        Assert.Equal(10, Limits.RateKickMultiplier);
        Assert.Equal(60f, Limits.HitReach);
        Assert.Equal(0.1f, Limits.MinPriceFraction, 3);
    }

    [Fact]
    public void BoundRpcLimitsApplyAndAreClamped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lobbyguard-{System.Guid.NewGuid():N}.cfg");
        try
        {
            ConfigFile config = new(path, false);
            Limits.Bind(config);
            Assert.Equal((8, 4f), Limits.RpcRate(RpcRules.For("ShipLights.SetShipLightsServerRpc")!));
            ConfigEntry<int> calls = (ConfigEntry<int>)config["Rpc rules", "SwitchCalls"];
            calls.Value = 3;
            Assert.Equal((3, 4f), Limits.RpcRate(RpcRules.For("ShipLights.SetShipLightsServerRpc")!));
            calls.Value = 0;
            Assert.Equal(1, calls.Value);
            ConfigEntry<int> sale = (ConfigEntry<int>)config["Rpc rules", "MaxSalePercent"];
            sale.Value = 500;
            Assert.Equal(100, sale.Value);
            Assert.Equal(0f, Limits.MinPriceFraction);
            ConfigEntry<int> reach = (ConfigEntry<int>)config["Rpc rules", "HitReachMetres"];
            reach.Value = 1;
            Assert.Equal(5f, Limits.HitReach);
            calls.Value = 8;
            sale.Value = 90;
            reach.Value = 60;
        }
        finally
        {
            File.Delete(path);
        }
    }
}
