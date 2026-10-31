using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using Unity.Netcode;

namespace LobbyGuard;

// Each RPC handler gets a check in front of its array, string and NativeArray reads. A length that can't fit in
// what's left of the message drops the message and disconnects the sender.
internal static class Lengths
{
    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly List<ILHook> hooks = new();

    private static readonly MethodInfo check = typeof(Lengths).GetMethod(nameof(Check), Statics);

    public static void HookAll()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Hook(e.LoadedAssembly);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Hook(assembly);
        }
    }

    public static bool Fits(int count, int size, int remaining) => count >= 0 && (long)count * size <= remaining;

    private static unsafe void Check(ref FastBufferReader reader, int size, __RpcParams rpc)
    {
        int remaining = reader.Length - reader.Position - sizeof(int);
        // Too short for a length at all: NGO throws on its own.
        if (remaining < 0 || Fits(*(int*)reader.GetUnsafePtrAtCurrentPosition(), size, remaining))
        {
            return;
        }
        ulong sender = rpc.Server.Receive.SenderClientId;
        NetworkManager? manager = NetworkManager.Singleton;
        if (manager != null && manager.IsServer && sender != NetworkManager.ServerClientId)
        {
            Plugin.Kick(manager, sender, "RPC array longer than its message");
        }
        throw new OverflowException("RPC array longer than its message");
    }

    // This runs inside every later mod's assembly load, where an exception fails that mod's load too, and at startup,
    // where it would stop LobbyGuard's other guards from going in. One bad assembly only loses its own checks.
    private static void Hook(Assembly assembly)
    {
        try
        {
            HookTypes(assembly);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Skipped RPC length checks in {assembly.GetName().Name}: {e.Message}");
        }
    }

    private static void HookTypes(Assembly assembly)
    {
        if (assembly.IsDynamic || assembly == typeof(NetworkBehaviour).Assembly || !References(assembly))
        {
            return;
        }
        Type?[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types;
        }
        foreach (Type? type in types)
        {
            if (type == null || !Derives(type, typeof(NetworkBehaviour)))
            {
                continue;
            }
            try
            {
                hooks.AddRange(type.GetMethods(Statics | BindingFlags.DeclaredOnly)
                    .Where(handler => handler.Name.StartsWith("__rpc_handler_", StringComparison.Ordinal) && ReadsLength(handler))
                    .Select(handler => new ILHook(handler, Guard)));
            }
            catch (Exception e) when (Missing(e))
            {
            }
        }
    }

    // A mod's compat code for another mod the player doesn't have names types from an assembly that isn't there. Mono
    // only fails on them once something looks at the type, so such a type isn't one to check.
    internal static bool Derives(Type type, Type from)
    {
        try
        {
            return from.IsAssignableFrom(type);
        }
        catch (Exception e) when (Missing(e))
        {
            return false;
        }
    }

    private static bool Missing(Exception e) => e is TypeLoadException or System.IO.FileNotFoundException or System.IO.FileLoadException;

    private static bool References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Any(a => a.Name == typeof(NetworkBehaviour).Assembly.GetName().Name);

    // A cheap first pass over the raw IL, so only handlers with a length read get an ILHook. A stray 0x28 byte that
    // isn't a call just fails to resolve.
    private static bool ReadsLength(MethodInfo handler)
    {
        byte[]? il = handler.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return false;
        }
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28)
            {
                continue;
            }
            try
            {
                if (ElementSize(handler.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1))) != null)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return false;
    }

    // Handlers are NGO's generated (NetworkBehaviour target, FastBufferReader reader, __RpcParams rpcParams), and every
    // read is a call on ldarga reader, so the check slots in right before the call without touching the stack.
    private static void Guard(ILContext il)
    {
        ParameterDefinition reader = il.Method.Parameters[1];
        ParameterDefinition rpc = il.Method.Parameters[2];
        var cursor = new ILCursor(il);
        while (cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Call && i.Operand is MethodReference))
        {
            MethodBase? read = null;
            try
            {
                read = ((MethodReference)cursor.Next.Operand).ResolveReflection();
            }
            catch (Exception)
            {
            }
            if (ElementSize(read) is int size)
            {
                cursor.Emit(OpCodes.Ldarga, reader);
                cursor.Emit(OpCodes.Ldc_I4, size);
                cursor.Emit(OpCodes.Ldarg, rpc);
                cursor.Emit(OpCodes.Call, check);
            }
            cursor.Index++;
        }
    }

    // The smallest number of bytes each element can take on the wire, for a FastBufferReader read whose length comes
    // from the message, or null for any other call.
    private static int? ElementSize(MethodBase? read)
    {
        if (read?.DeclaringType != typeof(FastBufferReader) || !read.Name.StartsWith("Read", StringComparison.Ordinal))
        {
            return null;
        }
        ParameterInfo[] parameters = read.GetParameters();
        if (parameters.Length == 0 || !parameters[0].ParameterType.IsByRef)
        {
            return null;
        }
        Type target = parameters[0].ParameterType.GetElementType()!;
        if (target == typeof(string))
        {
            return 1;
        }
        Type? element = target.IsArray ? target.GetElementType()
            : target.IsGenericType && target.GetGenericTypeDefinition().FullName == "Unity.Collections.NativeArray`1"
                ? target.GetGenericArguments()[0]
                : null;
        if (element == null)
        {
            return null;
        }
        // Serializable types write what they like, maybe less than their size in memory.
        if (!element.IsValueType || typeof(INetworkSerializable).IsAssignableFrom(element))
        {
            return 1;
        }
        return (int)typeof(Lengths).GetMethod(nameof(SizeOf), Statics).MakeGenericMethod(element).Invoke(null, null);
    }

    private static unsafe int SizeOf<T>() where T : unmanaged => sizeof(T);
}
