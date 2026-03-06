using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scripter;

internal sealed class ScriptStorageService
{
    public const string ClearScriptType = "clearscript";

    private const string ScriptsFolderName = "Scripts";
    private const string ExampleScriptsFolderName = "ExampleScripts";
    private const string FirstRunMarkerName = ".first_run_complete";

    public static readonly string DefaultScratchpadScript =
        "// Scratchpad: edit and execute from Command Palette\n" +
        "const now = new Date().toISOString();\n" +
        "`Current UTC time: ${now}`;";

    public string RootDirectory { get; }

    public string ScriptsDirectory { get; }

    public string ScratchpadFilePath { get; }

    public ScriptStorageService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        RootDirectory = Path.Combine(localAppData, "Microsoft", "PowerToys", "CommandPalette", "Scripter");
        ScriptsDirectory = Path.Combine(RootDirectory, ScriptsFolderName);
        ScratchpadFilePath = Path.Combine(ScriptsDirectory, "scratchpad.js");

        EnsureInitialized();
    }

    public IReadOnlyList<ScriptFileEntry> GetScriptEntries()
    {
        if (!Directory.Exists(ScriptsDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(ScriptsDirectory, "*.js", SearchOption.TopDirectoryOnly)
            .Where(path => !path.Equals(ScratchpadFilePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(path => new ScriptFileEntry(path, LoadMetadata(path), ResolveScriptLogoPath(path)))
            .ToArray();
    }

    public string LoadScript(string scriptPath)
    {
        var resolvedPath = ResolveScriptPath(scriptPath);

        try
        {
            return File.Exists(resolvedPath)
                ? File.ReadAllText(resolvedPath)
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SaveScript(string scriptPath, string scriptContent)
    {
        var resolvedPath = ResolveScriptPath(scriptPath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(resolvedPath, scriptContent);
    }

    private void EnsureInitialized()
    {
        Directory.CreateDirectory(ScriptsDirectory);

        if (!File.Exists(ScratchpadFilePath))
        {
            File.WriteAllText(ScratchpadFilePath, DefaultScratchpadScript);
        }

        CopyExampleScriptsFromPackage();

        var firstRunMarker = Path.Combine(RootDirectory, FirstRunMarkerName);
        if (!File.Exists(firstRunMarker))
        {
            File.WriteAllText(firstRunMarker, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
    }

    private void CopyExampleScriptsFromPackage()
    {
        var sourceDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExampleScriptsFolderName);
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var destinationPath = Path.Combine(ScriptsDirectory, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (!File.Exists(destinationPath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
            }
        }
    }

    private static ScriptMetadata LoadMetadata(string scriptPath)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(scriptPath);
        var fallbackDescription = scriptPath;
        var metadataPath = GetMetadataPath(scriptPath);
        if (!File.Exists(metadataPath))
        {
            return new ScriptMetadata(fallbackName, fallbackDescription, [], false, false);
        }

        try
        {
            var metadata = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), ScripterJsonContext.Default.ScriptMetadataFile);
            if (metadata is null)
            {
                return new ScriptMetadata(fallbackName, fallbackDescription, [], false, false);
            }

            var name = string.IsNullOrWhiteSpace(metadata.Name) ? fallbackName : metadata.Name;
            var description = string.IsNullOrWhiteSpace(metadata.Description) ? fallbackDescription : metadata.Description;
            var nativeTypes = metadata.NativeTypes ?? [];

            var scriptType = string.IsNullOrWhiteSpace(metadata.Type) ? ClearScriptType : metadata.Type;
            return new ScriptMetadata(name, description, nativeTypes, metadata.DynamicImport, metadata.CommandExecution, scriptType);
        }
        catch
        {
            return new ScriptMetadata(fallbackName, fallbackDescription, [], false, false);
        }
    }

    private static string GetMetadataPath(string scriptPath) => scriptPath + ".meta.json";

    private static string? ResolveScriptLogoPath(string scriptPath)
    {
        var logoPath = Path.ChangeExtension(scriptPath, ".png");
        return File.Exists(logoPath) ? logoPath : null;
    }

    private string ResolveScriptPath(string scriptPath)
    {
        var candidate = Path.GetFullPath(scriptPath);
        var scriptsRoot = Path.GetFullPath(ScriptsDirectory);
        if (!scriptsRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            scriptsRoot += Path.DirectorySeparatorChar;
        }

        if (!candidate.StartsWith(scriptsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return ScratchpadFilePath;
        }

        return candidate;
    }

}

internal sealed record ScriptMetadata(
    string Name,
    string Description,
    IReadOnlyList<ScriptNativeType> NativeTypes,
    bool DynamicImport,
    bool CommandExecution,
    string Type)
{
    public ScriptMetadata(string name, string description)
        : this(name, description, [], false, false, ScriptStorageService.ClearScriptType)
    {
    }

    public ScriptMetadata(string name, string description, IReadOnlyList<ScriptNativeType> nativeTypes, bool dynamicImport, bool commandExecution)
        : this(name, description, nativeTypes, dynamicImport, commandExecution, ScriptStorageService.ClearScriptType)
    {
    }
}

internal sealed record ScriptNativeType(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("typeName")] string TypeName);

internal sealed class ScriptMetadataFile
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

    [JsonPropertyName("type")]
    public string Type { get; set; } = ScriptStorageService.ClearScriptType;
}

internal sealed record ScriptFileEntry(string Path, ScriptMetadata Metadata, string? LogoPath);
