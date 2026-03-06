using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace Scripter;

internal sealed class ScriptExecutionService
{
    private readonly ScripterSettingsManager _settings;

    private readonly object _runLock = new();
    private V8ScriptEngine? _activeEngine;
    private CancellationTokenSource? _activeRunCancellation;

    public ScriptExecutionService(ScripterSettingsManager settings)
    {
        _settings = settings;
    }

    public ScriptExecutionResult Execute(
        string script,
        IReadOnlyList<ScriptNativeType>? exposedTypes = null,
        bool enableDynamicImport = false,
        bool allowCommandExecution = false)
    {
        return ExecuteCore(script, exposedTypes, enableDynamicImport, allowCommandExecution);
    }

    public Task<ScriptExecutionResult> ExecuteAsync(
        string script,
        IReadOnlyList<ScriptNativeType>? exposedTypes = null,
        bool enableDynamicImport = false,
        bool allowCommandExecution = false)
    {
        if (IsRunning)
        {
            return Task.FromResult(new ScriptExecutionResult(false, "Another script is already running.", 0));
        }

        return Task.Run(() => ExecuteCore(script, exposedTypes, enableDynamicImport, allowCommandExecution));
    }

    public bool IsRunning
    {
        get
        {
            lock (_runLock)
            {
                return _activeEngine is not null;
            }
        }
    }

    public bool TryRequestStop()
    {
        lock (_runLock)
        {
            if (_activeEngine is null)
            {
                return false;
            }

            _activeRunCancellation?.Cancel();
            _activeEngine.Interrupt();
            return true;
        }
    }

