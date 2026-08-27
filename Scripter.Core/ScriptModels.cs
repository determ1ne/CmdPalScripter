using System.Text.Json.Serialization;

namespace Scripter.Core;

public sealed record ScriptNativeType(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("typeName")] string TypeName);

public sealed record ScriptMetadata(
    string Name,
    string Description,
    IReadOnlyList<ScriptNativeType> NativeTypes,
    bool DynamicImport,
    bool CommandExecution,
    bool NativeFfi,
    string Type,
    IReadOnlyList<string> Export)
{
    public const string ClearScriptType = "clearscript";

    public ScriptMetadata(string name, string description)
        : this(name, description, [], false, false, false, ClearScriptType, [])
    {
    }

    public ScriptMetadata(
        string name,
        string description,
        IReadOnlyList<ScriptNativeType> nativeTypes,
        bool dynamicImport,
        bool commandExecution)
        : this(name, description, nativeTypes, dynamicImport, commandExecution, false, ClearScriptType, [])
    {
    }

    public ScriptMetadata(
        string name,
        string description,
        IReadOnlyList<ScriptNativeType> nativeTypes,
        bool dynamicImport,
        bool commandExecution,
        bool nativeFfi)
        : this(name, description, nativeTypes, dynamicImport, commandExecution, nativeFfi, ClearScriptType, [])
    {
    }
}

public sealed class ScriptMetadataFile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("nativeTypes")]
    public List<ScriptNativeType> NativeTypes { get; set; } = [];

    [JsonPropertyName("dynamicImport")]
    public bool DynamicImport { get; set; }

    [JsonPropertyName("commandExecution")]
    public bool CommandExecution { get; set; }

    [JsonPropertyName("nativeFfi")]
    public bool NativeFfi { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = ScriptMetadata.ClearScriptType;

    [JsonPropertyName("export")]
    public List<string>? Export { get; set; }
}

public sealed record ScriptFileEntry(string Path, ScriptMetadata Metadata, string? LogoPath);

public sealed record ScriptExecutionRequest(
    string ScriptPath,
    string ScriptContent,
    ScriptInvocation Invocation,
    Uri? SourceMapUri = null)
{
    public static ScriptExecutionRequest ForFile(
        string scriptPath,
        string scriptContent,
        ScriptInvocation? invocation = null) =>
        new(Path.GetFullPath(scriptPath), scriptContent, invocation ?? ScriptInvocation.WholeScript);
}

public sealed record ScriptExecutionOptions(
    IReadOnlyList<ScriptNativeType> NativeTypes,
    bool DynamicImport,
    bool CommandExecution,
    bool NativeFfi)
{
    public static ScriptExecutionOptions Default { get; } = new([], false, false, false);

    public static ScriptExecutionOptions FromMetadata(ScriptMetadata metadata) =>
        new(metadata.NativeTypes, metadata.DynamicImport, metadata.CommandExecution, metadata.NativeFfi);
}

public enum ScriptDebugMode
{
    None,
    Immediate,
    Wait,
    Break,
}

public sealed record ScriptDebugOptions(ScriptDebugMode Mode, int Port = 9222)
{
    public static ScriptDebugOptions Disabled { get; } = new(ScriptDebugMode.None);

    public int ValidatedPort => Math.Clamp(Port, 1, 65535);
}

public sealed record ScriptExecutionResult(
    bool IsSuccess,
    string Output,
    long DurationMilliseconds,
    string? Error = null,
    bool WasCancelled = false);

public sealed class ScriptDebuggerEventArgs : EventArgs
{
    public ScriptDebuggerEventArgs(int port) => Port = port;

    public int Port { get; }
}
