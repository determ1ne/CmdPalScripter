using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Scripter;

public partial class ScripterCommandsProvider : CommandProvider
{
    private static readonly ScripterSettingsManager SettingsManager = new();

    private ICommandItem[] _commands = [];
    private readonly CommandItem _scriptsCommand;
    private readonly ScripterPage _page;
    private readonly ScriptStorageService _storageService;
    private readonly ScriptPermissionService _permissionService;
    private readonly ScriptExecutionService _executionService;

    public ScripterCommandsProvider()
    {
        DisplayName = "Scripts";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        Settings = SettingsManager.Settings;
        Frozen = false;

        _storageService = new ScriptStorageService();
        _permissionService = new ScriptPermissionService(_storageService.RootDirectory);
        _executionService = new ScriptExecutionService(SettingsManager);
        _page = new ScripterPage(
            _storageService,
            SettingsManager,
            _permissionService,
            _executionService,
            RefreshTopLevelCommands);

        _scriptsCommand = new CommandItem(_page)
        {
            Title = DisplayName,
            Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png"),
            MoreCommands = [new CommandContextItem(SettingsManager.Settings.SettingsPage)],
        };

        RefreshTopLevelCommands();
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    private void RefreshTopLevelCommands()
    {
        var exportedCommands = _page.ScriptEntries
            .SelectMany(entry => entry.Metadata.Export
                .Where(ScriptInvocationParser.IsValidFunctionName)
                .Distinct(StringComparer.Ordinal)
                .Select(functionName => (ICommandItem)new CommandItem(new ExportedFunctionPage(
                        entry,
                        functionName,
                        _storageService,
                        _executionService,
                        _permissionService))
                    {
                        Title = functionName,
                        Subtitle = entry.Metadata.Name,
                        Icon = entry.LogoPath is null
                            ? IconHelpers.FromRelativePath("Assets\\StoreLogo.png")
                            : new IconInfo(entry.LogoPath),
                    }))
            .ToArray();

        _commands = [_scriptsCommand, .. exportedCommands];

        RaiseItemsChanged();
    }

}
