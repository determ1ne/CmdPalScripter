using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Scripter.Core;

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
    private readonly ScriptCatalog _catalog;

    public ScripterCommandsProvider()
    {
        DisplayName = "Scripts";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

        Settings = SettingsManager.Settings;
        Frozen = false;

        _storageService = new ScriptStorageService();
        _permissionService = new ScriptPermissionService(_storageService.RootDirectory);
        _executionService = new ScriptExecutionService();
        _catalog = new ScriptCatalog(_storageService.ScriptsDirectory);
        _page = new ScripterPage(
            _storageService,
            SettingsManager,
            _permissionService,
            _executionService,
            _catalog);

        _scriptsCommand = new CommandItem(_page)
        {
            Title = DisplayName,
            Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png"),
            MoreCommands = [new CommandContextItem(SettingsManager.Settings.SettingsPage)],
        };

        _catalog.Changed += OnCatalogChanged;
        RefreshTopLevelCommands();
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override void Dispose()
    {
        _catalog.Changed -= OnCatalogChanged;
        _catalog.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnCatalogChanged(object? sender, EventArgs args)
    {
        _page.ApplyCatalogSnapshot();
        RefreshTopLevelCommands();
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
                        _permissionService,
                        SettingsManager))
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
