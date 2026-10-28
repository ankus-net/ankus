using System;
using System.Collections.Generic;
using System.Text;

namespace Ankus.PgConfig;

/// <summary>
/// Reads shell-quoted PostgreSQL compiler arguments without executing shell expressions.
/// </summary>
internal static class UnixCompilerArguments
{
    /// <summary>
    /// Splits words while preserving quoted whitespace, empty words and escaped characters.
    /// </summary>
    /// <param name="arguments">The preprocessor flags reported by pg_config.</param>
    /// <returns>Individual arguments suitable for a process argument list.</returns>
    internal static IReadOnlyList<string> Split(string arguments)
    {
        var result = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        bool started = false;
        for (int index = 0; index < arguments.Length; index++)
        {
            char current = arguments[index];
            if (current == '\\' && quote != '\'')
            {
                if (++index == arguments.Length)
                {
                    throw new FormatException("PostgreSQL preprocessor flags end with an incomplete escape.");
                }

                char escaped = arguments[index];
                if (escaped == '\n')
                {
                    continue;
                }

                if (quote == '"' && escaped is not ('\\' or '"' or '$' or '`'))
                {
                    word.Append('\\');
                }

                word.Append(escaped);
                started = true;
            }
            else if (quote != '\0')
            {
                if (current == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(current);
                }
            }
            else if (current is '\'' or '"')
            {
                quote = current;
                started = true;
            }
            else if (current is ' ' or '\t' or '\r' or '\n')
            {
                if (started)
                {
                    result.Add(word.ToString());
                    word.Clear();
                    started = false;
                }
            }
            else
            {
                word.Append(current);
                started = true;
            }
        }

        if (quote != '\0')
        {
            throw new FormatException("PostgreSQL preprocessor flags contain an unterminated quoted argument.");
        }

        if (started)
        {
            result.Add(word.ToString());
        }

        return result.AsReadOnly();
    }
}
