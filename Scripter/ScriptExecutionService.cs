using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        bool allowCommandExecution = false,
        ScriptInvocation? invocation = null)
    {
        return ExecuteCore(script, exposedTypes, enableDynamicImport, allowCommandExecution, invocation ?? ScriptInvocation.WholeScript);
    }

    public Task<ScriptExecutionResult> ExecuteAsync(
        string script,
        IReadOnlyList<ScriptNativeType>? exposedTypes = null,
        bool enableDynamicImport = false,
        bool allowCommandExecution = false,
        ScriptInvocation? invocation = null)
    {
        if (IsRunning)
        {
            return Task.FromResult(new ScriptExecutionResult(false, "Another script is already running.", 0));
        }

        return Task.Run(() => ExecuteCore(script, exposedTypes, enableDynamicImport, allowCommandExecution, invocation ?? ScriptInvocation.WholeScript));
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
        bool allowCommandExecution,
        ScriptInvocation invocation)
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
                + "var encoding = options && typeof options === 'object' ? options.encoding : undefined; "
                + "return builtins.RunCommandWithShellAndEncoding(String(command), shell == null ? 'cmd' : String(shell), encoding == null ? '' : String(encoding)); };");
            engine.Execute(
                "globalThis.$.exec = function(fileName, args, options) { "
                + "if (args === undefined || args === null) args = []; "
                + "if (!Array.isArray(args)) throw new TypeError('$.exec args must be an array.'); "
                + "var normalizedArgs = args.map(function(arg) { return arg === undefined || arg === null ? '' : String(arg); }); "
                + "return builtins.RunProcess(String(fileName), JSON.stringify(normalizedArgs), JSON.stringify(options || {})); };");
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

            object? result;
            if (invocation.FunctionName is null)
            {
                result = engine.Evaluate(script);
            }
            else
            {
                engine.Execute(script);
                if (!ScriptInvocationParser.IsValidFunctionName(invocation.FunctionName)
                    || engine.Evaluate($"typeof globalThis.{invocation.FunctionName} === 'function'") is not true)
                {
                    throw new MissingMethodException($"Exported function '{invocation.FunctionName}' was not defined by the script.");
                }

                var globalObject = (ScriptObject)engine.Script;
                var exportedValue = globalObject.GetProperty(invocation.FunctionName);
                if (exportedValue is not ScriptObject exportedFunction)
                {
                    throw new MissingMethodException($"Exported function '{invocation.FunctionName}' was not defined by the script.");
                }

                result = exportedFunction.Invoke(false, invocation.Arguments.Cast<object>().ToArray());
            }
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
        return RunCommandWithShellAndEncoding(command, "cmd", string.Empty);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Exposed as host object instance methods for ClearScript.")]
    public string RunCommandWithShell(string command, string shell)
    {
        return RunCommandWithShellAndEncoding(command, shell, string.Empty);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Exposed as host object instance methods for ClearScript.")]
    public string RunCommandWithShellAndEncoding(string command, string shell, string encoding)
    {
        EnsureCommandExecutionAllowed();

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command cannot be empty.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = "cmd";
        }

        var cancellationToken = _cancellationProvider();
        var startInfo = CreateStartInfo(command, shell, ResolveOutputEncoding(encoding));
        return RunAndCapture(startInfo, cancellationToken);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "$.exec only deserializes simple built-in string arrays and RunProcessOptions generated by the script bridge.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "$.exec only deserializes simple built-in string arrays and RunProcessOptions generated by the script bridge.")]
    public string RunProcess(string fileName, string argsJson, string optionsJson)
    {
        EnsureCommandExecutionAllowed();

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("File name cannot be empty.", nameof(fileName));
        }

        var args = JsonSerializer.Deserialize<string[]>(argsJson) ?? [];
        var options = JsonSerializer.Deserialize<RunProcessOptions>(optionsJson) ?? new RunProcessOptions();
        var showWindow = options.Window ?? false;
        var wait = options.Wait ?? !showWindow;
        var captureOutput = !showWindow && wait;
        var outputEncoding = ResolveOutputEncoding(options.Encoding);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = !showWindow,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
            StandardOutputEncoding = captureOutput ? outputEncoding : null,
            StandardErrorEncoding = captureOutput ? outputEncoding : null,
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var cancellationToken = _cancellationProvider();
        if (captureOutput)
        {
            return RunAndCapture(startInfo, cancellationToken);
        }

        using var process = new Process
        {
            StartInfo = startInfo,
        };
        process.Start();
        if (wait)
        {
            while (!process.WaitForExit(100))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Command failed with exit code {process.ExitCode}.");
            }

            return $"Process exited successfully: {fileName}";
        }

        return $"Started process {process.Id}: {fileName}";
    }

    private static ProcessStartInfo CreateStartInfo(string command, string shell, Encoding outputEncoding)
    {
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };

        if (string.Equals(shell, "cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/d /c " + command;
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

    private static Encoding ResolveOutputEncoding(string? encoding)
    {
        EnsureCodePagesEncodingProviderRegistered();

        if (string.IsNullOrWhiteSpace(encoding) || string.Equals(encoding, "oem", StringComparison.OrdinalIgnoreCase))
        {
            return GetDefaultCommandOutputEncoding();
        }

        if (string.Equals(encoding, "utf8", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encoding, "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        if (int.TryParse(encoding, out var codePage))
        {
            return Encoding.GetEncoding(codePage);
        }

        return Encoding.GetEncoding(encoding);
    }

    private static Encoding GetDefaultCommandOutputEncoding()
    {
        EnsureCodePagesEncodingProviderRegistered();

        if (!OperatingSystem.IsWindows())
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch (ArgumentException)
        {
            return Encoding.Default;
        }
    }

    private static void EnsureCodePagesEncodingProviderRegistered()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private void EnsureCommandExecutionAllowed()
    {
        if (!_allowCommandExecution)
        {
            throw new InvalidOperationException("Command execution is disabled for this script. Enable commandExecution in script metadata.");
        }
    }

    private static string RunAndCapture(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
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

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}

internal sealed class RunProcessOptions
{
    [JsonPropertyName("window")]
    public bool? Window { get; set; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }
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
