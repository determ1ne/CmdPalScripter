using System;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Scripter;

internal sealed class ScripterSettingsManager : JsonSettingsManager
{
    private const string NamespacePrefix = "scripter";

    private static string Namespaced(string propertyName) => $"{NamespacePrefix}.{propertyName}";

    private readonly ToggleSetting _enableRemoteDebugging = new(
        Namespaced(nameof(EnableRemoteDebugging)),
        "Enable remote debugging",
        "Expose V8 debugger endpoint on the configured debug port.",
        false);

    private readonly ToggleSetting _pauseOnStart = new(
        Namespaced(nameof(PauseOnStart)),
        "Pause on script start",
        "Wait for debugger and break before script execution starts.",
        false);

    private readonly TextSetting _debugPort = new(
        Namespaced(nameof(DebugPort)),
        "Debug port",
        "TCP port for V8 debugger when remote debugging is enabled.",
        "9222")
    {
        Placeholder = "9222",
    };

    public ScripterSettingsManager()
    {
        FilePath = SettingsJsonPath();

        Settings.Add(_enableRemoteDebugging);
        Settings.Add(_pauseOnStart);
        Settings.Add(_debugPort);

        LoadSettings();
        Settings.SettingsChanged += (s, a) => SaveSettings();
    }

    public bool EnableRemoteDebugging() => _enableRemoteDebugging.Value;

    public bool PauseOnStart() => _pauseOnStart.Value;

    public int DebugPort()
    {
        var raw = _debugPort.Value ?? string.Empty;
        if (!int.TryParse(raw, out var port))
        {
            return 9222;
        }

        return Math.Clamp(port, 1, 65535);
    }

    private static string SettingsJsonPath()
    {
        var directory = Utilities.BaseSettingsPath("Microsoft.CmdPal");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "settings.json");
    }
}
