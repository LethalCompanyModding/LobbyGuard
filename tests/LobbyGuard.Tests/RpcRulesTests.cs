using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Xunit;

namespace LobbyGuard.Tests;

// Limits is static, and a test that binds it changes what the others see.
[Collection("Limits")]
public sealed class RpcRulesTests
{
    private const ulong Sender = 3;

    private static readonly int[] Two = [0, 1];
    private static readonly int[] One = [1];
    private static readonly string[] ClientTypes = ["UInt64", "Int32"];

    private static Call CallFrom(params object?[] args) => new() { Sender = Sender, Now = 100, SenderPlayer = 2, Args = args };

    private static (Verdict Verdict, string Why) Judge(string key, Call call, CallRates? rates = null) =>
        RpcRules.Judge(key, RpcRules.For(key)!, call, rates ?? new CallRates());

    [Fact]
    public void ACallNamingAnotherPlayerKicksAndOwnIsAllowed()
    {
        Assert.Equal(Verdict.Allow, Judge("ShipAlarmCord.PullCordServerRpc", CallFrom(2)).Verdict);
        Assert.Equal(Verdict.Kick, Judge("ShipAlarmCord.PullCordServerRpc", CallFrom(1)).Verdict);
    }

    [Fact]
    public void ASenderNotYetInThePlayerListIsNotJudgedByIndex()
    {
        Call call = CallFrom(1);
        call.SenderPlayer = null;
        Assert.Equal(Verdict.Allow, Judge("ShipAlarmCord.PullCordServerRpc", call).Verdict);
    }

    [Fact]
    public void ACallNamingAnotherClientKicks()
    {
        Assert.Equal(Verdict.Allow, Judge("RoundManager.FinishedGeneratingLevelServerRpc", CallFrom(Sender)).Verdict);
        Assert.Equal(Verdict.Kick, Judge("RoundManager.FinishedGeneratingLevelServerRpc", CallFrom(1UL)).Verdict);
        Assert.Equal(Verdict.Kick, Judge("StartOfRound.SyncAlreadyHeldObjectsServerRpc", CallFrom(5)).Verdict);
    }

    [Fact]
    public void AHitIsTwentyThirtyOrAHundredFromSomeoneInReach()
    {
        const string key = "PlayerControllerB.DamagePlayerFromOtherClientServerRpc";
        Assert.Equal(Verdict.Allow, Judge(key, CallFrom(100, default(object), 2)).Verdict);
        Assert.Equal(Verdict.Kick, Judge(key, CallFrom(101, default(object), 2)).Verdict);
        Assert.Equal(Verdict.Kick, Judge(key, CallFrom(-20, default(object), 2)).Verdict);
        Assert.Equal(Verdict.Kick, Judge(key, CallFrom(20, default(object), 1)).Verdict);
        Call far = CallFrom(20, default(object), 2);
        far.Distance = RpcRules.HitReach + 1;
        Assert.Equal(Verdict.Drop, Judge(key, far).Verdict);
        far.Distance = RpcRules.HitReach;
        Assert.Equal(Verdict.Allow, Judge(key, far).Verdict);
    }

    [Fact]
    public void CallsNothingInTheGameMakesKick()
    {
        Assert.Equal(Verdict.Kick, Judge("DepositItemsDesk.AttackPlayersServerRpc", CallFrom()).Verdict);
        Assert.Equal(Verdict.Kick, Judge("RoundManager.DespawnEnemyServerRpc", CallFrom(default(object))).Verdict);
    }

    [Fact]
    public void OnlyTheOwnerOfAnItemOrEnemyCallsIt()
    {
        Call call = CallFrom();
        call.TargetOwner = Sender;
        Assert.Equal(Verdict.Allow, Judge("GrabbableObject.GrabServerRpc", call).Verdict);
        call.TargetOwner = 7;
        Assert.Equal(Verdict.Drop, Judge("GrabbableObject.GrabServerRpc", call).Verdict);
        Assert.Equal(Verdict.Drop, Judge("EnemyAI.KillEnemyServerRpc", call).Verdict);
        call.TargetOwner = null;
        Assert.Equal(Verdict.Allow, Judge("GrabbableObject.GrabServerRpc", call).Verdict);
    }

