using System;
using System.Collections.Generic;

namespace LobbyGuard;

internal enum Purchase
{
    None,
    Items,
    Unlockable,
    Level,
    Vehicle,
}

// A vanilla ServerRpc any client may call, and what a vanilla client never sends. Every number is a field, so a limit
// can be changed without touching the checks in RpcRules.Judge.
internal sealed class RpcRule
{
    // No vanilla client code calls it.
    public bool Never;
    // Parameters that hold the sender's own player number or client id. A player acts as themselves.
    public int[] SelfPlayer = [];
    public int[] SelfClient = [];
    // Parameters that must name a connected client.
    public int[] ConnectedClient = [];
    // Only the owner of the object the call goes to sends it. Held items belong to the player holding them.
    public bool TargetOwnedBySender;
    // A NetworkObjectReference parameter whose object the sender owns.
    public int OwnedArg = -1;
    // Furthest the sender's player may be from the object the call goes to, in metres. 0 is no limit.
    public float MaxDistance;
    // At most RateMax calls in RateSeconds per sender. 0 is no limit.
    public int RateMax;
    public float RateSeconds;
    // Inclusive limits for integer parameters.
    public Bound[] Bounds = [];
    public Purchase Purchase;
    public int CreditsParam = -1;
    public int IdParam = -1;
}

internal readonly struct Bound
{
    public Bound(int param, int min, int max)
    {
        Param = param;
        Min = min;
        Max = max;
    }

    public int Param { get; }

    public int Min { get; }

    public int Max { get; }
}

internal static partial class RpcRules
{
    // A rate over this many times the cap is a script, not lag: lag bunches a few calls together, not ten windows' worth.
    internal const int RateKickFactor = 10;

    // Sales take at most 90% off an item (highestSalePercentage is clamped to 90, and prices round to the nearest ten).
    internal const float MinPriceFraction = 0.1f;

    // The most a vanilla ship item holds to be bought at once.
    internal const int MaxItemsPerOrder = 12;

    // Damage from IHittable.Hit: force up to 2, up to 4, and above.
    internal const int MaxPlayerHit = 100;

    // Shotgun range is about 30 m and a position on the host can be a second behind.
    internal const float HitReach = 60;

    // A held item is in its holder's hand, so the owner's distance is the one that matters; stored for items on the
    // ground, where picking up needs the sender within arm's reach plus the same lag.
    internal const float PickupReach = 25;

    // Clicks on a ship switch, door or button by a person: a few a second at the very most.
    internal const int SwitchRateMax = 8;
    internal const float SwitchRateSeconds = 4;

    // Every player can vote once per day to send the ship off early. The day is far longer than this window, so
    // a second vote inside it is never a vanilla one.
    internal const int VoteRateMax = 1;
    internal const float VoteRateSeconds = 600;

    // Chat: the game's own text box sends one message per Enter press.
    internal const int ChatRateMax = 6;
    internal const float ChatRateSeconds = 3;

    // A vanilla Cruiser or shop call is a click on a terminal that takes seconds to read.
    internal const int ShopRateMax = 4;
    internal const float ShopRateSeconds = 5;

    private static readonly Dictionary<string, RpcRule> table = Build();

    internal static IReadOnlyDictionary<string, RpcRule> Table => table;

    internal static RpcRule? For(string key) => table.TryGetValue(key, out RpcRule? rule) ? rule : null;

