using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Scripter;

internal sealed partial class ScripterPage : DynamicListPage
{
    private static readonly IconInfo DefaultItemIcon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");

    private readonly ScripterSettingsManager _settingsManager;
    private readonly ScriptStorageService _storageService;
    private readonly ScriptPermissionService _permissionService;
    private readonly ScriptExecutionService _executionService;
    private readonly List<ScriptFileEntry> _scriptEntries = [];

    public ScripterPage()
        : this(new ScriptStorageService(), new ScripterSettingsManager())
    {
    }

    internal ScripterPage(ScripterSettingsManager settingsManager)
        : this(new ScriptStorageService(), settingsManager)
    {
    }

    internal ScripterPage(ScriptStorageService storageService, ScripterSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        _storageService = storageService;
        _permissionService = new ScriptPermissionService(_storageService.RootDirectory);
        _executionService = new ScriptExecutionService(_settingsManager);

        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Script Library";
        Name = "Open Scripts";
        PlaceholderText = "Search scripts";
        EmptyContent = new CommandItem(new NoOpCommand())
        {
            Title = "No script files found",
            Subtitle = _storageService.ScriptsDirectory,
        };

        ReloadScripts();
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (oldSearch == newSearch)
        {
            return;
        }

        RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var query = SearchText.Trim();
        var matchingEntries = string.IsNullOrWhiteSpace(query)
            ? _scriptEntries
            : _scriptEntries.Where(entry => MatchesQuery(entry, query)).ToList();

        var items = new List<IListItem>
        {
            CreateScratchpadItem(),
            CreateStopRunningItem(),
            CreateOpenScriptsFolderItem(),
            CreateReloadItem(),
        };

        foreach (var entry in matchingEntries.OrderBy(e => e.Metadata.Name, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(CreateScriptFileItem(entry));
        }

        return items.ToArray();
    }

    private static bool MatchesQuery(ScriptFileEntry entry, string query)
    {
        return entry.Metadata.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Metadata.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(entry.Path).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ReloadScripts()
    {
        _scriptEntries.Clear();
        _scriptEntries.AddRange(_storageService.GetScriptEntries());
    }

    private ListItem CreateReloadItem()
    {
        var reloadCommand = new AnonymousCommand(() =>
        {
            ReloadScripts();
            RaiseItemsChanged();
        })
        {
            Name = "Reload scripts",
            Result = CommandResult.KeepOpen(),
        };

        return new ListItem(reloadCommand)
        {
            Title = "Reload scripts",
            Subtitle = "Rescan files and metadata",
            Icon = new IconInfo("\uE72C"),
        };
    }

    private ListItem CreateScratchpadItem()
    {
        return new ListItem(new CommandItem(new ScriptScratchpadPage(_storageService, _executionService, _permissionService)))
        {
            Title = "Open scratchpad",
            Subtitle = _storageService.ScratchpadFilePath,
            TextToSuggest = "scratchpad",
            Icon = new IconInfo("\uE70F"),
        };
    }

    private ListItem CreateOpenScriptsFolderItem()
    {
        var command = new OpenScriptsFolderCommand(_storageService.ScriptsDirectory)
        {
            Name = "Open scripts folder",
        };

        return new ListItem(command)
        {
            Title = "Open scripts folder",
            Subtitle = _storageService.ScriptsDirectory,
            TextToSuggest = "open scripts folder",
            Icon = new IconInfo("\uE8B7"),
        };
    }

    private ListItem CreateStopRunningItem()
    {
        var command = new StopRunningScriptCommand(_executionService)
        {
            Name = "Stop running script",
        };

        return new ListItem(command)
        {
            Title = "Stop running script",
            Subtitle = "Force-stop current script execution",
            TextToSuggest = "stop script",
            Icon = new IconInfo("\uE71A"),
        };
    }

    private ListItem CreateScriptFileItem(ScriptFileEntry entry)
    {
        var nativeTypeList = entry.Metadata.NativeTypes.Count == 0
            ? "(none)"
            : string.Join(", ", entry.Metadata.NativeTypes.Select(t => t.Name));
        var dynamicImport = entry.Metadata.DynamicImport ? "enabled" : "disabled";

        var command = new RunScriptFileCommand(entry, _storageService, _executionService, _permissionService)
        {
            Name = "Run script",
        };

        return new ListItem(command)
        {
            Title = entry.Metadata.Name,
            Subtitle = entry.Metadata.Description,
            TextToSuggest = Path.GetFileName(entry.Path),
            Icon = entry.LogoPath is null ? DefaultItemIcon : new IconInfo(entry.LogoPath),
            Details = new Details()
            {
                Title = entry.Metadata.Name,
                Body = $"{entry.Metadata.Description}\nType: {entry.Metadata.Type}\nNative types: {nativeTypeList}\nDynamic import: {dynamicImport}\nCommand execution: {(entry.Metadata.CommandExecution ? "enabled" : "disabled")}\nLogo: `{entry.LogoPath ?? "default"}`\nPath: `{entry.Path}`",
            },
        };
    }
}

internal sealed partial class ScriptScratchpadPage : ContentPage
{
    private static readonly ScriptMetadata ScratchpadSecurityMetadata = new("Scratchpad", "Scratchpad", [], true, true);

    private readonly ScriptExecutionService _executionService;
    private readonly ScriptPermissionService _permissionService;
    private readonly ScriptStorageService _storageService;

    private readonly MarkdownContent _resultContent;
    private readonly ScriptRunnerForm _scriptForm;

    public ScriptScratchpadPage(
        ScriptStorageService storageService,
        ScriptExecutionService executionService,
        ScriptPermissionService permissionService)
    {
        _storageService = storageService;
        _executionService = executionService;
        _permissionService = permissionService;

        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        Title = "Scratchpad";
        Name = "Open Scratchpad";

        _resultContent = new MarkdownContent()
        {
            Body = "Run JavaScript using the editor below. Output appears here.",
        };

        _scriptForm = new ScriptRunnerForm(_storageService.LoadScript(_storageService.ScratchpadFilePath), ExecuteAndRenderResult);

        Details = new Details()
        {
            Title = "Scratchpad file",
            Body = _storageService.ScratchpadFilePath,
        };
    }

    public override IContent[] GetContent()
    {
        return [_scriptForm, _resultContent];
    }

    private CommandResult ExecuteAndRenderResult(string script)
    {
        _storageService.SaveScript(_storageService.ScratchpadFilePath, script);

        if (_executionService.IsRunning)
        {
            return CommandResult.ShowToast("Another script is already running. Stop it first.");
        }

        if (!_permissionService.IsApproved(_storageService.ScratchpadFilePath, script, ScratchpadSecurityMetadata))
        {
            var confirmArgs = new ConfirmationArgs
            {
                Title = "⚠ Allow scratchpad permissions",
                Description = ScriptPermissionDescriptionFormatter.BuildDescription("Scratchpad", ScratchpadSecurityMetadata),
                PrimaryCommand = new ApproveAndRunScratchpadCommand(
                    this,
                    _storageService,
                    _executionService,
                    _permissionService,
                    script)
                {
                    Name = "Allow and run",
                },
                IsPrimaryCommandCritical = true,
            };

            return CommandResult.Confirm(confirmArgs);
        }

        return StartScratchpadRun(script);
    }

    internal CommandResult StartScratchpadRun(string script)
    {
        var busy = ScriptRunStatus.Show("Scratchpad");
        _resultContent.Body = "Running scratchpad...";
        RaiseItemsChanged();

        _ = _executionService.ExecuteAsync(script, enableDynamicImport: true, allowCommandExecution: true).ContinueWith(task =>
        {
            var result = task.Result;

            var status = result.IsSuccess ? "Success" : "Error";
            var output = string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : result.Output;
            _resultContent.Body =
$"""
### {status}

Duration: `{result.DurationMilliseconds} ms`

```text
{output}
```
""";

            RaiseItemsChanged();
            ScriptRunStatus.Complete(
                busy,
                result.IsSuccess,
                "Scratchpad",
                result.DurationMilliseconds);
        });

        return CommandResult.KeepOpen();
    }
}

internal sealed partial class RunScriptFileCommand : InvokableCommand
{
    private readonly ScriptFileEntry _entry;
    private readonly ScriptStorageService _storageService;
    private readonly ScriptExecutionService _executionService;
    private readonly ScriptPermissionService _permissionService;

    public RunScriptFileCommand(
        ScriptFileEntry entry,
        ScriptStorageService storageService,
        ScriptExecutionService executionService,
        ScriptPermissionService permissionService)
    {
        _entry = entry;
        _storageService = storageService;
        _executionService = executionService;
        _permissionService = permissionService;
    }

    public override CommandResult Invoke()
    {
        var script = _storageService.LoadScript(_entry.Path);

        if (!string.Equals(_entry.Metadata.Type, ScriptStorageService.ClearScriptType, StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.ShowToast($"Unsupported script type '{_entry.Metadata.Type}'.");
        }

        if (ScriptPermissionService.RequiresPermission(_entry.Metadata)
            && !_permissionService.IsApproved(_entry.Path, script, _entry.Metadata))
        {
            var confirmArgs = new ConfirmationArgs
            {
                Title = "⚠ Allow script permissions",
                Description = ScriptPermissionDescriptionFormatter.BuildDescription(_entry.Metadata.Name, _entry.Metadata),
                PrimaryCommand = new ApproveAndRunScriptCommand(_entry, _storageService, _executionService, _permissionService, script)
                {
                    Name = "Allow and run",
                },
                IsPrimaryCommandCritical = true,
            };

            return CommandResult.Confirm(confirmArgs);
        }

        if (_executionService.IsRunning)
        {
            return CommandResult.ShowToast("Another script is already running. Stop it first.");
        }

        return StartFileRun(script);
    }

    private CommandResult StartFileRun(string script)
    {
        var busy = ScriptRunStatus.Show(_entry.Metadata.Name);
        _ = _executionService.ExecuteAsync(script, _entry.Metadata.NativeTypes, _entry.Metadata.DynamicImport, _entry.Metadata.CommandExecution).ContinueWith(task =>
        {
            var result = task.Result;
            var prefix = result.IsSuccess ? "Ran" : "Failed";
            var output = string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : result.Output;
            if (output.Length > 220)
            {
                output = output[..220] + "...";
            }

            new ToastStatusMessage(new StatusMessage()
            {
                Message = $"{prefix}: {_entry.Metadata.Name} ({result.DurationMilliseconds} ms)\n{output}",
                State = result.IsSuccess ? MessageState.Success : MessageState.Error,
            }).Show();

            ScriptRunStatus.Complete(
                busy,
                result.IsSuccess,
                _entry.Metadata.Name,
                result.DurationMilliseconds);
        });

        return CommandResult.KeepOpen();
    }
}

internal sealed partial class ApproveAndRunScriptCommand : InvokableCommand
{
    private readonly ScriptFileEntry _entry;
    private readonly ScriptStorageService _storageService;
    private readonly ScriptExecutionService _executionService;
    private readonly ScriptPermissionService _permissionService;
    private readonly string _expectedFingerprint;

    public ApproveAndRunScriptCommand(
        ScriptFileEntry entry,
        ScriptStorageService storageService,
        ScriptExecutionService executionService,
        ScriptPermissionService permissionService,
        string scriptSnapshot)
    {
        _entry = entry;
        _storageService = storageService;
        _executionService = executionService;
        _permissionService = permissionService;
        _expectedFingerprint = ScriptPermissionService.ComputeFingerprint(scriptSnapshot, _entry.Metadata);
    }

    public override CommandResult Invoke()
    {
        var script = _storageService.LoadScript(_entry.Path);
        var currentFingerprint = ScriptPermissionService.ComputeFingerprint(script, _entry.Metadata);
        if (!string.Equals(_expectedFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = "Script changed before approval. Please run again to review and approve latest content.",
                Result = CommandResult.KeepOpen(),
            });
        }

        _permissionService.Approve(_entry.Path, script, _entry.Metadata);
        if (_executionService.IsRunning)
        {
            return CommandResult.ShowToast("Another script is already running. Stop it first.");
        }

        var busy = ScriptRunStatus.Show(_entry.Metadata.Name);
        _ = _executionService.ExecuteAsync(script, _entry.Metadata.NativeTypes, _entry.Metadata.DynamicImport, _entry.Metadata.CommandExecution).ContinueWith(task =>
        {
            var result = task.Result;
            var prefix = result.IsSuccess ? "Ran" : "Failed";
            var output = string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : result.Output;
            if (output.Length > 220)
            {
                output = output[..220] + "...";
            }

            new ToastStatusMessage(new StatusMessage()
            {
                Message = $"{prefix}: {_entry.Metadata.Name} ({result.DurationMilliseconds} ms)\n{output}",
                State = result.IsSuccess ? MessageState.Success : MessageState.Error,
            }).Show();

            ScriptRunStatus.Complete(
                busy,
                result.IsSuccess,
                _entry.Metadata.Name,
                result.DurationMilliseconds);
        });

        return CommandResult.KeepOpen();
    }
}

internal static class ScriptRunStatus
{
    public static ScriptRunHandle Show(string scriptName)
    {
        var message = new StatusMessage
        {
            State = MessageState.Info,
            Message = $"Running: {scriptName}",
            Progress = new ProgressState() { IsIndeterminate = true },
        };

        var handle = new ScriptRunHandle(message);
        handle.ShowTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(175, handle.CancellationTokenSource.Token);
                if (handle.CancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                ExtensionHost.ShowStatus(handle.Message, StatusContext.Page);
                handle.IsShown = true;
            }
            catch (TaskCanceledException)
            {
            }
        });

        return handle;
    }

