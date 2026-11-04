using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using Unity.Netcode;

namespace LobbyGuard;

// NGO keeps a type's ServerRpcs and ClientRpcs in one table and doesn't check which way a call came from. The host is a
// client too, so a client can run any ClientRpc on the host by sending it as a ServerRpc.
internal static class Rpcs
{
    private delegate void Orig(ref NetworkContext context, ref RpcMetadata metadata, ref FastBufferReader payload, ref __RpcParams rpcParams);

    private static readonly Dictionary<MethodInfo, bool> clientRpcs = new();

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
            NetworkBehaviour.__rpc_func_table[behaviour.GetType()].TryGetValue(metadata.NetworkRpcMethodId, out NetworkBehaviour.RpcReceiveHandler handler) &&
            IsClientRpc(handler.Method))
        {
            Plugin.Kick(manager, context.SenderId, $"called {handler.Method.Name}, a ClientRpc only the host can call");
            return;
        }
        orig(ref context, ref metadata, ref payload, ref rpcParams);
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