    [Fact]
    public void SellingAnItemTakesOwningIt()
    {
        Call call = CallFrom(default(object));
        call.ArgOwners = [7];
        Assert.Equal(Verdict.Drop, Judge("DepositItemsDesk.AddObjectToDeskServerRpc", call).Verdict);
        call.ArgOwners = [Sender];
        Assert.Equal(Verdict.Allow, Judge("DepositItemsDesk.AddObjectToDeskServerRpc", call).Verdict);
        call.ArgOwners = [null];
        Assert.Equal(Verdict.Allow, Judge("DepositItemsDesk.AddObjectToDeskServerRpc", call).Verdict);
    }

    [Fact]
    public void AnItemCanOnlyBeHandedToAConnectedClient()
    {
        Call call = CallFrom(99UL);
        call.ClientsConnected = [false];
        Assert.Equal(Verdict.Drop, Judge("GrabbableObject.ChangeOwnershipOfPropServerRpc", call).Verdict);
        call.ClientsConnected = [true];
        Assert.Equal(Verdict.Allow, Judge("GrabbableObject.ChangeOwnershipOfPropServerRpc", call).Verdict);
    }

    [Fact]
    public void ARateOverTheCapDropsAndFarOverKicks()
    {
        const string key = "TimeOfDay.SetShipLeaveEarlyServerRpc";
        CallRates rates = new();
        Assert.Equal(Verdict.Allow, Judge(key, CallFrom(), rates).Verdict);
        Assert.Equal(Verdict.Drop, Judge(key, CallFrom(), rates).Verdict);
        Verdict last = Verdict.Allow;
        for (int i = 0; i < RpcRules.RateKickFactor; i++)
        {
            last = Judge(key, CallFrom(), rates).Verdict;
        }
        Assert.Equal(Verdict.Kick, last);
    }

    [Fact]
    public void ARateWindowEndsAndSendersAreCountedApart()
    {
        const string key = "TimeOfDay.SetShipLeaveEarlyServerRpc";
        CallRates rates = new();
        Judge(key, CallFrom(), rates);
        Assert.Equal(Verdict.Drop, Judge(key, CallFrom(), rates).Verdict);
        Call other = CallFrom();
        other.Sender = 4;
        Assert.Equal(Verdict.Allow, Judge(key, other, rates).Verdict);
        Call later = CallFrom();
        later.Now = 100 + RpcRules.VoteRateSeconds;
        Assert.Equal(Verdict.Allow, Judge(key, later, rates).Verdict);
        rates.Remove(Sender);
        Assert.Equal(Verdict.Allow, Judge(key, CallFrom(), rates).Verdict);
    }

    [Fact]
    public void ChatAtTheSpeedOfTypingIsAllowed()
    {
        CallRates rates = new();
        for (int i = 0; i < RpcRules.ChatRateMax; i++)
        {
            Call call = CallFrom("hi", 2);
            call.Now = 100 + i * 0.4;
            Assert.Equal(Verdict.Allow, Judge("HUDManager.AddTextMessageServerRpc", call, rates).Verdict);
        }
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(70, 0)]
    [InlineData(80, 1)]
    [InlineData(101, 1)]
    public void CreditsLeftHaveToMatchWhatThePurchaseCosts(int left, int expected)
    {
        Call call = CallFrom(4, left);
        call.Credits = 100;
        call.PriceCap = call.PriceFloor = 30;
        Assert.Equal((Verdict)expected, Judge("StartOfRound.BuyShipUnlockableServerRpc", call).Verdict);
    }

