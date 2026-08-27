using Scripter.Cli;
using Scripter.Core;

namespace Scripter.Core.Tests;

public sealed class CliOptionsTests
{
    [Fact]
    public void ParsesFunctionArgumentsAfterDelimiterExactly()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "args.js");
        File.WriteAllText(path, "function print(...args) { return args.join('|'); }");

        var parsed = CliOptions.TryParse(
            ["run", path, "--function", "print", "--", "", "Hello World"],
            out var options,
            out var error);

        Assert.True(parsed, error);
        Assert.Equal("print", options!.FunctionName);
        Assert.Equal(["", "Hello World"], options.Arguments);
    }

    [Fact]
    public void RejectsWaitAndBreakTogether()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "debug.js");
        File.WriteAllText(path, "1");

        var parsed = CliOptions.TryParse(["debug", path, "--wait", "--break"], out _, out var error);

        Assert.False(parsed);
        Assert.Contains("cannot be used together", error, StringComparison.OrdinalIgnoreCase);
    }
}
