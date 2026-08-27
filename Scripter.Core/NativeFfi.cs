using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Microsoft.ClearScript;

namespace Scripter.Core;

public sealed class NativeFfi : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NativeLibraryModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public NativeFunction Bind(string libraryName, string exportName, string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        if (exportName.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A DLL export name cannot contain a null character.", nameof(exportName));
        }

        var parsedSignature = NativeSignatureParser.Parse(signature);
        var module = GetOrLoadModule(libraryName);
        return new NativeFunction(module, module.GetExport(exportName), parsedSignature);
    }

    public NativeFunction BindOrdinal(string libraryName, int ordinal, string signature)
    {
        if ((uint)ordinal > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "A DLL export ordinal must be between 0 and 65535.");
        }

        var parsedSignature = NativeSignatureParser.Parse(signature);
        var module = GetOrLoadModule(libraryName);
        return new NativeFunction(module, module.GetExport(ordinal), parsedSignature);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var module in _modules.Values)
            {
                module.Dispose();
            }

            _modules.Clear();
        }
    }

    private NativeLibraryModule GetOrLoadModule(string libraryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryName);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Native FFI is currently supported only on Windows.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_modules.TryGetValue(libraryName, out var module))
            {
                module = new NativeLibraryModule(libraryName, NativeLibrary.Load(libraryName));
                _modules.Add(libraryName, module);
            }

            return module;
        }
    }
}

public sealed class NativeFunction
{
    private readonly NativeLibraryModule _module;
    private readonly nint _functionPointer;
    private readonly NativeSignature _signature;
    private readonly NativeCallInvoker _invoker;

    internal NativeFunction(NativeLibraryModule module, nint functionPointer, NativeSignature signature)
    {
        _module = module;
        _functionPointer = functionPointer;
        _signature = signature;
        _invoker = NativeCallStubCache.GetOrCreate(signature);
    }

    public object? Invoke(params object?[] arguments)
    {
        arguments ??= [];
        if (arguments.Length != _signature.ParameterTypes.Length)
        {
            throw new TargetParameterCountException(
                $"Native function signature '{_signature.CanonicalText}' expects {_signature.ParameterTypes.Length} argument(s), but received {arguments.Length}.");
        }

        var normalizedArguments = new object?[_signature.ParameterTypes.Length];
        for (var index = 0; index < normalizedArguments.Length; index++)
        {
            normalizedArguments[index] = NativeValueConverter.Normalize(arguments[index], _signature.ParameterTypes[index], index);
        }

        return _module.Invoke(_functionPointer, _invoker, normalizedArguments);
    }
}

internal sealed class NativeLibraryModule : IDisposable
{
    private readonly object _gate = new();
    private nint _handle;

    public NativeLibraryModule(string libraryName, nint handle)
    {
        LibraryName = libraryName;
        _handle = handle;
    }

    public string LibraryName { get; }

    public nint GetExport(string exportName)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            var address = NativeMethods.GetProcAddressByName(_handle, exportName);
            return address != 0
                ? address
                : throw CreateEntryPointException(exportName);
        }
    }

    public nint GetExport(int ordinal)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            var address = NativeMethods.GetProcAddressByOrdinal(_handle, ordinal);
            return address != 0
                ? address
                : throw CreateEntryPointException($"ordinal {ordinal}");
        }
    }

    public object? Invoke(nint functionPointer, NativeCallInvoker invoker, object?[] arguments)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            return invoker(functionPointer, arguments);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_handle == 0)
            {
                return;
            }

            NativeLibrary.Free(_handle);
            _handle = 0;
        }
    }

    private EntryPointNotFoundException CreateEntryPointException(string export) =>
        new($"Could not find export '{export}' in native library '{LibraryName}'.", new Win32Exception(Marshal.GetLastPInvokeError()));

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_handle == 0, this);
}

internal static partial class NativeMethods
{
    [SuppressMessage("Interoperability", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "GetProcAddress requires an ANSI export name.")]
    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true, CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern nint GetProcAddressByName(nint module, [MarshalAs(UnmanagedType.LPStr)] string exportName);

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true, SetLastError = true)]
    internal static extern nint GetProcAddressByOrdinal(nint module, nint ordinal);
}

internal enum NativeValueType
{
    Void,
    I8,
    U8,
    I16,
    U16,
    I32,
    U32,
    I64,
    U64,
    F32,
    F64,
    Pointer,
    UnsignedPointer,
}