    [Fact]
    public void ASaleCanTakeUpToNinetyPercentOffAnItem()
    {
        Call call = CallFrom(Two, 0);
        call.Credits = 500;
        call.PriceCap = 200;
        call.PriceFloor = (int)(200 * RpcRules.MinPriceFraction);
        Assert.Equal(Verdict.Drop, Judge("Terminal.BuyItemsServerRpc", call).Verdict);
        call.Args[1] = 500 - 20;
        Assert.Equal(Verdict.Allow, Judge("Terminal.BuyItemsServerRpc", call).Verdict);
        call.Args[1] = 500 - 19;
        Assert.Equal(Verdict.Drop, Judge("Terminal.BuyItemsServerRpc", call).Verdict);
        call.Args[1] = 500 - 201;
        Assert.Equal(Verdict.Drop, Judge("Terminal.BuyItemsServerRpc", call).Verdict);
    }

    [Fact]
    public void AnOrderLargerThanTheCartKicksAndUnknownPricesLetItThrough()
    {
        Call call = CallFrom(new int[RpcRules.MaxItemsPerOrder + 1], 10);
        Assert.Equal(Verdict.Kick, Judge("Terminal.BuyItemsServerRpc", call).Verdict);
        call = CallFrom(One, 0);
        call.Credits = 500;
        Assert.Equal(Verdict.Allow, Judge("Terminal.BuyItemsServerRpc", call).Verdict);
        call.Invalid = "item 99";
        Assert.Equal(Verdict.Kick, Judge("Terminal.BuyItemsServerRpc", call).Verdict);
    }

    [Fact]
    public void EveryRuleNamesARealRpcWithTheRightParameterTypes()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll");
        using AssemblyDefinition game = AssemblyDefinition.ReadAssembly(path);
        foreach ((string key, RpcRule rule) in RpcRules.Table)
        {
            string[] part = key.Split('.');
            TypeDefinition type = game.MainModule.Types.Single(t => t.Name == part[0] || t.FullName.EndsWith("." + part[0], StringComparison.Ordinal));
            MethodDefinition method = type.Methods.Single(m => m.Name == part[1]);
            Assert.True(method.CustomAttributes.Any(a => a.AttributeType.Name is "ServerRpcAttribute" or "RpcAttribute" &&
                a.Properties.Concat(a.Fields).Any(p => p.Name == "RequireOwnership" && p.Argument.Value is false)), $"{key} is not a RequireOwnership = false RPC");
            string TypeAt(int i)
            {
                Assert.InRange(i, 0, method.Parameters.Count - 1);
                return method.Parameters[i].ParameterType.Name;
            }
            foreach (int i in rule.SelfPlayer) Assert.Equal("Int32", TypeAt(i));
            foreach (int i in rule.SelfClient) Assert.Contains(TypeAt(i), ClientTypes);
            foreach (int i in rule.ConnectedClient) Assert.Equal("UInt64", TypeAt(i));
            foreach (Bound b in rule.Bounds) Assert.Equal("Int32", TypeAt(b.Param));
            if (rule.OwnedArg >= 0) Assert.Equal("NetworkObjectReference", TypeAt(rule.OwnedArg));
            if (rule.Purchase != Purchase.None)
            {
                Assert.Equal("Int32", TypeAt(rule.CreditsParam));
                Assert.Equal(rule.Purchase == Purchase.Items ? "Int32[]" : "Int32", TypeAt(rule.IdParam));
            }
        }
    }

    [Fact]
    public void EnemySyncCallsAreCappedPerCallType()
    {
        CallRates rates = new();
        Verdict last = Verdict.Allow;
        for (int i = 0; i < RpcRules.EnemyRateMax; i++)
        {
            last = RpcRules.Judge("PumaAI.SyncStalkingFrozenRpc", RpcRules.EnemySync, CallFrom(), rates).Verdict;
        }
        Assert.Equal(Verdict.Allow, last);
        Assert.Equal(Verdict.Drop, RpcRules.Judge("PumaAI.SyncStalkingFrozenRpc", RpcRules.EnemySync, CallFrom(), rates).Verdict);
        Assert.Equal(Verdict.Allow, RpcRules.Judge("PumaAI.PingPumaAttentionRpc", RpcRules.EnemySync, CallFrom(), rates).Verdict);
    }
}
