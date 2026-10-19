using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace Ankus.PgConfig;

/// <summary>
/// Reads and writes PostgreSQL extension control assignments without loading native code.
/// </summary>
/// <remarks>
/// Control files use ASCII because PostgreSQL cannot determine their encoding.
/// Comments, optional equals signs, quoted values, doubled quotes, backslash escapes,
/// and last-assignment precedence follow PostgreSQL's configuration syntax.
/// Include directives are not supported. This text codec handles syntax only; the selected
/// PostgreSQL version interprets parameter names and values.
/// </remarks>
public static partial class ExtensionControlFile
{
    /// <summary>
    /// Reads a control file and returns its immutable assignments.
    /// </summary>
    /// <param name="path">The control file path.</param>
    /// <returns>The case-sensitive parameter names and decoded values.</returns>
    /// <exception cref="FormatException">The file contains invalid or unsupported control syntax.</exception>
    public static IReadOnlyDictionary<string, string> Read(string path)
        => Parse(File.ReadAllText(path));

    /// <summary>
    /// Parses control text without changing its values or reading additional files.
    /// </summary>
    /// <param name="text">ASCII control assignments.</param>
    /// <returns>The case-sensitive parameter names and decoded values.</returns>
    /// <exception cref="ArgumentNullException">The text is null.</exception>
    /// <exception cref="FormatException">The text contains invalid syntax, non-ASCII text, NUL, or an include directive.</exception>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateText(text);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int number = 0;
        foreach (string input in text.Split('\n'))
        {
            number++;
            string line = input.TrimStart(' ', '\t', '\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            Match name = NamePattern().Match(line);
            if (!name.Success)
            {
                throw new FormatException($"Invalid control parameter at line {number}.");
            }

            if (name.Value is "include" or "include_dir" or "include_if_exists")
            {
                throw new FormatException("Control file include directives are not supported.");
            }

            string remainder = line[name.Length..].TrimStart(' ', '\t', '\r');
            if (remainder.StartsWith('='))
            {
                remainder = remainder[1..].TrimStart(' ', '\t', '\r');
            }

            Match value = ValuePattern().Match(remainder);
            if (!value.Success)
            {
                throw new FormatException($"Invalid control value at line {number}.");
            }

            string literal = value.Groups["value"].Value;
            values[name.Value] = literal.StartsWith('\'') ? Decode(literal) : literal;
        }

        return new ReadOnlyDictionary<string, string>(values);
    }

    /// <summary>
    /// Formats assignments in ordinal name order with PostgreSQL string escaping.
    /// </summary>
    /// <param name="values">The exact decoded assignments.</param>
    /// <returns>ASCII control text with LF line endings.</returns>
    /// <exception cref="ArgumentNullException">The dictionary or one of its values is null.</exception>
    /// <exception cref="FormatException">A name is invalid, or a value contains non-ASCII text or NUL.</exception>
    public static string Format(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new StringBuilder();
        foreach ((string name, string value) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (name.Length == 0 || NamePattern().Match(name).Length != name.Length ||
                name is "include" or "include_dir" or "include_if_exists")
            {
                throw new FormatException("Invalid control parameter name.");
            }

            ArgumentNullException.ThrowIfNull(value);
            ValidateText(value);
            result.Append(name).Append(" = '");
            foreach (char character in value)
            {
                result.Append(character switch
                {
                    '\'' => "''",
                    '\\' => "\\\\",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    '\b' => "\\b",
                    '\f' => "\\f",
                    _ => character.ToString(),
                });
            }

            result.Append("'\n");
        }

        return result.ToString();
    }

    private static void ValidateText(string text)
    {
        if (text.Any(static character => character == '\0' || character > 127))
        {
            throw new FormatException("Control files require ASCII text without NUL; use SQL for non-ASCII comments.");
        }
    }

    private static string Decode(string literal)
    {
        var value = new StringBuilder();
        for (int index = 1; index < literal.Length - 1; index++)
        {
            char character = literal[index];
            if (character == '\'')
            {
                index++;
            }
            else if (character == '\\')
            {
                character = literal[++index];
                if (character is >= '0' and <= '7')
                {
                    int code = character - '0';
                    for (int count = 1; count < 3 && literal[index + 1] is >= '0' and <= '7'; count++)
                    {
                        code = (code << 3) + literal[++index] - '0';
                    }

                    // PostgreSQL's octal escape writes one byte, including wraparound.
                    character = (char)(code & 255);
                }
                else
                {
                    character = character switch
                    {
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => character,
                    };
                }
            }

            value.Append(character);
        }

        string decoded = value.ToString();
        ValidateText(decoded);
        return decoded;
    }

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z_0-9]*(?:\.[A-Za-z_][A-Za-z_0-9]*)?", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\A(?<value>'(?:[^'\\\n]|\\.|'')*'|[A-Za-z_][A-Za-z_0-9./:\-]*|[+-]?(?:0x[0-9a-fA-F]+|[0-9]+)[a-zA-Z]*|[+-]?[0-9]*\.[0-9]*(?:[Ee][+-]?[0-9]+)?)[ \t\r]*(?:#.*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();
}
