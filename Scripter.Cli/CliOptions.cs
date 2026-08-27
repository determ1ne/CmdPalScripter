using Scripter.Core;

namespace Scripter.Cli;

internal sealed record CliOptions(
    string Command,
    string ScriptPath,
    string? FunctionName,
    IReadOnlyList<string> Arguments,
    bool Watch,
    bool Trust,
    ScriptDebugOptions DebugOptions)
{
    public static bool TryParse(string[] args, out CliOptions? options, out string? error)
    {
        options = null;
        error = null;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            error = string.Empty;
            return false;
        }

        var command = args[0].ToLowerInvariant();
        if (command is not ("run" or "watch" or "debug"))
        {
            error = $"Unknown command '{args[0]}'.";
            return false;
        }

        string? scriptPath = null;
        string? functionName = null;
        var trust = false;
        var watch = command == "watch";
        var port = 9222;
        var debugMode = command == "debug" ? ScriptDebugMode.Immediate : ScriptDebugMode.None;
        IReadOnlyList<string> scriptArguments = [];

        for (var index = 1; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg == "--")
            {
                scriptArguments = args[(index + 1)..];
                break;
            }

            switch (arg)
            {
                case "--function":
                    if (++index >= args.Length)
                    {
                        error = "--function requires a JavaScript function name.";
                        return false;
                    }

                    functionName = args[index];
                    break;
                case "--trust":
                    trust = true;
                    break;
                case "--watch":
                    if (command != "debug")
                    {
                        error = "--watch is only valid with the debug command; use 'scripter watch' otherwise.";
                        return false;
                    }

                    watch = true;
                    break;
                case "--wait":
                    if (command != "debug")
                    {
                        error = "--wait is only valid with the debug command.";
                        return false;
                    }

                    if (debugMode == ScriptDebugMode.Break)
                    {
                        error = "--wait and --break cannot be used together.";
                        return false;
                    }

                    debugMode = ScriptDebugMode.Wait;
                    break;
                case "--break":
                    if (command != "debug")
                    {
                        error = "--break is only valid with the debug command.";
                        return false;
                    }

                    if (debugMode == ScriptDebugMode.Wait)
                    {
                        error = "--wait and --break cannot be used together.";
                        return false;
                    }

                    debugMode = ScriptDebugMode.Break;
                    break;
                case "--port":
                    if (command != "debug" || ++index >= args.Length || !int.TryParse(args[index], out port) || port is < 1 or > 65535)
                    {
                        error = "--port requires a number from 1 to 65535 and is only valid with debug.";
                        return false;
                    }

                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"Unknown option '{arg}'. Use -- before script arguments.";
                        return false;
                    }

                    if (scriptPath is not null)
                    {
                        error = "Only one script path is allowed. Use -- before script arguments.";
                        return false;
                    }

                    scriptPath = arg;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            error = "A .js script path is required.";
            return false;
        }

        var fullPath = Path.GetFullPath(scriptPath);
        if (!fullPath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            error = "The script path must end with .js.";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = $"Script not found: {fullPath}";
            return false;
        }

        if (functionName is not null && !ScriptInvocationParser.IsValidFunctionName(functionName))
        {
            error = $"'{functionName}' is not a valid JavaScript function name.";
            return false;
        }

        options = new CliOptions(
            command,
            fullPath,
            functionName,
            scriptArguments,
            watch,
            trust,
            new ScriptDebugOptions(debugMode, port));
        return true;
    }
}
