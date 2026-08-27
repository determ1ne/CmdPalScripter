using System.Text;
using Scripter.Core;

namespace Scripter.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (!CliOptions.TryParse(args, out var options, out var error))
        {
            if (!string.IsNullOrEmpty(error))
            {
                Console.Error.WriteLine($"Error: {error}");
            }

            PrintUsage();
            return string.IsNullOrEmpty(error) ? 0 : 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await RunAsync(options!, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped.");
            return 130;
        }
    }

    private static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var permissionRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Scripter",
            "Cli");
        var permissionService = new ScriptPermissionService(permissionRoot);
        var executionService = new ScriptExecutionService();
        using var session = executionService.CreateSession(options.DebugOptions);
        ConfigureDebuggerStatus(session, options.DebugOptions);

        if (options.DebugOptions.Mode != ScriptDebugMode.None)
        {
            Console.Error.WriteLine($"[debug] Listening on 127.0.0.1:{options.DebugOptions.ValidatedPort}.");
        }

        if (!options.Watch)
        {
            var result = await ExecuteCurrentFileAsync(options, session, permissionService, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || result?.WasCancelled == true)
            {
                return 130;
            }

            return result?.IsSuccess == true ? 0 : 1;
        }

        using var watcher = new ScriptFileWatcher(options.ScriptPath);
        watcher.Error += (_, eventArgs) => Console.Error.WriteLine($"[watch] Watcher error: {eventArgs.GetException().Message}");
        Console.Error.WriteLine($"[watch] Watching {options.ScriptPath}");

        var runTask = ExecuteCurrentFileAsync(options, session, permissionService, cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var changeTask = watcher.WaitForChangeAsync(cancellationToken);
            var completed = await Task.WhenAny(runTask, changeTask).ConfigureAwait(false);
            if (completed == changeTask)
            {
                await changeTask.ConfigureAwait(false);
                if (!runTask.IsCompleted)
                {
                    Console.Error.WriteLine("[watch] File changed; stopping the previous run.");
                    session.TryRequestStop();
                    await runTask.ConfigureAwait(false);
                }

                Console.Error.WriteLine("[watch] Change detected; running again.");
                runTask = ExecuteCurrentFileAsync(options, session, permissionService, cancellationToken);
                continue;
            }

            await runTask.ConfigureAwait(false);
            await changeTask.ConfigureAwait(false);
            Console.Error.WriteLine("[watch] Change detected; running again.");
            runTask = ExecuteCurrentFileAsync(options, session, permissionService, cancellationToken);
        }

        session.TryRequestStop();
        return 130;
    }

    private static async Task<ScriptExecutionResult?> ExecuteCurrentFileAsync(
        CliOptions options,
        ScriptExecutionSession session,
        ScriptPermissionService permissionService,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ScriptPath))
        {
            Console.Error.WriteLine($"[watch] Script is missing; waiting for it to be recreated: {options.ScriptPath}");
            return null;
        }

        string script;
        try
        {
            script = await ReadAllTextWithRetryAsync(options.ScriptPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Error reading script: {exception.Message}");
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Error reading script: {exception.Message}");
            return null;
        }

        var metadata = ScriptMetadataLoader.Load(options.ScriptPath);
        if (!string.Equals(metadata.Type, ScriptMetadata.ClearScriptType, StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Unsupported script type '{metadata.Type}'.";
            Console.Error.WriteLine($"Error: {message}");
            return new ScriptExecutionResult(false, message, 0, message);
        }

        if (!EnsurePermission(options, permissionService, script, metadata))
        {
            const string message = "Script permission was not granted.";
            Console.Error.WriteLine($"Error: {message}");
            return new ScriptExecutionResult(false, message, 0, message);
        }

        var invocation = options.FunctionName is null
            ? ScriptInvocation.WholeScript
            : new ScriptInvocation(options.FunctionName, options.Arguments);
        var request = ScriptExecutionRequest.ForFile(options.ScriptPath, script, invocation);
        var result = await session.ExecuteAsync(
            request,
            ScriptExecutionOptions.FromMetadata(metadata),
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            Console.Out.WriteLine(result.Output);
            Console.Error.WriteLine($"[scripter] Completed in {result.DurationMilliseconds} ms.");
        }
        else if (!result.WasCancelled)
        {
            Console.Error.WriteLine(result.Error ?? result.Output);
            Console.Error.WriteLine($"[scripter] Failed in {result.DurationMilliseconds} ms.");
        }

        return result;
    }

    private static bool EnsurePermission(
        CliOptions options,
        ScriptPermissionService permissionService,
        string script,
        ScriptMetadata metadata)
    {
        if (!ScriptPermissionService.RequiresPermission(metadata)
            || permissionService.IsApproved(options.ScriptPath, script, metadata))
        {
            return true;
        }

        if (options.Trust)
        {
            permissionService.Approve(options.ScriptPath, script, metadata);
            Console.Error.WriteLine("[permission] Approved the current script fingerprint because --trust was specified.");
            return true;
        }

        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("[permission] Interactive confirmation is unavailable. Re-run with --trust to approve this fingerprint.");
            return false;
        }

        Console.Error.WriteLine(ScriptPermissionDescriptionFormatter.BuildDescription(metadata.Name, metadata));
        Console.Error.Write("Allow and remember this exact fingerprint? [y/N] ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        permissionService.Approve(options.ScriptPath, script, metadata);
        return true;
    }

    private static async Task<string> ReadAllTextWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                lastError = exception;
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError!;
    }

    private static void ConfigureDebuggerStatus(ScriptExecutionSession session, ScriptDebugOptions options)
    {
        session.WaitingForDebugger += (_, _) =>
            Console.Error.WriteLine($"[debug] Waiting for a debugger on port {options.ValidatedPort}...");
        session.DebuggerConnected += (_, eventArgs) =>
            Console.Error.WriteLine($"[debug] Debugger connected on port {eventArgs.Port}.");
        session.DebuggerDisconnected += (_, eventArgs) =>
            Console.Error.WriteLine($"[debug] Debugger disconnected from port {eventArgs.Port}; the session remains active.");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Usage:
              scripter run <file.js> [--function name] [--trust] [-- args...]
              scripter watch <file.js> [--function name] [--trust] [-- args...]
              scripter debug <file.js> [--watch] [--wait | --break] [--port 9222] [--function name] [--trust] [-- args...]

            Debug modes:
              --wait   Wait for debugger attach, then run to configured breakpoints.
              --break  Wait for debugger attach and pause at the first line.
            """);
    }
}