internal sealed record NativeSignature(
    CallingConvention CallingConvention,
    NativeValueType ReturnType,
    NativeValueType[] ParameterTypes,
    string CanonicalText);

internal static class NativeSignatureParser
{
    public static NativeSignature Parse(string signature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signature);
        var openParenthesis = signature.IndexOf('(');
        var closeParenthesis = signature.LastIndexOf(')');
        if (openParenthesis < 0
            || closeParenthesis < openParenthesis
            || !string.IsNullOrWhiteSpace(signature[(closeParenthesis + 1)..]))
        {
            throw InvalidSignature(signature);
        }

        var prefix = signature[..openParenthesis]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (prefix.Length != 2)
        {
            throw InvalidSignature(signature);
        }

        var callingConvention = ParseCallingConvention(prefix[0], signature);
        var returnType = ParseValueType(prefix[1], allowVoid: true, signature);
        var parameterText = signature[(openParenthesis + 1)..closeParenthesis].Trim();
        NativeValueType[] parameterTypes;
        if (parameterText.Length == 0 || string.Equals(parameterText, "void", StringComparison.OrdinalIgnoreCase))
        {
            parameterTypes = [];
        }
        else
        {
            var parameters = parameterText.Split(',', StringSplitOptions.TrimEntries);
            if (parameters.Any(string.IsNullOrWhiteSpace))
            {
                throw InvalidSignature(signature);
            }

            parameterTypes = parameters.Select(parameter => ParseValueType(parameter, allowVoid: false, signature)).ToArray();
        }

        var conventionName = callingConvention switch
        {
            CallingConvention.Winapi => "winapi",
            CallingConvention.Cdecl => "cdecl",
            CallingConvention.StdCall => "stdcall",
            _ => throw InvalidSignature(signature),
        };
        var returnName = GetTypeName(returnType);
        var parameterNames = string.Join(',', parameterTypes.Select(GetTypeName));
        return new NativeSignature(callingConvention, returnType, parameterTypes, $"{conventionName} {returnName}({parameterNames})");
    }

    private static CallingConvention ParseCallingConvention(string value, string signature) => value.ToLowerInvariant() switch
    {
        "winapi" => CallingConvention.Winapi,
        "cdecl" => CallingConvention.Cdecl,
        "stdcall" => CallingConvention.StdCall,
        _ => throw new FormatException($"Unsupported calling convention '{value}' in native signature '{signature}'."),
    };

    private static NativeValueType ParseValueType(string value, bool allowVoid, string signature)
    {
        var type = value.ToLowerInvariant() switch
        {
            "void" => NativeValueType.Void,
            "i8" => NativeValueType.I8,
            "u8" => NativeValueType.U8,
            "i16" => NativeValueType.I16,
            "u16" => NativeValueType.U16,
            "i32" => NativeValueType.I32,
            "u32" => NativeValueType.U32,
            "i64" => NativeValueType.I64,
            "u64" => NativeValueType.U64,
            "f32" => NativeValueType.F32,
            "f64" => NativeValueType.F64,
            "ptr" => NativeValueType.Pointer,
            "uptr" => NativeValueType.UnsignedPointer,
            _ => throw new FormatException($"Unsupported native type '{value}' in signature '{signature}'."),
        };

        if (!allowVoid && type == NativeValueType.Void)
        {
            throw new FormatException($"The void type cannot be used as a parameter in native signature '{signature}'.");
        }

        return type;
    }

    private static string GetTypeName(NativeValueType type) => type switch
    {
        NativeValueType.Void => "void",
        NativeValueType.I8 => "i8",
        NativeValueType.U8 => "u8",
        NativeValueType.I16 => "i16",
        NativeValueType.U16 => "u16",
        NativeValueType.I32 => "i32",
        NativeValueType.U32 => "u32",
        NativeValueType.I64 => "i64",
        NativeValueType.U64 => "u64",
        NativeValueType.F32 => "f32",
        NativeValueType.F64 => "f64",
        NativeValueType.Pointer => "ptr",
        NativeValueType.UnsignedPointer => "uptr",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static FormatException InvalidSignature(string signature) =>
        new($"Invalid native signature '{signature}'. Expected '<winapi|cdecl|stdcall> <return-type>(<parameter-types>)'.");
}

internal delegate object? NativeCallInvoker(nint functionPointer, object?[] arguments);