    private static Dictionary<string, RpcRule> Build()
    {
        Dictionary<string, RpcRule> rules = new();

        // The caller names their own player: every call site passes the local player.
        foreach (string key in new[]
        {
            "GiantKiwiAI.PryOpenDoorServerRpc:0", "GiantKiwiAI.ScreechServerRpc:1", "GiantKiwiAI.PingBirdAttentionServerRpc:2",
            "FlowerSnakeEnemy.FSHitPlayerServerRpc:0", "FlowerSnakeEnemy.StopClingingServerRpc:0",
            "EntranceTeleport.TeleportPlayerServerRpc:0", "DepositItemsDesk.CheckAnimationGrabPlayerServerRpc:1",
            "SpikeRoofTrap.SpikeTrapSlamServerRpc:0", "CadaverGrowthAI.CoughSporesRpc:1", "CadaverGrowthAI.SyncBurstFromPlayerRpc:0",
            "CadaverGrowthAI.SyncInfectionMeterRpc:0", "BeltBagItem.TryAddObjectToBagServerRpc:1", "BeltBagItem.TryCheckingBagServerRpc:0",
            "CinespecManager.GetClientCalculatingCinespecRpc:0", "NutcrackerEnemyAI.SwitchTargetServerRpc:0",
            "VehicleController.RemoveKeyFromIgnitionServerRpc:0", "VehicleController.RevCarServerRpc:0",
            "VehicleController.StartIgnitionServerRpc:0", "VehicleController.TryIgnitionServerRpc:0",
            "VehicleController.CancelTryIgnitionServerRpc:0", "VehicleController.PassengerLeaveVehicleServerRpc:0",
            "VehicleController.SetPlayerInControlOfVehicleServerRpc:0", "VehicleController.RemovePlayerControlOfVehicleServerRpc:0",
            "VehicleController.ShiftToGearServerRpc:1", "VehicleController.SetHonkServerRpc:1", "VehicleController.AddTurboBoostServerRpc:0",
            "VehicleController.AddEngineOilServerRpc:0", "VehicleController.DestroyCarServerRpc:0",
            "CaveDwellerPhysicsProp.DropBabyServerRpc:0", "LassoManAI.BeginChasingPlayerServerRpc:0", "CrawlerAI.BeginChasingPlayerServerRpc:0",
            "ShipBuildModeManager.StoreObjectServerRpc:1",
            "SandSpiderAI.PlayerTripWebServerRpc:1", "SandSpiderAI.HitPlayerServerRpc:0", "CrawlerAI.HitPlayerServerRpc:0",
            "InteractTrigger.StopUsingServerRpc:0", "StingrayAI.ShowSpitOnPlayerServerRpc:0", "RoundManager.BreakTreeServerRpc:1",
            "SoccerBallProp.KickBallServerRpc:1", "RadarBoosterItem.PingRadarBoosterServerRpc:0", "Turret.EnterBerserkModeServerRpc:0",
            "ShipAlarmCord.PullCordServerRpc:0", "ShipAlarmCord.StopPullingCordServerRpc:0", "ItemCharger.PlayChargeItemEffectServerRpc:0",
            "RedLocustBees.EnterAttackZapModeServerRpc:0", "RedLocustBees.BeeKillPlayerServerRpc:0", "StartOfRound.EndGameServerRpc:0",
            "StartOfRound.StopShipSpeakerServerRpc:0", "ShipBuildModeManager.PlaceShipObjectServerRpc:3",
            "PlayerControllerB.DamagePlayerFromOtherClientServerRpc:2", "HUDManager.SyncPlayerLevelServerRpc:0",
            "BeltBagItem.RemoveFromBagServerRpc:4",
        })
        {
            string[] part = key.Split(':');
            rules[part[0]] = new RpcRule { SelfPlayer = [int.Parse(part[1])] };
        }

        // The caller names their own connection.
        rules["RoundManager.FinishedGeneratingLevelServerRpc"] = new RpcRule { SelfClient = [0] };
        rules["CentipedeAI.TriggerCentipedeFallServerRpc"] = new RpcRule { SelfClient = [0] };
        rules["StartOfRound.SyncAlreadyHeldObjectsServerRpc"] = new RpcRule { SelfClient = [0] };

        // A hit on another player is 20, 30 or 100, from someone within weapon range.
        rules["PlayerControllerB.DamagePlayerFromOtherClientServerRpc"] = new RpcRule
        {
            SelfPlayer = [2],
            Bounds = [new Bound(0, 0, MaxPlayerHit)],
            MaxDistance = HitReach,
        };

        // The player holding an item owns it. Anyone else's grab, equip, use or discard is a grief.
        foreach (string method in new[] { "GrabServerRpc", "ActivateItemRpc", "DiscardItemRpc", "InteractLeftRightServerRpc", "UseUpItemBatteriesServerRpc" })
        {
            rules[$"GrabbableObject.{method}"] = new RpcRule { TargetOwnedBySender = true };
        }
        rules["GrabbableObject.ChangeOwnershipOfPropServerRpc"] = new RpcRule { ConnectedClient = [0] };

        // Only the client that runs an enemy tells the host it died. Other clients' hits go through HitEnemy.
        rules["EnemyAI.KillEnemyServerRpc"] = new RpcRule { TargetOwnedBySender = true };
        rules["RoundManager.DespawnEnemyServerRpc"] = new RpcRule { Never = true };
        rules["DepositItemsDesk.AddObjectToDeskServerRpc"] = new RpcRule { OwnedArg = 0 };

        // The game defines these and nothing calls them.
        foreach (string key in new[]
        {
            "DepositItemsDesk.AttackPlayersServerRpc", "GrabbableObject.EquipItemServerRpc", "GrabbableObject.PocketItemServerRpc",
            "StartOfRound.SetDoorsClosedServerRpc", "StartOfRound.SyncCompanyBuyingRateServerRpc", "SpringManAI.SetCoilheadOnCooldownServerRpc",
            "CinespecManager.ResetClientCalculatingCinespecRpc", "CadaverBloomAI.SyncBitePlayerRpc", "ButlerEnemyAI.SyncKilledLastTargetFalseServerRpc",
            "CaveDwellerAI.SetBabyBServerRpc",
        })
        {
            rules[key] = new RpcRule { Never = true };
        }

        // The one vote per player per day.
        rules["TimeOfDay.SetShipLeaveEarlyServerRpc"] = new RpcRule { RateMax = VoteRateMax, RateSeconds = VoteRateSeconds };

        foreach (string key in new[]
        {
            "ShipLights.SetShipLightsServerRpc", "StartOfRound.SetMagnetOnServerRpc",
            "StartMatchLever.PlayLeverPullEffectsServerRpc", "ShipTeleporter.PressTeleportButtonServerRpc", "ItemDropship.OpenShipServerRpc",
            "StartOfRound.StartGameServerRpc", "TerminalAccessibleObject.SetDoorOpenServerRpc", "Landmine.ToggleMineServerRpc",
            "Turret.ToggleTurretServerRpc", "DoorLock.UnlockDoorServerRpc",
        })
        {
            rules[key] = new RpcRule { RateMax = SwitchRateMax, RateSeconds = SwitchRateSeconds };
        }
        foreach (string key in new[] { "HUDManager.AddPlayerChatMessageServerRpc", "HUDManager.AddTextMessageServerRpc", "HUDManager.UseSignalTranslatorServerRpc" })
        {
            rules[key] = new RpcRule { RateMax = ChatRateMax, RateSeconds = ChatRateSeconds };
        }

        // Credits: the client sends the total it thinks is left. A purchase takes exactly what it costs.
        rules["Terminal.BuyItemsServerRpc"] = new RpcRule { Purchase = Purchase.Items, IdParam = 0, CreditsParam = 1, RateMax = ShopRateMax, RateSeconds = ShopRateSeconds };
        rules["StartOfRound.BuyShipUnlockableServerRpc"] = new RpcRule { Purchase = Purchase.Unlockable, IdParam = 0, CreditsParam = 1, RateMax = ShopRateMax, RateSeconds = ShopRateSeconds };
        rules["StartOfRound.ChangeLevelServerRpc"] = new RpcRule { Purchase = Purchase.Level, IdParam = 0, CreditsParam = 1, RateMax = ShopRateMax, RateSeconds = ShopRateSeconds };
        rules["Terminal.BuyVehicleServerRpc"] = new RpcRule { Purchase = Purchase.Vehicle, IdParam = 0, CreditsParam = 1, RateMax = ShopRateMax, RateSeconds = ShopRateSeconds };
        return rules;
    }
}