    public static void Complete(ScriptRunHandle handle, bool success, string scriptName, long durationMs)
    {
        handle.CancellationTokenSource.Cancel();
        try
        {
            handle.ShowTask?.Wait(250);
        }
        catch
        {
        }

        if (handle.IsShown)
        {
            handle.Message.Progress = null;
            handle.Message.State = success ? MessageState.Success : MessageState.Error;
            handle.Message.Message = $"{(success ? "Finished" : "Failed")}: {scriptName} ({durationMs} ms)";

            Task.Run(() =>
            {
                Thread.Sleep(1500);
                ExtensionHost.HideStatus(handle.Message);
            });
        }

        handle.CancellationTokenSource.Dispose();
    }
}

internal sealed class ScriptRunHandle
{
    public ScriptRunHandle(StatusMessage message)
    {
        Message = message;
    }

    public StatusMessage Message { get; }

    public CancellationTokenSource CancellationTokenSource { get; } = new();

    public Task? ShowTask { get; set; }

    public bool IsShown { get; set; }
}

internal sealed partial class ApproveAndRunScratchpadCommand : InvokableCommand
{
    private readonly ScriptScratchpadPage _page;
    private readonly ScriptStorageService _storageService;
    private readonly ScriptExecutionService _executionService;
    private readonly ScriptPermissionService _permissionService;
    private readonly string _expectedFingerprint;

