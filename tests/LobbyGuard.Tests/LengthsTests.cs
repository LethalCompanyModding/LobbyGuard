using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using MonoMod.Cil;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using Xunit;

namespace LobbyGuard.Tests;

public sealed class LengthsTests
{
    [Fact]
    public void LengthMustFitWhatsLeft()
    {
        Assert.True(Lengths.Fits(3, 4, 12));
        Assert.True(Lengths.Fits(0, 4, 0));
        Assert.False(Lengths.Fits(4, 4, 12));
        Assert.False(Lengths.Fits(-1, 1, 100));
        Assert.False(Lengths.Fits(int.MaxValue, 8, int.MaxValue));
    }

    [Fact]
    public void AStringLengthThatDoesntFitIsRejected()
    {
        // Lengths NGO reads as uint: 0x40000000 chars is 2 GB as UTF-16, the rest are negative as an int.
        foreach (uint claimed in new uint[] { 0x40000000, 0x80000000, 0xFFFFFFFF })
        {
            Assert.False(Lengths.LengthFits(100, 0, unchecked((int)claimed), 1));
        }
        Assert.True(Lengths.LengthFits(100, 0, 96, 1));
        Assert.False(Lengths.LengthFits(100, 0, 97, 1));
        // Too short for a length at all is NGO's own error.
        Assert.True(Lengths.LengthFits(3, 0, 12345, 1));
    }

    private static MethodInfo Method(Type type, string name, Func<ParameterInfo[], bool> match) =>
        type.GetMethods().Single(m => m.Name == name && match(m.GetParameters()));

    private static bool Takes(ParameterInfo[] parameters, Type target, Type? marker = null) =>
        parameters[0].ParameterType == target.MakeByRefType() && (marker == null || parameters.Length > 1 && parameters[1].ParameterType == marker);

