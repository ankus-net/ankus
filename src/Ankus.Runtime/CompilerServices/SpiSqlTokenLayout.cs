namespace Ankus.CompilerServices;

/// <summary>
/// Keeps interpolation-owned PostgreSQL parameters separate from literal positional tokens and quoted SQL text.
/// This recognizes token boundaries; PostgreSQL remains responsible for parsing and planning the statement.
/// </summary>
internal static class SpiSqlTokenLayout
{
    /// <summary>
    /// Validates generated parameters under both supported ordinary-string escape settings without a backend call.
    /// </summary>
    /// <param name="command">The built SQL, including generated parenthesized parameters.</param>
    /// <param name="positions">The exact dollar-token positions emitted by the interpolation builder.</param>
    /// <param name="allowLiteralParameters">Allows independent positional tokens in raw command text.</param>
    /// <param name="quotedFragments">
    /// Whether positions mark complete quoted SQL atoms; otherwise they mark interpolation-owned parameters.
    /// </param>
    /// <returns>The first binding ownership or SQL token-context failure, if any.</returns>
    internal static SqlInterpolationFailure Check(string command, IReadOnlyList<int> positions, bool allowLiteralParameters = false, bool quotedFragments = false)
    {
        SqlInterpolationFailure failure = Check(command, positions, escapeOrdinaryStrings: false, allowLiteralParameters, quotedFragments, out _);
        return failure != SqlInterpolationFailure.None || command.IndexOf('\\') < 0
            ? failure : Check(command, positions, escapeOrdinaryStrings: true, allowLiteralParameters, quotedFragments, out _);
    }

    /// <summary>
    /// Verifies a complete quoted-fragment layout whose trailing lexical context cannot consume a later fragment.
    /// </summary>
    /// <param name="command">The complete SQL text.</param>
    /// <param name="positions">The opening positions of complete quoted SQL atoms.</param>
    /// <returns>Whether every atom is outside another token and every token context is closed.</returns>
    internal static bool HasCompleteQuotedLayout(string command, IReadOnlyList<int> positions)
    {
        SqlInterpolationFailure failure = Check(command, positions, escapeOrdinaryStrings: false,
            allowLiteralParameters: true, quotedFragments: true, out bool complete);
        if (failure != SqlInterpolationFailure.None || !complete)
        {
            return false;
        }

        if (command.IndexOf('\\') < 0)
        {
            return true;
        }

        failure = Check(command, positions, escapeOrdinaryStrings: true,
            allowLiteralParameters: true, quotedFragments: true, out complete);
        return failure == SqlInterpolationFailure.None && complete;
    }

    /// <summary>
    /// Visits PostgreSQL token contexts without treating dollars in strings, identifiers or comments as parameters.
    /// </summary>
    /// <param name="command">The complete SQL text.</param>
    /// <param name="positions">The generated parameter token positions.</param>
    /// <param name="escapeOrdinaryStrings">Whether ordinary single quotes recognize backslash escapes.</param>
    /// <param name="allowLiteralParameters">Whether unowned positional tokens are permitted by the caller.</param>
    /// <param name="quotedFragments">Whether supplied positions identify complete quoted SQL atoms.</param>
    /// <param name="complete">Whether the command ends outside a quoted token or comment.</param>
    /// <returns>The first binding ownership or SQL token-context failure, if any.</returns>
    private static SqlInterpolationFailure Check(string command, IReadOnlyList<int> positions, bool escapeOrdinaryStrings,
        bool allowLiteralParameters, bool quotedFragments, out bool complete)
    {
        complete = true;
        int next = 0;
        for (int index = 0; index < command.Length;)
        {
            if (next < positions.Count && positions[next] < index)
            {
                return SqlInterpolationFailure.InsideSqlToken;
            }

            if (quotedFragments && next < positions.Count && positions[next] == index)
            {
                next++;
            }

            char value = command[index];
            char following = index + 1 < command.Length ? command[index + 1] : '\0';
            bool closed;
            if (value == '-' && following == '-')
            {
                index += 2;
                while (index < command.Length && command[index] is not ('\r' or '\n'))
                {
                    index++;
                }

                complete &= index < command.Length;
            }
            else if (value == '/' && following == '*')
            {
                index = SkipComment(command, index + 2, out closed);
                complete &= closed;
            }
            else if (value is '\'' or '"')
            {
                index = SkipQuoted(command, index, value == '\'' && escapeOrdinaryStrings, positions, ref next,
                    quotedFragments, out closed);
                complete &= closed;
            }
            else if (value is 'e' or 'E' && following == '\'')
            {
                index = SkipQuoted(command, index + 1, escapes: true, positions, ref next, quotedFragments, out closed);
                complete &= closed;
            }
            else if (value is 'u' or 'U' && following == '&' && index + 2 < command.Length && command[index + 2] is '\'' or '"')
            {
                index = SkipQuoted(command, index + 2, escapes: false, positions, ref next, quotedFragments, out closed);
                complete &= closed;
            }
            else if (value is 'b' or 'B' or 'x' or 'X' && following == '\'')
            {
                index = SkipQuoted(command, index + 1, escapes: false, positions, ref next, quotedFragments, out closed);
                complete &= closed;
            }
            else if (IsIdentifierStart(value))
            {
                do
                {
                    index++;
                }
                while (index < command.Length && (IsIdentifierStart(command[index]) || IsDigit(command[index]) || command[index] == '$'));
            }
            else if (value == '$' && IsDigit(following))
            {
                if (!quotedFragments && next < positions.Count && positions[next] == index)
                {
                    next++;
                }
                else if (!allowLiteralParameters)
                {
                    return SqlInterpolationFailure.ForeignParameter;
                }

                index += 2;
                while (index < command.Length && IsDigit(command[index]))
                {
                    index++;
                }
            }
            else if (value == '$')
            {
                int length = DollarDelimiterLength(command, index);
                if (length != 0)
                {
                    string delimiter = command.Substring(index, length);
                    int end = command.IndexOf(delimiter, index + length, StringComparison.Ordinal);
                    complete &= end >= 0;
                    index = end < 0 ? command.Length : end + length;
                }
                else
                {
                    index++;
                }
            }
            else
            {
                index++;
            }
        }

        return next == positions.Count ? SqlInterpolationFailure.None : SqlInterpolationFailure.InsideSqlToken;
    }