    public ApproveAndRunScratchpadCommand(
        ScriptScratchpadPage page,
        ScriptStorageService storageService,
        ScriptExecutionService executionService,
        ScriptPermissionService permissionService,
        string scriptSnapshot)
    {
        _page = page;
        _storageService = storageService;
        _executionService = executionService;
        _permissionService = permissionService;
        _expectedFingerprint = ScriptPermissionService.ComputeFingerprint(scriptSnapshot, new ScriptMetadata("Scratchpad", "Scratchpad", [], true, true));
    }

    public override CommandResult Invoke()
    {
        var script = _storageService.LoadScript(_storageService.ScratchpadFilePath);
        var metadata = new ScriptMetadata("Scratchpad", "Scratchpad", [], true, true);
        var currentFingerprint = ScriptPermissionService.ComputeFingerprint(script, metadata);
        if (!string.Equals(_expectedFingerprint, currentFingerprint, StringComparison.Ordinal))
        {
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = "Scratchpad changed before approval. Please run again to review and approve latest content.",
                Result = CommandResult.KeepOpen(),
            });
        }

        _permissionService.Approve(_storageService.ScratchpadFilePath, script, metadata);
        _storageService.SaveScript(_storageService.ScratchpadFilePath, script);
        return _page.StartScratchpadRun(script);
    }
}

