using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Scripter;

public partial class ScripterCommandsProvider : CommandProvider
{
    private static readonly ScripterSettingsManager SettingsManager = new();

    private readonly ICommandItem[] _commands;

    public ScripterCommandsProvider()
    {
        DisplayName = "Scripts";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        Settings = SettingsManager.Settings;

        _commands = [
            new CommandItem(new ScripterPage(SettingsManager))
            {
                Title = DisplayName,
                Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png"),
                MoreCommands = [new CommandContextItem(SettingsManager.Settings.SettingsPage)],
            },
        ];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

}
