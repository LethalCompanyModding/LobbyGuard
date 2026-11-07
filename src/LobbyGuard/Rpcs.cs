using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Unity.Netcode;

namespace LobbyGuard;

// NGO keeps a type's ServerRpcs and ClientRpcs in one table and doesn't check which way a call came from. The host is a
// client too, so a client can run any ClientRpc on the host by sending it as a ServerRpc.
// The ServerRpcs any client may call (RequireOwnership = false) get the checks in RpcRules too.
internal static class Rpcs
{
    private delegate void Orig(ref NetworkContext context, ref RpcMetadata metadata, ref FastBufferReader payload, ref __RpcParams rpcParams);

    private static readonly Dictionary<MethodInfo, bool> clientRpcs = new();
    private static readonly Dictionary<MethodInfo, (string Key, MethodInfo Rpc, RpcRule Rule)?> ruled = new();
    private static readonly CallRates rates = new();
    private static readonly Dictionary<(ulong, string), float> lastDropLog = new();

    // A script dropping thousands of calls a second shouldn't fill the log.
    private const float DropLogSeconds = 10;

    private static Hook? hook;

    public static void Hook()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        hook = new Hook(typeof(RpcMessageHelpers).GetMethod(nameof(RpcMessageHelpers.Handle), any),
            typeof(Rpcs).GetMethod(nameof(Handle), any));
    }

    private static void Handle(Orig orig, ref NetworkContext context, ref RpcMetadata metadata, ref FastBufferReader payload, ref __RpcParams rpcParams)
    {
        if (context.SenderId != NetworkManager.ServerClientId && context.SystemOwner is NetworkManager { IsServer: true } manager &&
            manager.SpawnManager.SpawnedObjects.TryGetValue(metadata.NetworkObjectId, out NetworkObject target) &&
            target.GetNetworkBehaviourAtOrderIndex(metadata.NetworkBehaviourId) is NetworkBehaviour behaviour &&
            NetworkBehaviour.__rpc_func_table[behaviour.GetType()].TryGetValue(metadata.NetworkRpcMethodId, out NetworkBehaviour.RpcReceiveHandler handler))
        {
            if (IsClientRpc(handler.Method))
            {
                Plugin.Kick(manager, context.SenderId, $"called {handler.Method.Name}, a ClientRpc only the host can call");
                return;
            }
            if (Judge(manager, context.SenderId, target, handler.Method, payload) is { } verdict)
            {
                if (verdict.Verdict == Verdict.Kick)
                {
                    rates.Remove(context.SenderId);
                    Plugin.Kick(manager, context.SenderId, verdict.Why);
                }
                else
                {
                    Drop(manager, context.SenderId, verdict.Why);
                }
                return;
            }
        }
        orig(ref context, ref metadata, ref payload, ref rpcParams);
    }

    internal static void Forget(ulong clientId) => rates.Remove(clientId);

    // Null lets the call through, including when the game gave nothing to judge it by.
    private static (Verdict Verdict, string Why)? Judge(NetworkManager manager, ulong sender, NetworkObject target, MethodInfo handler, FastBufferReader payload)
    {
        try
        {
            if (!ruled.TryGetValue(handler, out (string Key, MethodInfo Rpc, RpcRule Rule)? found))
            {
                ruled[handler] = found = Find(handler);
            }
            if (found is not { } rule)
            {
                return null;
            }
            Call call = RpcContext.Build(rule.Rule, sender, manager, target, rule.Rpc, payload);
            (Verdict verdict, string why) = RpcRules.Judge(rule.Key, rule.Rule, call, rates);
            return verdict == Verdict.Allow ? null : (verdict, why);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Couldn't check an RPC call, letting it through: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    private static void Drop(NetworkManager manager, ulong clientId, string why)
    {
        float now = UnityEngine.Time.realtimeSinceStartup;
        if (lastDropLog.TryGetValue((clientId, why), out float last) && now - last < DropLogSeconds)
        {
            return;
        }
        lastDropLog[(clientId, why)] = now;
        ulong steamId = Plugin.SteamIdOf(manager, clientId);
        Plugin.Log.LogWarning($"Dropped a call from {(steamId != 0 ? $"Steam user {steamId}" : $"client {clientId}")}: {why}");
    }

    private static bool IsOpenEnemyRpc(MethodInfo rpc) =>
        typeof(EnemyAI).IsAssignableFrom(rpc.DeclaringType) &&
        (rpc.GetCustomAttribute<ServerRpcAttribute>() is { RequireOwnership: false } || rpc.GetCustomAttribute<RpcAttribute>() is { RequireOwnership: false });

    // The ServerRpc a handler calls and its rule, if it has one.
    private static (string Key, MethodInfo Rpc, RpcRule Rule)? Find(MethodInfo handler)
    {
        byte[]? il = handler.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return null;
        }
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28 && il[i] != 0x6F)
            {
                continue;
            }
            try
            {
                MethodBase method = handler.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                if (method is MethodInfo rpc && method.DeclaringType != null && (method.IsDefined(typeof(ServerRpcAttribute), false) || method.IsDefined(typeof(RpcAttribute), false)))
                {
                    string key = $"{method.DeclaringType.Name}.{method.Name}";
                    RpcRule? rule = RpcRules.For(key) ?? (IsOpenEnemyRpc(rpc) ? RpcRules.EnemySync : null);
                    return rule != null ? (key, rpc, rule) : null;
                }
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException or System.IO.FileNotFoundException or System.IO.FileLoadException or MemberAccessException)
            {
            }
        }
        return null;
    }

    // A handler is NGO's generated code that reads the arguments and calls the RPC.
    internal static bool IsClientRpc(MethodInfo handler)
    {
        if (!clientRpcs.TryGetValue(handler, out bool isClientRpc))
        {
            clientRpcs[handler] = isClientRpc = CallsClientRpc(handler);
        }
        return isClientRpc;
    }

    private static bool CallsClientRpc(MethodInfo handler)
    {
        byte[]? il = handler.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return false;
        }
        for (int i = 0; i + 4 < il.Length; i++)
        {
            // call and callvirt. A stray byte that isn't one just fails to resolve.
            if (il[i] != 0x28 && il[i] != 0x6F)
            {
                continue;
            }
            try
            {
                if (handler.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)).IsDefined(typeof(ClientRpcAttribute), false))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException or System.IO.FileNotFoundException or System.IO.FileLoadException or MemberAccessException)
            {
            }
        }
        return false;
    }
}
