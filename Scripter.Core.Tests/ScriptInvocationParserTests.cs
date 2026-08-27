using Scripter.Core;

namespace Scripter.Core.Tests;

public sealed class ScriptInvocationParserTests
{
    [Theory]
    [InlineData("\"Hello World\"", "Hello World")]
    [InlineData("\"Hello \\\"World\\\"\"", "Hello \"World\"")]
    [InlineData("'Hello'", "Hello")]
    [InlineData("\"\"", "")]
    public void ParseArgumentsPreservesQuotesAndEmptyValues(string text, string expected)
    {
        var argument = Assert.Single(ScriptInvocationParser.ParseArguments(text));
        Assert.Equal(expected, argument);
    }

    [Fact]
    public void TryParseRequiresACompleteFunctionName()
    {
        Assert.True(ScriptInvocationParser.TryParse("add 1 2", "add", out var invocation));
        Assert.Equal(["1", "2"], invocation.Arguments);
        Assert.False(ScriptInvocationParser.TryParse("address", "add", out _));
    }
}