    private struct Serializable : INetworkSerializable
    {
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
        }
    }

    [Fact]
    public void FastBufferReaderReadsHaveElementSizes()
    {
        Type reader = typeof(FastBufferReader);
        Assert.Equal(1, Lengths.ElementSize(Method(reader, "ReadValueSafe", p => Takes(p, typeof(string)))));
        Assert.Equal(1, Lengths.ElementSize(Method(reader, "ReadValue", p => Takes(p, typeof(string)))));
        Assert.Equal(12, Lengths.ElementSize(Method(reader, "ReadValueSafe", p => Takes(p, typeof(Vector3[])))));
        Assert.Null(Lengths.ElementSize(Method(reader, "ReadValueSafe", p => Takes(p, typeof(Vector3)))));
        Assert.Null(Lengths.ElementSize(Method(reader, "ReadByteSafe", p => p.Length == 1 && p[0].ParameterType == typeof(byte).MakeByRefType())));
    }

    [Fact]
    public void BufferSerializerReadsHaveElementSizes()
    {
        Type serializer = typeof(BufferSerializer<>);
        Assert.Equal(1, Lengths.ElementSize(Method(serializer, "SerializeValue", p => Takes(p, typeof(string)))));
        Assert.Equal(1, Lengths.ElementSize(Method(serializer, "SerializeValuePreChecked", p => Takes(p, typeof(string)))));
        Assert.Equal(12, Lengths.ElementSize(Method(serializer, "SerializeValue", p => Takes(p, typeof(Vector3[])))));
        Assert.Null(Lengths.ElementSize(Method(serializer, "SerializeValue", p => Takes(p, typeof(Vector3)))));
        Assert.Null(Lengths.ElementSize(Method(serializer, "SerializeValue", p => Takes(p, typeof(byte)))));

        MethodInfo primitives = Method(serializer, "SerializeValue", p => p[0].ParameterType.IsByRef && p[0].ParameterType.GetElementType()!.IsArray &&
            p.Length == 2 && p[1].ParameterType == typeof(FastBufferWriter.ForPrimitives));
        // Inside a generic NetworkSerialize the element is still a type parameter.
        Assert.Equal(1, Lengths.ElementSize(primitives));
        Assert.Equal(4, Lengths.ElementSize(primitives.MakeGenericMethod(typeof(int))));
        Assert.Equal(8, Lengths.ElementSize(primitives.MakeGenericMethod(typeof(double))));

        MethodInfo serializables = Method(serializer, "SerializeValue", p => p[0].ParameterType.IsByRef && p[0].ParameterType.GetElementType()!.IsArray &&
            p.Length == 2 && p[1].ParameterType == typeof(FastBufferWriter.ForNetworkSerializable));
        Assert.Equal(1, Lengths.ElementSize(serializables.MakeGenericMethod(typeof(Serializable))));

        MethodInfo native = Method(serializer, "SerializeValue", p => p[0].ParameterType.IsByRef && p[0].ParameterType.GetElementType()!.IsGenericType &&
            p[0].ParameterType.GetElementType()!.GetGenericTypeDefinition() == typeof(NativeArray<>) && p.Length == 3 &&
            p[2].ParameterType == typeof(FastBufferWriter.ForGeneric));
        Assert.Equal(2, Lengths.ElementSize(native.MakeGenericMethod(typeof(short))));
    }

    // Methods that are only read, never run: the guard works on their IL.
    private static class Reads
    {
        public static void ByValue(FastBufferReader reader) => reader.ReadValueSafe(out string _);

        public static void ByRef(ref FastBufferReader reader) => reader.ReadValueSafe(out int[] _);

        public static void Handler(NetworkBehaviour target, FastBufferReader reader, __RpcParams rpcParams) => reader.ReadValueSafe(out string _);

        public static void Number(FastBufferReader reader) => reader.ReadValueSafe(out int _);

        public static void Copy(FastBufferReader reader)
        {
            FastBufferReader other = reader;
            other.ReadValueSafe(out string _);
        }

        public static void Serialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            string text = "";
            serializer.SerializeValue(ref text);
            Vector3[] points = [];
            serializer.SerializeValuePreChecked(ref points);
        }
    }

    private static List<string> Guarded(string name)
    {
        MethodInfo method = typeof(Reads).GetMethod(name, BindingFlags.Static | BindingFlags.Public)!;
        Lengths.Hookable? plan = Lengths.Plan(method);
        using ModuleDefinition module = ModuleDefinition.ReadModule(typeof(Reads).Assembly.Location);
        TypeDefinition type = module.GetTypes().Single(t => t.FullName == typeof(Reads).FullName!.Replace('+', '/'));
        var il = new ILContext(type.Methods.Single(m => m.Name == name));
        if (plan != null)
        {
            Lengths.Guard(il, plan.Sizes);
        }
        return il.Instrs.Select(i => i.Operand).OfType<MethodReference>().Where(m => m.DeclaringType.Name == nameof(Lengths)).Select(m => m.Name).ToList();
    }

    [Fact]
    public void ReadsOnAReaderParameterAreChecked()
    {
        Assert.Equal(["CheckMessage"], Guarded(nameof(Reads.ByValue)));
        Assert.Equal(["CheckMessage"], Guarded(nameof(Reads.ByRef)));
        Assert.Equal(["CheckRpc"], Guarded(nameof(Reads.Handler)));
        Assert.Equal(["CheckSerializer", "CheckSerializer"], Guarded(nameof(Reads.Serialize)));
    }

    [Fact]
    public void OtherReadsAreLeftAlone()
    {
        Assert.Empty(Guarded(nameof(Reads.Number)));
        Assert.Empty(Guarded(nameof(Reads.Copy)));
    }

    [Fact]
    public void ATypeThatCantLoadIsSkipped()
    {
        Assert.True(Lengths.Derives(typeof(string), typeof(object)));
        Assert.False(Lengths.Derives(new Unloadable(), typeof(object)));
    }

    // Fails the way Mono does on a type naming an assembly that isn't installed.
    private sealed class Unloadable() : TypeDelegator(typeof(object))
    {
        public override Type UnderlyingSystemType => throw new TypeLoadException("Could not load file or assembly 'Missing'");
    }
}
