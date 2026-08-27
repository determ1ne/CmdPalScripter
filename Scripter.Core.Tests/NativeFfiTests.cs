using System.Reflection;
using Scripter.Core;

namespace Scripter.Core.Tests;

public sealed class NativeFfiTests
{
    [Theory]
    [InlineData("winapi u32()", "winapi u32()", 0)]
    [InlineData(" CDECL  i32 ( i32, i16 ) ", "cdecl i32(i32,i16)", 2)]
    [InlineData("stdcall void(void)", "stdcall void()", 0)]
    [InlineData("winapi ptr(uptr)", "winapi ptr(uptr)", 1)]
    [InlineData("cdecl f64(f32, f64, i8, u8, i16, u16, i64, u64)", "cdecl f64(f32,f64,i8,u8,i16,u16,i64,u64)", 8)]
    public void ParsesAndNormalizesSupportedSignatures(string input, string expected, int parameterCount)
    {
        var signature = NativeSignatureParser.Parse(input);

        Assert.Equal(expected, signature.CanonicalText);
        Assert.Equal(parameterCount, signature.ParameterTypes.Length);
    }

    [Theory]
    [InlineData("fastcall i32()")]
    [InlineData("winapi string()")]
    [InlineData("winapi i32(void, i32)")]
    [InlineData("winapi i32(i32,)")]
    public void RejectsUnsupportedOrMalformedSignatures(string signature)
    {
        Assert.Throws<FormatException>(() => NativeSignatureParser.Parse(signature));
    }

    [Fact]
    public void GeneratedCallStubIsCachedByNormalizedSignature()
    {
        var first = NativeSignatureParser.Parse("WINAPI i32(i32)");
        var second = NativeSignatureParser.Parse("winapi i32( i32 )");

        Assert.Same(NativeCallStubCache.GetOrCreate(first), NativeCallStubCache.GetOrCreate(second));
    }

    [Fact]
    public void NormalizesEveryPrimitiveToItsExactClrType()
    {
        Assert.IsType<sbyte>(NativeValueConverter.Normalize(1, NativeValueType.I8, 0));
        Assert.IsType<byte>(NativeValueConverter.Normalize(1, NativeValueType.U8, 0));
        Assert.IsType<short>(NativeValueConverter.Normalize(1, NativeValueType.I16, 0));
        Assert.IsType<ushort>(NativeValueConverter.Normalize(1, NativeValueType.U16, 0));
        Assert.IsType<int>(NativeValueConverter.Normalize(1, NativeValueType.I32, 0));
        Assert.IsType<uint>(NativeValueConverter.Normalize(1, NativeValueType.U32, 0));
        Assert.IsType<long>(NativeValueConverter.Normalize(1, NativeValueType.I64, 0));
        Assert.IsType<ulong>(NativeValueConverter.Normalize(1, NativeValueType.U64, 0));
        Assert.IsType<float>(NativeValueConverter.Normalize(1, NativeValueType.F32, 0));
        Assert.IsType<double>(NativeValueConverter.Normalize(1, NativeValueType.F64, 0));
        Assert.IsType<nint>(NativeValueConverter.Normalize(1, NativeValueType.Pointer, 0));
        Assert.IsType<nuint>(NativeValueConverter.Normalize(1, NativeValueType.UnsignedPointer, 0));
    }

    [Fact]
    public void CallsAWindowsExportWithPrimitiveArguments()
    {
        using var ffi = new NativeFfi();
        var mulDiv = ffi.Bind("kernel32.dll", "MulDiv", "winapi i32(i32, i32, i32)");
        var stdCallMulDiv = ffi.Bind("kernel32.dll", "MulDiv", "stdcall i32(i32, i32, i32)");
        var absoluteValue = ffi.Bind("msvcrt.dll", "abs", "cdecl i32(i32)");

        Assert.Equal(42, mulDiv.Invoke(6, 7, 1));
        Assert.Equal(42, stdCallMulDiv.Invoke(6, 7, 1));
        Assert.Equal(7, absoluteValue.Invoke(-7));
    }

    [Fact]
    public void CallsVoidAndPointerFunctions()
    {
        using var ffi = new NativeFfi();
        var sleep = ffi.Bind("kernel32.dll", "Sleep", "winapi void(u32)");
        var process = ffi.Bind("kernel32.dll", "GetCurrentProcess", "winapi ptr()");

        Assert.Null(sleep.Invoke(0));
        Assert.IsType<nint>(process.Invoke());
    }

    [Fact]
    public void RejectsWrongArgumentCountsAndDisposedLibraries()
    {
        var ffi = new NativeFfi();
        var function = ffi.Bind("kernel32.dll", "GetCurrentProcessId", "winapi u32()");

        Assert.Throws<TargetParameterCountException>(() => function.Invoke(1));
        ffi.Dispose();
        Assert.Throws<ObjectDisposedException>(() => function.Invoke());
    }

    [Fact]
    public void RejectsOutOfRangeOrdinalsBeforeLoadingTheLibrary()
    {
        using var ffi = new NativeFfi();

        Assert.Throws<ArgumentOutOfRangeException>(() => ffi.BindOrdinal("missing.dll", -1, "winapi u32()"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ffi.BindOrdinal("missing.dll", 65536, "winapi u32()"));
    }
}
