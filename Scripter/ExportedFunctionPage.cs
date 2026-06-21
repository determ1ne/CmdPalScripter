using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Scripter;

internal sealed partial class ExportedFunctionPage : DynamicListPage
{
    private readonly ScriptFileEntry _entry;
    private readonly string _functionName;
    private readonly ScriptStorageService _storageService;
    private readonly ScriptExecutionService _executionService;
    private readonly ScriptPermissionService _permissionService;

    public ExportedFunctionPage(
        ScriptFileEntry entry,
        string functionName,
        ScriptStorageService storageService,
        ScriptExecutionService executionService,
        ScriptPermissionService permissionService)
    {
        _entry = entry;
        _functionName = functionName;
        _storageService = storageService;
        _executionService = executionService;
        _permissionService = permissionService;

        Name = functionName;
        Title = functionName;
        PlaceholderText = $"Arguments for {functionName}";
        Icon = entry.LogoPath is null
            ? IconHelpers.FromRelativePath("Assets\\StoreLogo.png")
            : new IconInfo(entry.LogoPath);
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (!string.Equals(oldSearch, newSearch, StringComparison.Ordinal))
        {
            RaiseItemsChanged(1);
        }
    }

    public override IListItem[] GetItems()
    {
        var invocation = new ScriptInvocation(
            _functionName,
            ScriptInvocationParser.ParseArguments(SearchText));
        var command = new RunScriptFileCommand(
            _entry,
            _storageService,
            _executionService,
            _permissionService,
            invocation)
        {
            Name = $"Run {_functionName}",
        };

        var typedArguments = SearchText.Trim();
        return
        [
            new ListItem(command)
            {
                Title = string.IsNullOrEmpty(typedArguments)
                    ? $"Run {_functionName}"
                    : $"{_functionName} {typedArguments}",
                Subtitle = $"{_entry.Metadata.Name} — {_entry.Metadata.Description}",
                TextToSuggest = SearchText,
                Icon = Icon,
            },
        ];
    }
}