internal sealed partial class StopRunningScriptCommand : InvokableCommand
{
    private readonly ScriptExecutionService _executionService;

    public StopRunningScriptCommand(ScriptExecutionService executionService)
    {
        _executionService = executionService;
    }

    public override CommandResult Invoke()
    {
        return _executionService.TryRequestStop()
            ? CommandResult.ShowToast("Stop signal sent to running script.")
            : CommandResult.ShowToast("No running script.");
    }
}

internal sealed partial class OpenScriptsFolderCommand : InvokableCommand
{
    private readonly string _scriptsDirectory;

    public OpenScriptsFolderCommand(string scriptsDirectory)
    {
        _scriptsDirectory = scriptsDirectory;
    }

    public override CommandResult Invoke()
    {
        try
        {
            Directory.CreateDirectory(_scriptsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _scriptsDirectory,
                UseShellExecute = true,
            });

            return CommandResult.KeepOpen();
        }
        catch (Exception ex)
        {
            return CommandResult.ShowToast(new ToastArgs
            {
                Message = $"Failed to open scripts folder: {ex.Message}",
                Result = CommandResult.KeepOpen(),
            });
        }
    }
}

internal sealed partial class ScriptRunnerForm : FormContent
{
    private readonly Func<string, CommandResult> _onExecutionRequested;

    private string _script;

    public ScriptRunnerForm(
        string initialScript,
        Func<string, CommandResult> onExecutionRequested)
    {
        _script = initialScript;
        _onExecutionRequested = onExecutionRequested;

        TemplateJson = BuildTemplateJson(_script);
    }

    public override CommandResult SubmitForm(string payload)
    {
        var submitted = JsonNode.Parse(payload)?.AsObject();
        if (submitted is null)
        {
            return CommandResult.KeepOpen();
        }

        _script = submitted["script"]?.ToString() ?? string.Empty;
        TemplateJson = BuildTemplateJson(_script);

        return _onExecutionRequested(_script);
    }

    private static string BuildTemplateJson(string script)
    {
        var encodedScript = JsonSerializer.Serialize(script, ScripterJsonContext.Default.String);
        return $$"""
{
  "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
  "type": "AdaptiveCard",
  "version": "1.6",
  "body": [
    {
      "type": "TextBlock",
      "text": "JavaScript Scratchpad",
      "weight": "Bolder",
      "size": "Medium"
    },
    {
      "type": "Input.Text",
      "id": "script",
      "label": "Script",
      "isMultiline": true,
      "value": {{encodedScript}},
      "placeholder": "Type JavaScript here"
    }
  ],
  "actions": [
    {
      "type": "Action.Submit",
      "title": "Execute"
    }
  ]
}
""";
    }
}
