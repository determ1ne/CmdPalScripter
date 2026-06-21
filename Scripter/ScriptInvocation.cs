using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Scripter;

internal sealed record ScriptInvocation(string? FunctionName, IReadOnlyList<string> Arguments)
{
    public static ScriptInvocation WholeScript { get; } = new(null, []);
}

internal static class ScriptInvocationParser
{
    public static bool TryParse(string query, string commandName, out ScriptInvocation invocation)
    {
        invocation = ScriptInvocation.WholeScript;
        var trimmed = query.TrimStart();
        if (!trimmed.StartsWith(commandName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.Length > commandName.Length && !char.IsWhiteSpace(trimmed[commandName.Length]))
        {
            return false;
        }

        var argumentText = trimmed.Length == commandName.Length
            ? string.Empty
            : trimmed[commandName.Length..];
        invocation = new ScriptInvocation(commandName, ParseArguments(argumentText));
        return true;
    }

    public static IReadOnlyList<string> ParseArguments(string text)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var tokenStarted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (quote == '\0' && char.IsWhiteSpace(ch))
            {
                if (tokenStarted)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                if (quote == '\0')
                {
                    quote = ch;
                    tokenStarted = true;
                    continue;
                }

                if (quote == ch)
                {
                    quote = '\0';
                    continue;
                }
            }

            if (ch == '\\' && index + 1 < text.Length)
            {
                var next = text[index + 1];
                if (next == quote || next == '\\' || (quote == '\0' && (next is '\'' or '"' || char.IsWhiteSpace(next))))
                {
                    current.Append(next);
                    tokenStarted = true;
                    index++;
                    continue;
                }
            }

            current.Append(ch);
            tokenStarted = true;
        }

        if (tokenStarted)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    public static bool IsValidFunctionName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var first = true;
        foreach (var rune in value.EnumerateRunes())
        {
            if (first ? !IsIdentifierStart(rune) : !IsIdentifierPart(rune))
            {
                return false;
            }

            first = false;
        }

        return !first;
    }

    private static bool IsIdentifierStart(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return rune.Value is '_' or '$'
            || category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.LetterNumber;
    }

    private static bool IsIdentifierPart(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return IsIdentifierStart(rune)
            || rune.Value is 0x200C or 0x200D
            || category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.ConnectorPunctuation;
    }
}