    private ScriptExecutionResult ExecuteCore(
        string script,
        IReadOnlyList<ScriptNativeType>? exposedTypes,
        bool enableDynamicImport,
        bool allowCommandExecution)
    {
        var stopwatch = Stopwatch.StartNew();
        using var runCancellation = new CancellationTokenSource();

        try
        {
            var flags = V8ScriptEngineFlags.EnableTaskPromiseConversion;
            if (_settings.EnableRemoteDebugging())
            {
                flags |= V8ScriptEngineFlags.EnableDebugging | V8ScriptEngineFlags.EnableRemoteDebugging;
                if (_settings.PauseOnStart())
                {
                    flags |= V8ScriptEngineFlags.AwaitDebuggerAndPauseOnStart;
                }
            }

            var debugPort = _settings.DebugPort();
            using var engine = _settings.EnableRemoteDebugging()
                ? new V8ScriptEngine(flags, debugPort)
                : new V8ScriptEngine(flags);
            lock (_runLock)
            {
                _activeEngine = engine;
                _activeRunCancellation = runCancellation;
            }

            engine.AddHostObject("builtins", new ScriptBuiltins(() => runCancellation.Token, allowCommandExecution));
            engine.Execute(
                "globalThis.messageBox = function(text, caption) { return caption === undefined ? builtins.MessageBox(text) : builtins.MessageBoxWithCaption(text, caption); };");
            engine.Execute(
                "globalThis.$ = function(command, options) { "
                + "var shell = options && typeof options === 'object' ? options.shell : undefined; "
                + "return shell === undefined ? builtins.RunCommand(command) : builtins.RunCommandWithShell(command, shell); };");
            engine.Execute(
                "globalThis.$.use = function(shell) { "
                + "return function(command, options) { "
                + "var merged = options && typeof options === 'object' ? Object.assign({}, options) : {}; "
                + "merged.shell = shell; "
                + "return globalThis.$(command, merged); }; };"
            );

            if (enableDynamicImport)
            {
                engine.AddHostObject("host", new ScriptHostApi(engine));
                engine.Execute("globalThis.importType = function(typeName) { return host.ImportType(typeName); };");
            }

            if (exposedTypes is not null)
            {
                foreach (var exposeType in exposedTypes)
                {
                    engine.AddHostType(exposeType.Name, ScriptHostApi.FindType(exposeType.TypeName));
                }
            }

            var result = engine.Evaluate(script);
            if (result is Task t)
            {
                result = ScriptBuiltins.ResolveAwaitable(t, runCancellation.Token);
            }

            stopwatch.Stop();

            var output = result switch
            {
                null => "null",
                Undefined => "undefined",
                _ => result.ToString() ?? string.Empty,
            };

            return new ScriptExecutionResult(
                IsSuccess: true,
                Output: output,
                DurationMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();

            return new ScriptExecutionResult(
                IsSuccess: false,
                Output: "Execution stopped.",
                DurationMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        catch (ScriptEngineException ex)
        {
            stopwatch.Stop();

            var message = string.IsNullOrWhiteSpace(ex.ErrorDetails)
                ? ex.Message
                : ex.ErrorDetails;

            return new ScriptExecutionResult(
                IsSuccess: false,
                Output: message,
                DurationMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new ScriptExecutionResult(
                IsSuccess: false,
                Output: ex.ToString(),
                DurationMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        finally
        {
            lock (_runLock)
            {
                _activeEngine = null;
                _activeRunCancellation = null;
            }
        }
    }
}

public sealed class ScriptBuiltins
{
    private readonly Func<CancellationToken> _cancellationProvider;
    private readonly bool _allowCommandExecution;

    public ScriptBuiltins(Func<CancellationToken> cancellationProvider, bool allowCommandExecution)
    {
        _cancellationProvider = cancellationProvider;
        _allowCommandExecution = allowCommandExecution;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Exposed as host object instance methods for ClearScript.")]
    public int MessageBox(string text)
    {
        return ScriptLibrary.MessageBox.Show(text);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Exposed as host object instance methods for ClearScript.")]
    public int MessageBoxWithCaption(string text, string caption)
    {
        return ScriptLibrary.MessageBox.Show(text, caption);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Exposed as host object instance methods for ClearScript.")]
    public string RunCommand(string command)
    {
        return RunCommandWithShell(command, "cmd");
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Exposed as host object instance methods for ClearScript.")]
    public string RunCommandWithShell(string command, string shell)
    {
        if (!_allowCommandExecution)
        {
            throw new InvalidOperationException("Command execution is disabled for this script. Enable commandExecution in script metadata.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command cannot be empty.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = "cmd";
        }

        var cancellationToken = _cancellationProvider();
        var startInfo = CreateStartInfo(command, shell);
        using var process = new Process
        {
            StartInfo = startInfo,
        };

        process.Start();

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            while (!process.WaitForExit(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Command failed with exit code {process.ExitCode}: {error}");
            }

            return string.IsNullOrWhiteSpace(output) ? "(no output)" : output.TrimEnd();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string command, string shell)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (string.Equals(shell, "cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
            return startInfo;
        }

        if (string.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "powershell.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
            return startInfo;
        }

        if (string.Equals(shell, "pwsh", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "pwsh.exe";
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
            return startInfo;
        }

        throw new InvalidOperationException("Unsupported shell. Use 'cmd', 'powershell', or 'pwsh'.");
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Accessing Task<TResult>.Result via reflection for generic tasks.")]
    public static object? ResolveAwaitable(object value, CancellationToken cancellationToken)
    {
        if (value is not Task task)
        {
            return value;
        }

        while (!task.Wait(100, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        task.GetAwaiter().GetResult();
        var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
        return resultProperty is null ? null : resultProperty.GetValue(task);
    }
}

public sealed class ScriptHostApi
{
    private readonly V8ScriptEngine _engine;
    private int _importCounter;

    public ScriptHostApi(V8ScriptEngine engine)
    {
        _engine = engine;
    }

    public object ImportType(string typeName)
    {
        var alias = "__importedType" + Interlocked.Increment(ref _importCounter);
        _engine.AddHostType(alias, FindType(typeName));
        return _engine.Evaluate(alias);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "Type names are provided at runtime for script interop.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Dynamic assembly type lookup is required for importType.")]
    public static Type FindType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new ArgumentException("Type name is required.", nameof(typeName));
        }

        var type = Type.GetType(typeName);
        if (type is not null)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName);
            if (type is not null)
            {
                return type;
            }
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var types = assembly.GetTypes();
                type = types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));
                if (type is not null)
                {
                    return type;
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                type = ex.Types.FirstOrDefault(t => t is not null && string.Equals(t.Name, typeName, StringComparison.Ordinal));
                if (type is not null)
                {
                    return type;
                }
            }
        }

        throw new DllNotFoundException("Type not found: " + typeName);
    }
}

internal sealed record ScriptExecutionResult(bool IsSuccess, string Output, long DurationMilliseconds);
