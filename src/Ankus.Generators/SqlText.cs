using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Formats identifiers and literal values for generated PostgreSQL declarations.
/// </summary>
internal static class SqlText
{
    /// <summary>
    /// Converts a managed identifier to the generator's snake-case convention.
    /// </summary>
    /// <param name="value">The unescaped managed identifier.</param>
    /// <returns>The default SQL name.</returns>
    internal static string SnakeCase(string value)
    {
        var result = new StringBuilder();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsUpper(current) && index > 0 &&
                (char.IsLower(value[index - 1]) || (index + 1 < value.Length && char.IsLower(value[index + 1]))))
            {
                result.Append('_');
            }

            result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }

    /// <summary>
    /// Checks identifier length in UTF-8 without accepting embedded zeroes or invalid Unicode.
    /// </summary>
    /// <param name="value">The proposed identifier.</param>
    /// <returns>Whether PostgreSQL can retain the complete identifier without truncation.</returns>
    internal static bool IsIdentifier(string? value)
    {
        return !string.IsNullOrEmpty(value) && IsText(value!) && Encoding.UTF8.GetByteCount(value!) <= 63;
    }

    /// <summary>
    /// Rejects embedded zeroes and invalid Unicode before emitting UTF-8 SQL artifacts.
    /// </summary>
    /// <param name="value">The proposed literal or SQL expression.</param>
    /// <returns>Whether the complete text can be represented in PostgreSQL UTF-8.</returns>
    internal static bool IsText(string value)
    {
        if (value.Contains('\0'))
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Quotes an already validated SQL identifier, including embedded double quotes.
    /// </summary>
    /// <param name="value">The exact identifier.</param>
    /// <returns>The quoted identifier.</returns>
    internal static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Escapes a string literal independently of standard_conforming_strings.
    /// </summary>
    /// <param name="value">The literal's content.</param>
    /// <returns>An escape-string SQL literal.</returns>
    internal static string Literal(string value) => "E'" + value.Replace("\\", "\\\\").Replace("'", "''")
        .Replace("\r", "\\r").Replace("\n", "\\n") + "'";

    /// <summary>
    /// Checks that a default expression stays inside the parentheses that enclose it, as PostgreSQL's scanner reads it.
    /// </summary>
    /// <param name="expression">The authored default expression.</param>
    /// <returns>
    /// Whether every string, quoted identifier, dollar quote and block comment closes, parentheses balance, no semicolon
    /// ends the statement, and a token remains outside comments. Both <c>standard_conforming_strings</c> settings must
    /// agree, because the installation script may run under either.
    /// </returns>
    internal static bool IsCompleteExpression(string expression)
        => IsCompleteExpression(expression, escapeOrdinaryStrings: false) && IsCompleteExpression(expression, escapeOrdinaryStrings: true);

    /// <summary>
    /// Scans one expression under one string-escaping rule.
    /// </summary>
    /// <param name="text">The authored default expression.</param>
    /// <param name="escapeOrdinaryStrings">Whether ordinary single-quoted strings treat backslash as an escape.</param>
    /// <returns>Whether the expression is complete under that rule.</returns>
    private static bool IsCompleteExpression(string text, bool escapeOrdinaryStrings)
    {
        int depth = 0;
        bool token = false;
        int index = 0;
        while (index < text.Length)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (current == '-' && next == '-')
            {
                // The emitter closes the parentheses on a new line, so a line comment may run to the end.
                int end = text.IndexOf('\n', index);
                index = end < 0 ? text.Length : end + 1;
                continue;
            }

            if (current == '/' && next == '*')
            {
                // PostgreSQL block comments nest.
                int nesting = 1;
                index += 2;
                while (nesting > 0 && index < text.Length)
                {
                    if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '*')
                    {
                        nesting++;
                        index += 2;
                    }
                    else if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '/')
                    {
                        nesting--;
                        index += 2;
                    }
                    else
                    {
                        index++;
                    }
                }

                if (nesting > 0)
                {
                    return false;
                }

                continue;
            }

            token |= !char.IsWhiteSpace(current);
            if (current == '\'')
            {
                index = SkipQuoted(text, index + 1, '\'', escapeOrdinaryStrings);
            }
            else if (current == '"')
            {
                index = SkipQuoted(text, index + 1, '"', backslashEscapes: false);
            }
            else if (current == '$' && DollarTag(text, index) is { } tag)
            {
                int close = text.IndexOf(tag, index + tag.Length, StringComparison.Ordinal);
                index = close < 0 ? -1 : close + tag.Length;
            }
            else if (IsIdentifierStart(current))
            {
                int start = index;
                while (index < text.Length && IsIdentifierPart(text[index]))
                {
                    index++;
                }

                // E'...' always escapes backslashes and B'...' and X'...' never do. Other strings after a name,
                // including N'...', are ordinary and are read on the next pass of the loop.
                if (index - start == 1 && index < text.Length && text[index] == '\'' &&
                    current is 'e' or 'E' or 'b' or 'B' or 'x' or 'X')
                {
                    index = SkipQuoted(text, index + 1, '\'', current is 'e' or 'E');
                }
                else if (index - start == 1 && current is 'u' or 'U' && index + 1 < text.Length && text[index] == '&' &&
                    text[index + 1] is '\'' or '"')
                {
                    index = SkipQuoted(text, index + 2, text[index + 1], backslashEscapes: false);
                }
            }
            else
            {
                if (current == ';')
                {
                    return false;
                }

                depth += current == '(' ? 1 : current == ')' ? -1 : 0;
                if (depth < 0)
                {
                    return false;
                }

                index++;
            }

            if (index < 0)
            {
                return false;
            }
        }

        return depth == 0 && token;
    }

    /// <summary>
    /// Finds the end of a quoted token whose doubled quote stands for one quote.
    /// </summary>
    /// <param name="text">The expression.</param>
    /// <param name="index">The first character after the opening quote.</param>
    /// <param name="quote">The closing quote character.</param>
    /// <param name="backslashEscapes">Whether a backslash escapes the following character.</param>
    /// <returns>The index after the closing quote, or -1 when the token does not close.</returns>
    private static int SkipQuoted(string text, int index, char quote, bool backslashEscapes)
    {
        while (index < text.Length)
        {
            char current = text[index];
            if (backslashEscapes && current == '\\')
            {
                index += 2;
            }
            else if (current == quote)
            {
                if (index + 1 < text.Length && text[index + 1] == quote)
                {
                    index += 2;
                }
                else
                {
                    return index + 1;
                }
            }
            else
            {
                index++;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads a dollar-quote delimiter such as <c>$$</c> or <c>$body$</c>; a positional parameter such as <c>$1</c> is not one.
    /// </summary>
    /// <param name="text">The expression.</param>
    /// <param name="index">The position of the opening dollar sign.</param>
    /// <returns>The complete delimiter, or null when the dollar sign does not open a quote.</returns>
    private static string? DollarTag(string text, int index)
    {
        int end = index + 1;
        if (end < text.Length && IsIdentifierStart(text[end]))
        {
            while (end < text.Length && IsIdentifierPart(text[end]) && text[end] != '$')
            {
                end++;
            }
        }

        return end < text.Length && text[end] == '$' ? text.Substring(index, end - index + 1) : null;
    }

    /// <summary>
    /// Matches PostgreSQL's identifier start: an ASCII letter, underscore or any non-ASCII character.
    /// </summary>
    private static bool IsIdentifierStart(char value)
        => value is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' || value >= '\u0080';

    /// <summary>
    /// Matches PostgreSQL's identifier continuation, which adds digits and dollar signs.
    /// </summary>
    private static bool IsIdentifierPart(char value) => IsIdentifierStart(value) || value is (>= '0' and <= '9') or '$';

    /// <summary>
    /// Renders an argument default clause that a trailing SQL line comment cannot extend into the following declaration.
    /// </summary>
    /// <param name="expression">The exact authored or converted default expression.</param>
    /// <returns>
    /// The parenthesized default. When the expression contains <c>--</c>, which can begin a comment that runs to the end
    /// of the line, the closing parenthesis follows a newline; the added whitespace does not change the expression.
    /// </returns>
    internal static string Default(string expression)
        => " DEFAULT (" + expression + (expression.Contains("--") ? "\n)" : ")");
}