    /// <summary>
    /// Skips single or double quotes, including doubled delimiters and explicit escape-string characters.
    /// </summary>
    /// <param name="command">The SQL text.</param>
    /// <param name="start">The opening quote.</param>
    /// <param name="escapes">Whether a backslash escapes the following character.</param>
    /// <param name="positions">The supplied parameter or complete-fragment positions.</param>
    /// <param name="next">The next position that has not been observed outside quoted content.</param>
    /// <param name="quotedFragments">Whether a complete quoted atom may continue a preceding string token.</param>
    /// <param name="closed">Whether the quoted token has a closing delimiter.</param>
    /// <returns>The first character after the quoted token, or the end of incomplete SQL.</returns>
    private static int SkipQuoted(string command, int start, bool escapes, IReadOnlyList<int> positions, ref int next,
        bool quotedFragments, out bool closed)
    {
        closed = false;
        char quote = command[start];
        for (int index = start + 1; index < command.Length; index++)
        {
            if (escapes && command[index] == '\\')
            {
                index++;
            }
            else if (command[index] == quote)
            {
                if (index + 1 < command.Length && command[index + 1] == quote)
                {
                    index++;
                }
                else
                {
                    int continuation = quote == '\'' ? ContinuedQuote(command, index + 1) : -1;
                    if (continuation >= 0)
                    {
                        if (quotedFragments && next < positions.Count && positions[next] == continuation)
                        {
                            next++;
                        }

                        index = continuation;
                        continue;
                    }

                    closed = true;
                    return index + 1;
                }
            }
        }

        return command.Length;
    }

    /// <summary>
    /// Preserves a string token's escape mode when PostgreSQL concatenates quoted parts across a newline.
    /// </summary>
    /// <param name="command">The SQL text.</param>
    /// <param name="index">The character after a quoted part.</param>
    /// <returns>The continued opening quote, or minus one for a complete string token.</returns>
    private static int ContinuedQuote(string command, int index)
    {
        bool newline = false;
        while (index < command.Length)
        {
            if (command[index] is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                newline |= command[index] is '\n' or '\r';
                index++;
            }
            else if (command[index] == '-' && index + 1 < command.Length && command[index + 1] == '-')
            {
                index += 2;
                while (index < command.Length && command[index] is not ('\n' or '\r'))
                {
                    index++;
                }
            }
            else
            {
                break;
            }
        }

        return newline && index < command.Length && command[index] == '\'' ? index : -1;
    }

    /// <summary>
    /// Skips PostgreSQL's nested block comments without inspecting their apparent positional tokens.
    /// </summary>
    /// <param name="command">The SQL text.</param>
    /// <param name="index">The first character inside the initial comment.</param>
    /// <param name="closed">Whether every nested block comment has a closing delimiter.</param>
    /// <returns>The first character after the comment, or the end of incomplete SQL.</returns>
    private static int SkipComment(string command, int index, out bool closed)
    {
        closed = false;
        int depth = 1;
        while (index + 1 < command.Length)
        {
            if (command[index] == '/' && command[index + 1] == '*')
            {
                depth++;
                index += 2;
            }
            else if (command[index] == '*' && command[index + 1] == '/')
            {
                index += 2;
                if (--depth == 0)
                {
                    closed = true;
                    return index;
                }
            }
            else
            {
                index++;
            }
        }

        return command.Length;
    }

    /// <summary>
    /// Measures a PostgreSQL dollar-quote delimiter whose tag has identifier syntax without a dollar character.
    /// </summary>
    /// <param name="command">The SQL text.</param>
    /// <param name="start">The initial dollar character.</param>
    /// <returns>The delimiter length, or zero for a non-delimiter dollar.</returns>
    private static int DollarDelimiterLength(string command, int start)
    {
        int index = start + 1;
        if (index < command.Length && command[index] == '$')
        {
            return 2;
        }

        if (index == command.Length || !IsIdentifierStart(command[index]))
        {
            return 0;
        }

        do
        {
            index++;
        }
        while (index < command.Length && (IsIdentifierStart(command[index]) || IsDigit(command[index])));
        return index < command.Length && command[index] == '$' ? index - start + 1 : 0;
    }

    /// <summary>
    /// Recognizes PostgreSQL's ASCII positional ordinal digits independently of culture.
    /// </summary>
    private static bool IsDigit(char value) => value is >= '0' and <= '9';

    /// <summary>
    /// Recognizes identifier starts, including server-encoding multibyte characters.
    /// </summary>
    private static bool IsIdentifierStart(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or >= '\u0080';
}

/// <summary>
/// Distinguishes token-context failures from a binding vector's ownership failures.
/// </summary>
internal enum SqlInterpolationFailure
{
    /// <summary>
    /// Every supplied position denotes an independent SQL token.
    /// </summary>
    None,

    /// <summary>
    /// A supplied position is inside quoted, identifier or commented text.
    /// </summary>
    InsideSqlToken,

    /// <summary>
    /// A positional parameter is not owned by the supplied binding positions.
    /// </summary>
    ForeignParameter,
}
