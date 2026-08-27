using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Scripter.Core;

namespace Scripter;

internal sealed class ScriptStorageService
{
    private const string ScriptsFolderName = "Scripts";
    private const string ExampleScriptsFolderName = "ExampleScripts";
    private const string FirstRunMarkerName = ".first_run_complete";

    public static readonly string DefaultScratchpadScript =
        "// Scratchpad: edit and execute from Command Palette\n"
        + "const now = new Date().toISOString();\n"
        + "`Current UTC time: ${now}`;";

    public ScriptStorageService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        RootDirectory = Path.Combine(localAppData, "Microsoft", "PowerToys", "CommandPalette", "Scripter");
        ScriptsDirectory = Path.Combine(RootDirectory, ScriptsFolderName);
        ScratchpadFilePath = Path.Combine(ScriptsDirectory, "scratchpad.js");
        EnsureInitialized();
    }

    public string RootDirectory { get; }

    public string ScriptsDirectory { get; }

    public string ScratchpadFilePath { get; }

    public IReadOnlyList<ScriptFileEntry> GetScriptEntries() =>
        new global::Scripter.Core.ScriptStorageService(ScriptsDirectory).GetScriptEntries()
            .Where(entry => !entry.Path.Equals(ScratchpadFilePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Storage access remains an adapter instance API.")]
    public string LoadScript(string scriptPath)
    {
        try
        {
            return File.Exists(scriptPath) ? File.ReadAllText(Path.GetFullPath(scriptPath)) : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Storage access remains an adapter instance API.")]
    public void SaveScript(string scriptPath, string scriptContent)
    {
        var resolvedPath = Path.GetFullPath(scriptPath);
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
}
