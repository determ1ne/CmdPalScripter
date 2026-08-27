using System.Text.Json;

namespace Scripter.Core;

public sealed class ScriptStorageService
{
    public ScriptStorageService(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory) ? null : Path.GetFullPath(rootDirectory);
    }

    public string? RootDirectory { get; }

    public IReadOnlyList<ScriptFileEntry> GetScriptEntries()
    {
        if (RootDirectory is null || !Directory.Exists(RootDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(RootDirectory, "*.js", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => new ScriptFileEntry(path, ScriptMetadataLoader.Load(path), ScriptMetadataLoader.ResolveLogoPath(path)))
            .ToArray();
    }

    public static string LoadScript(string scriptPath) => File.ReadAllText(Path.GetFullPath(scriptPath));

    public static void SaveScript(string scriptPath, string scriptContent)
    {
        var resolvedPath = Path.GetFullPath(scriptPath);
        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(resolvedPath, scriptContent);
    }
}

public static class ScriptMetadataLoader
{
    public static ScriptMetadata Load(string scriptPath)
    {
        var fullPath = Path.GetFullPath(scriptPath);
        var fallbackName = Path.GetFileNameWithoutExtension(fullPath);
        var fallbackDescription = fullPath;
        var metadataPath = GetMetadataPath(fullPath);
        if (!File.Exists(metadataPath))
        {
            return new ScriptMetadata(fallbackName, fallbackDescription);
        }

        try
        {
            var metadata = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), ScripterCoreJsonContext.Default.ScriptMetadataFile);
            if (metadata is null)
            {
                return new ScriptMetadata(fallbackName, fallbackDescription);
            }

            var name = string.IsNullOrWhiteSpace(metadata.Name) ? fallbackName : metadata.Name;
            var description = string.IsNullOrWhiteSpace(metadata.Description) ? fallbackDescription : metadata.Description;
            var type = string.IsNullOrWhiteSpace(metadata.Type) ? ScriptMetadata.ClearScriptType : metadata.Type;
            var exports = metadata.Export?.Select(value => value ?? string.Empty).ToArray() ?? [];
            return new ScriptMetadata(
                name,
                description,
                metadata.NativeTypes ?? [],
                metadata.DynamicImport,
                metadata.CommandExecution,
                metadata.NativeFfi,
                type,
                exports);
        }
        catch (JsonException)
        {
            return new ScriptMetadata(fallbackName, fallbackDescription);
        }
        catch (IOException)
        {
            return new ScriptMetadata(fallbackName, fallbackDescription);
        }
        catch (UnauthorizedAccessException)
        {
            return new ScriptMetadata(fallbackName, fallbackDescription);
        }
    }

    public static string GetMetadataPath(string scriptPath) => Path.GetFullPath(scriptPath) + ".meta.json";

    public static string? ResolveLogoPath(string scriptPath)
    {
        var logoPath = Path.ChangeExtension(Path.GetFullPath(scriptPath), ".png");
        return File.Exists(logoPath) ? logoPath : null;
    }
}
