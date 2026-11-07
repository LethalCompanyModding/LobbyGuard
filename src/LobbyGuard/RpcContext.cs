using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LobbyGuard;

// Reads what an RpcRule needs from the running game: the call's arguments, who owns the objects it names, and prices.
internal static class RpcContext
{
    private static Dictionary<int, int>? unlockableCosts;
    private static Dictionary<int, int>? levelCosts;

    public static Call Build(RpcRule rule, ulong sender, NetworkManager manager, NetworkObject target, MethodInfo rpc, FastBufferReader payload)
    {
        Call call = new() { Sender = sender, Now = Time.realtimeSinceStartup, TargetOwner = target.OwnerClientId };
        StartOfRound? round = StartOfRound.Instance;
        PlayerControllerB? player = null;
        if (round != null && round.ClientPlayerList.TryGetValue(sender, out int index))
        {
            call.SenderPlayer = index;
            player = index >= 0 && index < round.allPlayerScripts.Length ? round.allPlayerScripts[index] : null;
        }
        if (player != null && rule.MaxDistance > 0)
        {
            call.Distance = Vector3.Distance(player.transform.position, target.transform.position);
        }
        call.Args = ReadArgs(payload, rpc.GetParameters(), RpcRules.LastArg(rule));
        call.ArgOwners = new ulong?[call.Args.Length];
        call.ClientsConnected = new bool[call.Args.Length];
        for (int i = 0; i < call.Args.Length; i++)
        {
            if (call.Args[i] is NetworkObjectReference reference)
            {
                call.ArgOwners[i] = manager.SpawnManager.SpawnedObjects.TryGetValue(reference.NetworkObjectId, out NetworkObject named) ? named.OwnerClientId : null;
            }
            else if (call.Args[i] is ulong client)
            {
                call.ClientsConnected[i] = manager.ConnectedClients.ContainsKey(client);
            }
        }
        if (rule.Purchase != Purchase.None)
        {
            Price(rule, call);
        }
        return call;
    }

    // The arguments up to `last`, decoded the way NGO's generated handler does. The caller's reader is put back where
    // it was, since the position is shared. Null for an argument type no rule reads.
    private static object?[] ReadArgs(FastBufferReader reader, ParameterInfo[] parameters, int last)
    {
        int start = reader.Position;
        object?[] args = new object?[Math.Min(last + 1, parameters.Length)];
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (type == typeof(int))
                {
                    ByteUnpacker.ReadValueBitPacked(reader, out int value);
                    args[i] = value;
                }
                else if (type == typeof(ulong))
                {
                    ByteUnpacker.ReadValueBitPacked(reader, out ulong value);
                    args[i] = value;
                }
                else if (type == typeof(bool))
                {
                    reader.ReadValueSafe(out bool value, default(FastBufferWriter.ForPrimitives));
                    args[i] = value;
                }
                else if (type == typeof(float))
                {
                    reader.ReadValueSafe(out float value, default(FastBufferWriter.ForPrimitives));
                    args[i] = value;
                }
                else if (type == typeof(Vector3))
                {
                    reader.ReadValueSafe(out Vector3 value);
                    args[i] = value;
                }
                else if (type == typeof(Quaternion))
                {
                    reader.ReadValueSafe(out Quaternion value);
                    args[i] = value;
                }
                else if (type == typeof(NetworkObjectReference))
                {
                    reader.ReadValueSafe(out NetworkObjectReference value, default(FastBufferWriter.ForNetworkSerializable));
                    args[i] = value;
                }
                else if (type == typeof(int[]))
                {
                    reader.ReadValueSafe(out bool present, default(FastBufferWriter.ForPrimitives));
                    int[]? value = null;
                    if (present)
                    {
                        reader.ReadValueSafe(out value, default(FastBufferWriter.ForPrimitives));
                    }
                    args[i] = value;
                }
                else
                {
                    // Later arguments can't be placed.
                    break;
                }
            }
        }
        catch (OverflowException)
        {
            // A short payload gets NGO's own error from the real handler.
        }
        finally
        {
            reader.Seek(start);
        }
        return args;
    }

    private static void Price(RpcRule rule, Call call)
    {
        Terminal? terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
        StartOfRound? round = StartOfRound.Instance;
        if (terminal == null || round == null)
        {
            return;
        }
        call.Credits = terminal.groupCredits;
        switch (rule.Purchase)
        {
            case Purchase.Items when call.Args.Length > rule.IdParam && call.Args[rule.IdParam] is int[] ids:
                PriceItems(terminal, ids, call);
                break;
            case Purchase.Unlockable when call.Args.Length > rule.IdParam && call.Args[rule.IdParam] is int unlockable:
                if (unlockable < 0 || unlockable >= round.unlockablesList.unlockables.Count)
                {
                    call.Invalid = $"unlockable {unlockable}";
                }
                else if (Costs(ref unlockableCosts, node => node.shipUnlockableID).TryGetValue(unlockable, out int cost))
                {
                    call.PriceCap = call.PriceFloor = cost;
                }
                break;
            case Purchase.Level when call.Args.Length > rule.IdParam && call.Args[rule.IdParam] is int level:
                if (level < 0 || level >= round.levels.Length)
                {
                    call.Invalid = $"moon {level}";
                }
                else if (Costs(ref levelCosts, node => node.buyRerouteToMoon).TryGetValue(level, out int cost))
                {
                    call.PriceCap = call.PriceFloor = cost;
                }
                break;
            case Purchase.Vehicle when call.Args.Length > rule.IdParam && call.Args[rule.IdParam] is int vehicle:
                if (vehicle < 0 || vehicle >= terminal.buyableVehicles.Length)
                {
                    call.Invalid = $"vehicle {vehicle}";
                }
                else
                {
                    // A warranty ticket makes the next one free, so only the top is known.
                    call.PriceCap = terminal.buyableVehicles[vehicle].creditsWorth;
                }
                break;
        }
    }

    // A negative id is the game's own bundle, which has its own price.
    private static void PriceItems(Terminal terminal, int[] ids, Call call)
    {
        int cap = 0;
        int floor = 0;
        foreach (int id in ids)
        {
            if (id >= terminal.buyableItemsList.Length)
            {
                call.Invalid = $"item {id}";
                return;
            }
            if (id < 0)
            {
                return;
            }
            int worth = terminal.buyableItemsList[id].creditsWorth;
            cap += worth;
            floor += (int)(worth * Limits.MinPriceFraction);
        }
        call.PriceCap = cap;
        call.PriceFloor = floor;
    }

    // The terminal nodes that confirm a purchase hold its price; the scene's assets aren't in the code.
    private static Dictionary<int, int> Costs(ref Dictionary<int, int>? cache, Func<TerminalNode, int> idOf)
    {
        if (cache != null)
        {
            return cache;
        }
        cache = new Dictionary<int, int>();
        foreach (TerminalNode node in Resources.FindObjectsOfTypeAll<TerminalNode>())
        {
            int id = idOf(node);
            if (id >= 0 && node.itemCost > 0)
            {
                cache[id] = node.itemCost;
            }
        }
        return cache;
    }
}
