using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using FormsMessageBox = System.Windows.Forms.MessageBox;

namespace Scripter.Core;

public sealed class ScriptExecutionService
{
    private readonly object _gate = new();
    private ScriptExecutionSession? _activeSession;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _activeSession is not null;
            }
        }
    }

    public ScriptExecutionResult Execute(
        ScriptExecutionRequest request,
        ScriptExecutionOptions? options = null,
        ScriptDebugOptions? debugOptions = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, options, debugOptions, cancellationToken).GetAwaiter().GetResult();

    public async Task<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        ScriptExecutionOptions? options = null,
        ScriptDebugOptions? debugOptions = null,
        CancellationToken cancellationToken = default)
    {
        var session = CreateSession(debugOptions ?? ScriptDebugOptions.Disabled);
        lock (_gate)
        {
            if (_activeSession is not null)
            {
                session.Dispose();
                return new ScriptExecutionResult(false, "Another script is already running.", 0, "Another script is already running.");
            }

            _activeSession = session;
        }

        try
        {
            return await session.ExecuteAsync(request, options ?? ScriptExecutionOptions.Default, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
            }
        }
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Sessions are created through the execution service public API.")]
    public ScriptExecutionSession CreateSession(ScriptDebugOptions? debugOptions = null) =>
        new(debugOptions ?? ScriptDebugOptions.Disabled);

    public bool TryRequestStop()
    {
        lock (_gate)
        {
            return _activeSession?.TryRequestStop() == true;
        }
    }
}

public sealed class ScriptExecutionSession : IDisposable
{
    private const string BuiltinsBridge =
        "globalThis.messageBox = function(text, caption) { return caption === undefined ? builtins.MessageBox(text) : builtins.MessageBoxWithCaption(text, caption); };"
        + "globalThis.$ = function(command, options) { "
        + "var shell = options && typeof options === 'object' ? options.shell : undefined; "
        + "var encoding = options && typeof options === 'object' ? options.encoding : undefined; "
        + "return builtins.RunCommandWithShellAndEncoding(String(command), shell == null ? 'cmd' : String(shell), encoding == null ? '' : String(encoding)); };"
        + "globalThis.$.exec = function(fileName, args, options) { "
        + "if (args === undefined || args === null) args = []; "
        + "if (!Array.isArray(args)) throw new TypeError('$.exec args must be an array.'); "
        + "var normalizedArgs = args.map(function(arg) { return arg === undefined || arg === null ? '' : String(arg); }); "
        + "return builtins.RunProcess(String(fileName), JSON.stringify(normalizedArgs), JSON.stringify(options || {})); };"
        + "globalThis.$.use = function(shell) { return function(command, options) { "
        + "var merged = options && typeof options === 'object' ? Object.assign({}, options) : {}; "
        + "merged.shell = shell; return globalThis.$(command, merged); }; };";

    private const string NativeFfiBridge =
        "(function(nativeFfi) { globalThis.ffi = Object.freeze({"
        + "bind: function(dll, exportName, signature) { return nativeFfi.Bind(String(dll), String(exportName), String(signature)); },"
        + "bindOrdinal: function(dll, ordinal, signature) { var value = Number(ordinal); "
        + "if (!Number.isInteger(value)) throw new TypeError('FFI export ordinal must be an integer.'); "
        + "return nativeFfi.BindOrdinal(String(dll), value, String(signature)); }"
        + "}); })(__scripterFfi); delete globalThis.__scripterFfi;";

    private readonly object _gate = new();
    private readonly ScriptDebugOptions _debugOptions;
    private readonly V8Runtime _runtime;
    private readonly string _runtimeName = "Scripter-" + Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _debuggerConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private V8ScriptEngine? _activeEngine;
    private CancellationTokenSource? _activeCancellation;
    private bool _waitCompleted;
    private bool _disposed;

