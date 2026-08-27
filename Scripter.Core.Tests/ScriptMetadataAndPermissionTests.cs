using Scripter.Core;

namespace Scripter.Core.Tests;

public sealed class ScriptMetadataAndPermissionTests
{
    [Fact]
    public void LoadsMetadataForAnArbitraryScriptPath()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "tool.js");
        File.WriteAllText(path, "1 + 1");
        File.WriteAllText(
            path + ".meta.json",
            """
            { "name": "Tool", "export": ["run"], "commandExecution": true, "nativeFfi": true }
            """);

        var metadata = ScriptMetadataLoader.Load(path);

        Assert.Equal("Tool", metadata.Name);
        Assert.Equal(["run"], metadata.Export);
        Assert.True(metadata.CommandExecution);
        Assert.True(metadata.NativeFfi);
    }

    [Fact]
    public void PermissionFingerprintChangesWithContentAndCapabilities()
    {
        var safe = new ScriptMetadata("test", "test");
        var command = new ScriptMetadata("test", "test", [], false, true);
        var nativeFfi = new ScriptMetadata("test", "test", [], false, false, true);

        var original = ScriptPermissionService.ComputeFingerprint("1", safe);

        Assert.NotEqual(original, ScriptPermissionService.ComputeFingerprint("2", safe));
        Assert.NotEqual(original, ScriptPermissionService.ComputeFingerprint("1", command));
        Assert.NotEqual(original, ScriptPermissionService.ComputeFingerprint("1", nativeFfi));
        Assert.True(ScriptPermissionService.RequiresPermission(nativeFfi));
        Assert.Contains("Native FFI", ScriptPermissionDescriptionFormatter.BuildDescription("test", nativeFfi), StringComparison.Ordinal);
    }
}
