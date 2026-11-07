using System;
using System.Collections.Generic;

namespace LobbyGuard;

internal enum Verdict
{
    Allow,
    // Lag or a race could make a vanilla client send it, so the call is ignored and the player stays.
    Drop,
    // No vanilla client can send it.
    Kick,
}

// What LobbyGuard knows about one call when it judges it. Null means it couldn't be found, and the check that needs it
// lets the call through.
internal sealed class Call
{
    public ulong Sender;
    public double Now;
    public int? SenderPlayer;
    public object?[] Args = [];
    public ulong? TargetOwner;
    public ulong?[] ArgOwners = [];
    public float? Distance;
    public bool[] ClientsConnected = [];
    public int? Credits;
    public int? PriceCap;
    public int? PriceFloor;
    // Why an id in the call can't exist in this game, if it can't.
    public string? Invalid;
}

internal sealed class CallRates
{
    private readonly Dictionary<(ulong, string), (double Start, int Count)> windows = new();

    // Counts one call and returns this window's total.
    public int Add(ulong sender, string key, double now, float seconds)
    {
        windows.TryGetValue((sender, key), out (double Start, int Count) window);
        window = window.Count > 0 && now - window.Start < seconds ? (window.Start, window.Count + 1) : (now, 1);
        windows[(sender, key)] = window;
        return window.Count;
    }

    public void Remove(ulong sender)
    {
        foreach ((ulong, string) key in new List<(ulong, string)>(windows.Keys))
        {
            if (key.Item1 == sender)
            {
                windows.Remove(key);
            }
        }
    }
}

internal static partial class RpcRules
{
    // The highest argument index a rule reads, so only that many arguments are decoded.
    internal static int LastArg(RpcRule rule)
    {
        int last = Math.Max(rule.CreditsParam, Math.Max(rule.IdParam, rule.OwnedArg));
        foreach (int i in rule.SelfPlayer) last = Math.Max(last, i);
        foreach (int i in rule.SelfClient) last = Math.Max(last, i);
        foreach (int i in rule.ConnectedClient) last = Math.Max(last, i);
        foreach (Bound b in rule.Bounds) last = Math.Max(last, b.Param);
        return last;
    }

    internal static (Verdict Verdict, string Why) Judge(string key, RpcRule rule, Call call, CallRates rates)
    {
        if (rule.Never)
        {
            return (Verdict.Kick, $"called {key}, which no vanilla client sends");
        }
        foreach (int i in rule.SelfPlayer)
        {
            if (call.SenderPlayer is { } own && call.Args.Length > i && call.Args[i] is int named && named != own)
            {
                return (Verdict.Kick, $"called {key} as player {named}");
            }
        }
        foreach (int i in rule.SelfClient)
        {
            if (call.Args.Length > i && ToUlong(call.Args[i]) is { } named && named != call.Sender)
            {
                return (Verdict.Kick, $"called {key} as client {named}");
            }
        }
        foreach (Bound b in rule.Bounds)
        {
            if (call.Args.Length > b.Param && call.Args[b.Param] is int value && (value < b.Min || value > b.Max))
            {
                return (Verdict.Kick, $"called {key} with {value}, outside {b.Min} to {b.Max}");
            }
        }
        if (call.Invalid != null)
        {
            return (Verdict.Kick, $"called {key} with {call.Invalid}");
        }
        if (rule.Purchase != Purchase.None && PurchaseVerdict(key, rule, call) is { } bad)
        {
            return bad;
        }
        foreach (int i in rule.ConnectedClient)
        {
            if (call.ClientsConnected.Length > i && !call.ClientsConnected[i])
            {
                return (Verdict.Drop, $"{key} named a client that isn't connected");
            }
        }
        if (rule.TargetOwnedBySender && call.TargetOwner is { } owner && owner != call.Sender)
        {
            return (Verdict.Drop, $"{key} on an object they don't own");
        }
        if (rule.OwnedArg >= 0 && call.ArgOwners.Length > rule.OwnedArg && call.ArgOwners[rule.OwnedArg] is { } argOwner && argOwner != call.Sender)
        {
            return (Verdict.Drop, $"{key} on an object they don't own");
        }
        if (rule.MaxDistance > 0 && call.Distance is { } far && far > rule.MaxDistance)
        {
            return (Verdict.Drop, $"{key} from {far:0} m away");
        }
        if (rule.RateMax > 0)
        {
            int count = rates.Add(call.Sender, key, call.Now, rule.RateSeconds);
            if (count > rule.RateMax * RateKickFactor)
            {
                return (Verdict.Kick, $"called {key} {count} times in {rule.RateSeconds:0.#} s");
            }
            if (count > rule.RateMax)
            {
                return (Verdict.Drop, $"{key} too often, {count} times in {rule.RateSeconds:0.#} s");
            }
        }
        return (Verdict.Allow, "");
    }

    private static (Verdict, string)? PurchaseVerdict(string key, RpcRule rule, Call call)
    {
        if (call.Args.Length <= rule.CreditsParam || call.Args[rule.CreditsParam] is not int credits)
        {
            return null;
        }
        if (credits < 0)
        {
            return (Verdict.Kick, $"called {key} with {credits} credits left");
        }
        if (rule.Purchase == Purchase.Items && call.Args.Length > rule.IdParam && call.Args[rule.IdParam] is int[] ids && ids.Length > MaxItemsPerOrder)
        {
            return (Verdict.Kick, $"ordered {ids.Length} items at once");
        }
        if (call.Credits is not { } have)
        {
            return null;
        }
        int spent = have - credits;
        if (spent < 0)
        {
            return (Verdict.Drop, $"{key} claiming more credits than the lobby has");
        }
        if (call.PriceCap is { } cap && spent > cap)
        {
            return (Verdict.Drop, $"{key} charging {spent}, more than the {cap} it costs");
        }
        if (call.PriceFloor is { } floor && spent < floor)
        {
            return (Verdict.Drop, $"{key} charging {spent}, less than the {floor} it costs");
        }
        return null;
    }

    private static ulong? ToUlong(object? value) => value switch
    {
        ulong u => u,
        int i when i >= 0 => (ulong)i,
        _ => null,
    };
}