    public ScriptExecutionSession(ScriptDebugOptions debugOptions)
    {
        _debugOptions = debugOptions with { Port = debugOptions.ValidatedPort };
        if (_debugOptions.Mode == ScriptDebugMode.None)
        {
            _runtime = new V8Runtime(_runtimeName);
        }
        else
        {
            var flags = V8RuntimeFlags.EnableDebugging | V8RuntimeFlags.EnableRemoteDebugging;
            _runtime = new V8Runtime(_runtimeName, flags, _debugOptions.Port);
            V8Runtime.DebuggerConnected += OnDebuggerConnected;
            V8Runtime.DebuggerDisconnected += OnDebuggerDisconnected;
        }
    }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _activeCancellation is not null;
            }
        }
    }

    public ScriptDebugOptions DebugOptions => _debugOptions;

    public event EventHandler<ScriptDebuggerEventArgs>? DebuggerConnected;

    public event EventHandler<ScriptDebuggerEventArgs>? DebuggerDisconnected;

    public event EventHandler? WaitingForDebugger;

    public async Task<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        ScriptExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ScriptPath);

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            if (_activeCancellation is not null)
            {
                return new ScriptExecutionResult(false, "Another script is already running in this session.", 0, "Another script is already running in this session.");
            }

            _activeCancellation = runCancellation;
        }

        try
        {
            if ((_debugOptions.Mode is ScriptDebugMode.Wait or ScriptDebugMode.Break) && !_waitCompleted)
            {
                WaitingForDebugger?.Invoke(this, EventArgs.Empty);
                try
                {
                    await _debuggerConnected.Task.WaitAsync(runCancellation.Token).ConfigureAwait(false);
                    _waitCompleted = true;
                }
                catch (OperationCanceledException)
                {
                    return new ScriptExecutionResult(false, "Execution stopped.", 0, "Execution stopped.", true);
                }
            }

            return await Task.Run(
                () => ExecuteCore(request, options ?? ScriptExecutionOptions.Default, runCancellation.Token),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeCancellation, runCancellation))
                {
                    _activeCancellation = null;
                }
            }
        }
    }

    public bool TryRequestStop()
    {
        lock (_gate)
        {
            if (_activeCancellation is null)
            {
                return false;
            }

            _activeCancellation?.Cancel();
            _activeEngine?.Interrupt();
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        TryRequestStop();
        if (_debugOptions.Mode != ScriptDebugMode.None)
        {
            V8Runtime.DebuggerConnected -= OnDebuggerConnected;
            V8Runtime.DebuggerDisconnected -= OnDebuggerDisconnected;
        }

        _runtime.Dispose();
    }

    private ScriptExecutionResult ExecuteCore(
        ScriptExecutionRequest request,
        ScriptExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var flags = V8ScriptEngineFlags.EnableTaskPromiseConversion;
            if (_debugOptions.Mode != ScriptDebugMode.None)
            {
                flags |= V8ScriptEngineFlags.EnableDebugging;
            }

            using var nativeFfi = options.NativeFfi ? new NativeFfi() : null;
            using var engine = _runtime.CreateScriptEngine(Path.GetFileName(request.ScriptPath), flags);
            lock (_gate)
            {
                _activeEngine = engine;
            }

            using var cancellationRegistration = cancellationToken.Register(engine.Interrupt);
            ConfigureEngine(engine, options, nativeFfi, cancellationToken);

            var documentInfo = new DocumentInfo(new Uri(Path.GetFullPath(request.ScriptPath)));
            if (request.SourceMapUri is not null)
            {
                documentInfo.SourceMapUri = request.SourceMapUri;
            }

            // Engine-wide pause-on-start stops in the internal builtins bridge. A debugger
            // statement prefixed without a newline preserves the user's URI and line numbers.
            var userSource = _debugOptions.Mode == ScriptDebugMode.Break
                ? "debugger;" + request.ScriptContent
                : request.ScriptContent;

            object? result;
            if (request.Invocation.FunctionName is null)
            {
                result = engine.Evaluate(documentInfo, userSource);
            }
            else
            {
                engine.Execute(documentInfo, userSource);
                result = InvokeExportedFunction(engine, request.Invocation);
            }

            result = ScriptBuiltins.ResolveAwaitable(result, cancellationToken);
            stopwatch.Stop();
            var output = result switch
            {
                null => "null",
                Undefined => "undefined",
                _ => result.ToString() ?? string.Empty,
            };
            return new ScriptExecutionResult(true, output, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ScriptExecutionResult(false, "Execution stopped.", stopwatch.ElapsedMilliseconds, "Execution stopped.", true);
        }
        catch (ScriptEngineException exception)
        {
            stopwatch.Stop();
            var message = string.IsNullOrWhiteSpace(exception.ErrorDetails) ? exception.Message : exception.ErrorDetails;
            return new ScriptExecutionResult(false, message, stopwatch.ElapsedMilliseconds, message);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new ScriptExecutionResult(false, exception.ToString(), stopwatch.ElapsedMilliseconds, exception.ToString());
        }
        finally
        {
            lock (_gate)
            {
                _activeEngine = null;
            }
        }
    }

    private static void ConfigureEngine(
        V8ScriptEngine engine,
        ScriptExecutionOptions options,
        NativeFfi? nativeFfi,
        CancellationToken cancellationToken)
    {
        engine.AddHostObject("builtins", new ScriptBuiltins(() => cancellationToken, options.CommandExecution));
        engine.ExecuteCommand(BuiltinsBridge);

        if (options.DynamicImport)
        {
            engine.AddHostObject("host", new ScriptHostApi(engine));
            engine.ExecuteCommand("globalThis.importType = function(typeName) { return host.ImportType(typeName); };");
        }

        foreach (var exposedType in options.NativeTypes)
        {
            engine.AddHostType(exposedType.Name, ScriptHostApi.FindType(exposedType.TypeName));
        }

        if (nativeFfi is not null)
        {
            engine.AddHostObject("__scripterFfi", nativeFfi);
            engine.ExecuteCommand(NativeFfiBridge);
        }
    }

    private static object? InvokeExportedFunction(V8ScriptEngine engine, ScriptInvocation invocation)
    {
        var functionName = invocation.FunctionName!;
        if (!ScriptInvocationParser.IsValidFunctionName(functionName)
            || engine.Evaluate($"typeof globalThis.{functionName} === 'function'") is not true)
        {
            throw new MissingMethodException($"Exported function '{functionName}' was not defined by the script.");
        }

        var globalObject = (ScriptObject)engine.Script;
        if (globalObject.GetProperty(functionName) is not ScriptObject exportedFunction)
        {
            throw new MissingMethodException($"Exported function '{functionName}' was not defined by the script.");
        }

        return exportedFunction.Invoke(false, invocation.Arguments.Cast<object>().ToArray());
    }

    private void OnDebuggerConnected(object? sender, V8RuntimeDebuggerEventArgs args)
    {
        if (!string.Equals(args.Name, _runtimeName, StringComparison.Ordinal))
        {
            return;
        }

        _debuggerConnected.TrySetResult();
        DebuggerConnected?.Invoke(this, new ScriptDebuggerEventArgs(args.Port));
    }

    private void OnDebuggerDisconnected(object? sender, V8RuntimeDebuggerEventArgs args)
    {
        if (string.Equals(args.Name, _runtimeName, StringComparison.Ordinal))
        {
            DebuggerDisconnected?.Invoke(this, new ScriptDebuggerEventArgs(args.Port));
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

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Exposed as a ClearScript host object method.")]
    public int MessageBox(string text) => (int)FormsMessageBox.Show(text, "Scripter");

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Exposed as a ClearScript host object method.")]
    public int MessageBoxWithCaption(string text, string caption) => (int)FormsMessageBox.Show(text, caption);

    public string RunCommand(string command) => RunCommandWithShellAndEncoding(command, "cmd", string.Empty);

    public string RunCommandWithShell(string command, string shell) => RunCommandWithShellAndEncoding(command, shell, string.Empty);

    public string RunCommandWithShellAndEncoding(string command, string shell, string encoding)
    {
        EnsureCommandExecutionAllowed();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command cannot be empty.", nameof(command));
        }

        var selectedShell = string.IsNullOrWhiteSpace(shell) ? "cmd" : shell;
        return RunAndCapture(CreateStartInfo(command, selectedShell, ResolveOutputEncoding(encoding)), _cancellationProvider());
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only simple generated bridge types are deserialized.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Only simple generated bridge types are deserialized.")]
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

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        if (wait)
        {
            try
            {
                WaitForExit(process, cancellationToken);
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Command failed with exit code {process.ExitCode}.");
                }

                return $"Process exited successfully: {fileName}";
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

        return $"Started process {process.Id}: {fileName}";
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Accessing Task<TResult>.Result via reflection.")]
    public static object? ResolveAwaitable(object? value, CancellationToken cancellationToken)
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
        return task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance)?.GetValue(task);
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
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (string.IsNullOrWhiteSpace(encoding) || string.Equals(encoding, "oem", StringComparison.OrdinalIgnoreCase))
        {
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

        if (string.Equals(encoding, "utf8", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encoding, "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.UTF8;
        }

        return int.TryParse(encoding, out var codePage) ? Encoding.GetEncoding(codePage) : Encoding.GetEncoding(encoding);
    }

    private static string RunAndCapture(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            WaitForExit(process, cancellationToken);
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

    private static void WaitForExit(Process process, CancellationToken cancellationToken)
    {
        while (!process.WaitForExit(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private void EnsureCommandExecutionAllowed()
    {
        if (!_allowCommandExecution)
        {
            throw new InvalidOperationException("Command execution is disabled for this script. Enable commandExecution in script metadata.");
        }
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

    public ScriptHostApi(V8ScriptEngine engine) => _engine = engine;

    public object ImportType(string typeName)
    {
        var alias = "__importedType" + Interlocked.Increment(ref _importCounter);
        _engine.AddHostType(alias, FindType(typeName));
        return _engine.Evaluate(alias);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "Type names are supplied at runtime.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Dynamic type lookup is required by importType.")]
    public static Type FindType(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
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
                type = assembly.GetTypes().FirstOrDefault(candidate => string.Equals(candidate.Name, typeName, StringComparison.Ordinal));
            }
            catch (ReflectionTypeLoadException exception)
            {
                type = exception.Types.FirstOrDefault(candidate => candidate is not null && string.Equals(candidate.Name, typeName, StringComparison.Ordinal));
            }

            if (type is not null)
            {
                return type;
            }
        }

        throw new DllNotFoundException("Type not found: " + typeName);
    }
}
