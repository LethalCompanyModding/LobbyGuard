using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using Unity.Netcode;

namespace LobbyGuard;

// A length read from a message makes NGO allocate before it looks at how much message there is. Every method that
// reads from a FastBufferReader or a BufferSerializer gets a check in front of its array, string and NativeArray reads:
// a length that can't fit in what's left drops the message and, on the host, disconnects the sender.
internal static class Lengths
{
    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Members = Statics | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    private static readonly List<ILHook> hooks = new();

    private static readonly MethodInfo checkRpc = typeof(Lengths).GetMethod(nameof(CheckRpc), Statics);
    private static readonly MethodInfo checkMessage = typeof(Lengths).GetMethod(nameof(CheckMessage), Statics);
    private static readonly MethodInfo checkSerializer = typeof(Lengths).GetMethod(nameof(CheckSerializer), Statics);

    // Runs in Awake before the other guards go in, so nothing here may throw.
    public static void HookAll()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Hook(e.LoadedAssembly);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Hook(assembly);
            }
            Plugin.Log.LogInfo($"Length checks in {hooks.Count} methods, found in {watch.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Length checks are off: {e.Message}");
        }
    }

    public static bool Fits(int count, int size, int remaining) => count >= 0 && (long)count * size <= remaining;

    // A length is the four bytes at the reader's position. A message too short for one gets NGO's own error.
    internal static bool LengthFits(int total, int position, int claimed, int size)
    {
        int remaining = total - position - sizeof(int);
        return remaining < 0 || Fits(claimed, size, remaining);
    }

    private static void CheckRpc(ref FastBufferReader reader, int size, __RpcParams rpc) =>
        Verify(ref reader, size, rpc.Server.Receive.SenderClientId);

    private static void CheckMessage(ref FastBufferReader reader, int size) => Verify(ref reader, size, Plugin.handling);

    // The reader a serializer hands out shares its position with the serializer.
    private static void CheckSerializer<TReaderWriter>(ref BufferSerializer<TReaderWriter> serializer, int size)
        where TReaderWriter : IReaderWriter
    {
        if (serializer.IsReader)
        {
            FastBufferReader reader = serializer.GetFastBufferReader();
            Verify(ref reader, size, Plugin.handling);
        }
    }

    private static unsafe void Verify(ref FastBufferReader reader, int size, ulong sender)
    {
        if (!reader.IsInitialized || LengthFits(reader.Length, reader.Position, *(int*)reader.GetUnsafePtrAtCurrentPosition(), size))
        {
            return;
        }
        NetworkManager? manager = NetworkManager.Singleton;
        if (manager != null && manager.IsServer && sender != NetworkManager.ServerClientId)
        {
            Plugin.Kick(manager, sender, "array or string longer than its message");
        }
        throw new OverflowException("array or string longer than its message");
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
            Plugin.Log.LogWarning($"Skipped length checks in {assembly.GetName().Name}: {e.Message}");
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
            // ponytail: methods of an open generic type are left alone, since their reads can't be hooked once for every type argument.
            if (type == null || type.IsGenericTypeDefinition)
            {
                continue;
            }
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(Members);
            }
            catch (Exception e) when (Missing(e))
            {
                continue;
            }
            foreach (MethodInfo method in methods)
            {
                try
                {
                    if (Plan(method) is { } plan)
                    {
                        hooks.Add(new ILHook(plan.Method, il => Guard(il, plan.Sizes)));
                    }
                }
                catch (Exception e)
                {
                    if (!Missing(e))
                    {
                        Plugin.Log.LogWarning($"Skipped length checks in {type.FullName}.{method.Name}: {e.Message}");
                    }
                }
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

    // The element size of each length read's call, by metadata token and by name. A hook's copy of the method has new
    // references without the original's tokens, so only the name finds them there.
    internal sealed class Sizes
    {
        public Dictionary<int, int> ByToken { get; } = new();

        public Dictionary<string, int> ByName { get; } = new();

        public int Count => ByToken.Count;

        public int? Find(MethodReference callee) =>
            ByToken.TryGetValue(callee.MetadataToken.ToInt32(), out int size) || ByName.TryGetValue(callee.FullName, out size) ? size : null;
    }

    internal sealed class Hookable(MethodBase method, Sizes sizes)
    {
        public MethodBase Method { get; } = method;

        public Sizes Sizes { get; } = sizes;
    }

    private static Type Unref(Type type) => type.IsByRef ? type.GetElementType()! : type;

    private static bool IsSerializer(Type type) =>
        Unref(type) is { IsGenericType: true } generic && generic.GetGenericTypeDefinition() == typeof(BufferSerializer<>);

    // Parameter types come first, since most methods have no reader and the rest of the scan reads their IL.
    internal static Hookable? Plan(MethodInfo method)
    {
        if (!method.GetParameters().Any(p => Unref(p.ParameterType) == typeof(FastBufferReader) || IsSerializer(p.ParameterType)))
        {
            return null;
        }
        MethodBase target = method;
        if (method.IsGenericMethodDefinition)
        {
            // A generic NetworkSerialize<T> has its own code for each reader or writer type, and only the reader's matters.
            // ponytail: only a method generic over its one serializer type is closed; any other generic method is left alone.
            Type[] arguments = method.GetGenericArguments();
            if (arguments.Length != 1 || !method.GetParameters().Any(p => IsSerializer(p.ParameterType) && Unref(p.ParameterType).GetGenericArguments()[0] == arguments[0]))
            {
                return null;
            }
            try
            {
                target = method.MakeGenericMethod(typeof(BufferSerializerReader));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        byte[]? il = target.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return null;
        }
        Type[]? methodArguments = target.IsGenericMethod ? target.GetGenericArguments() : null;
        var sizes = new Sizes();
        using ModuleDefinition scratch = ModuleDefinition.CreateModule("LobbyGuard.Names", ModuleKind.Dll);
        // A stray 0x28 byte that isn't a call just fails to resolve.
        for (int i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != 0x28)
            {
                continue;
            }
            int token = BitConverter.ToInt32(il, i + 1);
            if (sizes.ByToken.ContainsKey(token))
            {
                continue;
            }
            try
            {
                MethodBase read = target.Module.ResolveMethod(token, null, methodArguments);
                if (ElementSize(read) is int size)
                {
                    sizes.ByToken[token] = size;
                    sizes.ByName[scratch.ImportReference(read).FullName] = size;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return sizes.Count == 0 ? null : new Hookable(target, sizes);
    }

    // The reads are calls on a reader parameter, so the check slots in right before the call without touching the stack.
    internal static void Guard(ILContext il, Sizes sizes)
    {
        var checks = new List<(Instruction Call, ParameterDefinition Reader, int Size)>();
        for (int i = 0; i < il.Instrs.Count; i++)
        {
            Instruction call = il.Instrs[i];
            if (call.OpCode == OpCodes.Call && call.Operand is MethodReference callee && sizes.Find(callee) is int size &&
                Receiver(il, i, callee) is { } reader)
            {
                checks.Add((call, reader, size));
            }
        }
        if (checks.Count == 0)
        {
            return;
        }
        ParameterDefinition? rpc = il.Method.Parameters.FirstOrDefault(p => p.ParameterType.FullName == typeof(__RpcParams).FullName);
        var cursor = new ILCursor(il);
        foreach ((Instruction call, ParameterDefinition reader, int size) in checks)
        {
            cursor.Goto(call, MoveType.Before);
            cursor.Emit(reader.ParameterType.IsByReference ? OpCodes.Ldarg : OpCodes.Ldarga, reader);
            cursor.Emit(OpCodes.Ldc_I4, size);
            TypeReference type = reader.ParameterType is ByReferenceType byRef ? byRef.ElementType : reader.ParameterType;
            if (type is GenericInstanceType serializer)
            {
                var generic = new GenericInstanceMethod(il.Import(checkSerializer));
                generic.GenericArguments.Add(serializer.GenericArguments[0]);
                cursor.Emit(OpCodes.Call, generic);
            }
            else if (rpc != null)
            {
                cursor.Emit(OpCodes.Ldarg, rpc);
                cursor.Emit(OpCodes.Call, checkRpc);
            }
            else
            {
                cursor.Emit(OpCodes.Call, checkMessage);
            }
        }
    }

    // The reader parameter a read at il.Instrs[call] is called on, or null when it's called on anything else (a local,
    // a field, another reader), where a check on a parameter would look at the wrong message. Walks back over the
    // straight-line code that pushed the call's arguments to find what pushed the first one.
    private static ParameterDefinition? Receiver(ILContext il, int call, MethodReference callee)
    {
        int arguments = callee.Parameters.Count + 1;
        int need = arguments;
        int start = -1;
        for (int i = call - 1; i >= 0 && start < 0; i--)
        {
            if (Effect(il.Instrs[i]) is not var (pop, push))
            {
                return null;
            }
            need -= push;
            if (need < 0)
            {
                return null;
            }
            need += pop;
            if (need == 0)
            {
                start = i;
            }
        }
        if (start < 0 || ParameterOf(il.Method, il.Instrs[start]) is not { } parameter || !SameType(parameter.ParameterType, callee.DeclaringType))
        {
            return null;
        }
        // The first argument must reach the call as pushed, not changed on the way (a field of the parameter, say).
        int depth = 1;
        for (int i = start + 1; i < call; i++)
        {
            if (Effect(il.Instrs[i]) is not var (pop, push))
            {
                return null;
            }
            depth -= pop;
            if (depth < 1)
            {
                return null;
            }
            depth += push;
        }
        return depth == arguments ? parameter : null;
    }

    private static bool SameType(TypeReference parameter, TypeReference owner) =>
        (parameter is ByReferenceType byRef ? byRef.ElementType : parameter).GetElementType().FullName == owner.GetElementType().FullName;

    private static ParameterDefinition? ParameterOf(MethodDefinition method, Instruction load)
    {
        int index;
        if (load.OpCode == OpCodes.Ldarg_0) index = 0;
        else if (load.OpCode == OpCodes.Ldarg_1) index = 1;
        else if (load.OpCode == OpCodes.Ldarg_2) index = 2;
        else if (load.OpCode == OpCodes.Ldarg_3) index = 3;
        else if (load.OpCode == OpCodes.Ldarg || load.OpCode == OpCodes.Ldarg_S || load.OpCode == OpCodes.Ldarga || load.OpCode == OpCodes.Ldarga_S)
        {
            return load.Operand as ParameterDefinition;
        }
        else return null;
        index -= method.HasThis ? 1 : 0;
        return index >= 0 && index < method.Parameters.Count ? method.Parameters[index] : null;
    }

    // What an instruction takes off the evaluation stack and puts on it, or null for one that isn't straight-line code.
    private static (int Pop, int Push)? Effect(Instruction instruction)
    {
        OpCode op = instruction.OpCode;
        if (op.FlowControl is not (FlowControl.Next or FlowControl.Call))
        {
            return null;
        }
        MethodReference? method = instruction.Operand as MethodReference;
        int pop = op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
                StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8 or
                StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
            StackBehaviour.Varpop when method != null && op != OpCodes.Calli =>
                method.Parameters.Count + (method.HasThis && !method.ExplicitThis && op != OpCodes.Newobj ? 1 : 0),
            _ => -1,
        };
        int push = op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or
                StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            StackBehaviour.Varpush when method != null =>
                op == OpCodes.Newobj || method.ReturnType.MetadataType != MetadataType.Void ? 1 : 0,
            _ => -1,
        };
        return pop < 0 || push < 0 ? null : (pop, push);
    }

    // The smallest number of bytes each element can take on the wire, for a FastBufferReader or BufferSerializer read
    // whose length comes from the message, or null for any other call.
    internal static int? ElementSize(MethodBase? read)
    {
        Type? owner = read?.DeclaringType;
        if (read == null || owner == null)
        {
            return null;
        }
        bool reads = owner == typeof(FastBufferReader) && read.Name.StartsWith("Read", StringComparison.Ordinal);
        bool serializes = owner.IsGenericType && owner.GetGenericTypeDefinition() == typeof(BufferSerializer<>) &&
            read.Name.StartsWith("SerializeValue", StringComparison.Ordinal);
        if (!reads && !serializes)
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
        // Serializable types write what they like, maybe less than their size in memory, and an element that is still a
        // type parameter has no size yet.
        if (element.ContainsGenericParameters || !element.IsValueType || typeof(INetworkSerializable).IsAssignableFrom(element))
        {
            return 1;
        }
        try
        {
            // An empty struct still takes room in an array.
            return Math.Max(1, (int)typeof(Lengths).GetMethod(nameof(SizeOf), Statics).MakeGenericMethod(element).Invoke(null, null));
        }
        catch (ArgumentException)
        {
            return 1;
        }
    }

    private static unsafe int SizeOf<T>() where T : unmanaged => sizeof(T);
}