internal static class NativeCallStubCache
{
    private static readonly ConcurrentDictionary<string, NativeCallInvoker> Cache = new(StringComparer.Ordinal);

    public static NativeCallInvoker GetOrCreate(NativeSignature signature) =>
        Cache.GetOrAdd(signature.CanonicalText, _ => CreateStub(signature));

    private static NativeCallInvoker CreateStub(NativeSignature signature)
    {
        var parameterTypes = signature.ParameterTypes.Select(GetClrType).ToArray();
        var returnType = GetClrType(signature.ReturnType);
        var method = new DynamicMethod(
            "ScripterNativeFfiCall",
            typeof(object),
            [typeof(nint), typeof(object[])],
            typeof(NativeCallStubCache).Module,
            skipVisibility: true);
        var generator = method.GetILGenerator();
        for (var index = 0; index < parameterTypes.Length; index++)
        {
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldc_I4, index);
            generator.Emit(OpCodes.Ldelem_Ref);
            generator.Emit(OpCodes.Unbox_Any, parameterTypes[index]);
        }

        generator.Emit(OpCodes.Ldarg_0);
        generator.EmitCalli(OpCodes.Calli, signature.CallingConvention, returnType, parameterTypes);
        if (returnType == typeof(void))
        {
            generator.Emit(OpCodes.Ldnull);
        }
        else
        {
            generator.Emit(OpCodes.Box, returnType);
        }

        generator.Emit(OpCodes.Ret);
        return (NativeCallInvoker)method.CreateDelegate(typeof(NativeCallInvoker));
    }

    private static Type GetClrType(NativeValueType type) => type switch
    {
        NativeValueType.Void => typeof(void),
        NativeValueType.I8 => typeof(sbyte),
        NativeValueType.U8 => typeof(byte),
        NativeValueType.I16 => typeof(short),
        NativeValueType.U16 => typeof(ushort),
        NativeValueType.I32 => typeof(int),
        NativeValueType.U32 => typeof(uint),
        NativeValueType.I64 => typeof(long),
        NativeValueType.U64 => typeof(ulong),
        NativeValueType.F32 => typeof(float),
        NativeValueType.F64 => typeof(double),
        NativeValueType.Pointer => typeof(nint),
        NativeValueType.UnsignedPointer => typeof(nuint),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

internal static class NativeValueConverter
{
    public static object Normalize(object? value, NativeValueType type, int index)
    {
        try
        {
            return type switch
            {
                NativeValueType.I8 => (object)Convert.ToSByte(value, CultureInfo.InvariantCulture),
                NativeValueType.U8 => (object)Convert.ToByte(value, CultureInfo.InvariantCulture),
                NativeValueType.I16 => (object)Convert.ToInt16(value, CultureInfo.InvariantCulture),
                NativeValueType.U16 => (object)Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                NativeValueType.I32 => (object)Convert.ToInt32(value, CultureInfo.InvariantCulture),
                NativeValueType.U32 => (object)Convert.ToUInt32(value, CultureInfo.InvariantCulture),
                NativeValueType.I64 => (object)Convert.ToInt64(value, CultureInfo.InvariantCulture),
                NativeValueType.U64 => (object)Convert.ToUInt64(value, CultureInfo.InvariantCulture),
                NativeValueType.F32 => (object)Convert.ToSingle(value, CultureInfo.InvariantCulture),
                NativeValueType.F64 => (object)Convert.ToDouble(value, CultureInfo.InvariantCulture),
                NativeValueType.Pointer => (object)ToPointer(value),
                NativeValueType.UnsignedPointer => (object)ToUnsignedPointer(value),
                NativeValueType.Void => throw new InvalidOperationException("Void is not a valid native argument type."),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new ArgumentException($"Argument {index} cannot be converted to native type '{type}'.", nameof(value), exception);
        }
    }

    private static nint ToPointer(object? value)
    {
        if (value is null or Undefined)
        {
            return 0;
        }

        return value switch
        {
            nint pointer => pointer,
            nuint unsignedPointer => checked((nint)unsignedPointer),
            _ => checked((nint)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        };
    }

    private static nuint ToUnsignedPointer(object? value)
    {
        if (value is null or Undefined)
        {
            return 0;
        }

        return value switch
        {
            nuint pointer => pointer,
            nint signedPointer => checked((nuint)signedPointer),
            _ => checked((nuint)Convert.ToUInt64(value, CultureInfo.InvariantCulture)),
        };
    }
}
