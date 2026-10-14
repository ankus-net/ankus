using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Ankus.Tool;

/// <summary>
/// Creates an independently restorable extension solution from the tool's bundled source templates.
/// </summary>
internal static partial class ProjectScaffolder
{
    private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        "__arglist", "__makeref", "__reftype", "__refvalue",
    };

    /// <summary>
    /// The reserved, column-name and type-or-function-name keywords of PostgreSQL 13–19, which quote_identifier quotes.
    /// Each later major's list contains every earlier one; the dotnet new template's extensionIdentifier symbol matches it.
    /// </summary>
    private static readonly HashSet<string> s_postgresKeywords = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "authorization", "between",
        "bigint", "binary", "bit", "boolean", "both", "case", "cast", "char", "character", "check", "coalesce",
        "collate", "collation", "column", "concurrently", "constraint", "create", "cross", "current_catalog",
        "current_date", "current_role", "current_schema", "current_time", "current_timestamp", "current_user", "dec",
        "decimal", "default", "deferrable", "desc", "distinct", "do", "else", "end", "except", "exists", "extract",
        "false", "fetch", "float", "for", "foreign", "freeze", "from", "full", "grant", "greatest", "group",
        "grouping", "having", "ilike", "in", "initially", "inner", "inout", "int", "integer", "intersect", "interval",
        "into", "is", "isnull", "join", "json", "json_array", "json_arrayagg", "json_exists", "json_object",
        "json_objectagg", "json_query", "json_scalar", "json_serialize", "json_table", "json_value", "lateral",
        "leading", "least", "left", "like", "limit", "localtime", "localtimestamp", "merge_action", "national",
        "natural", "nchar", "none", "normalize", "not", "notnull", "null", "nullif", "numeric", "offset", "on", "only",
        "or", "order", "out", "outer", "overlaps", "overlay", "placing", "position", "precision", "primary", "real",
        "references", "returning", "right", "row", "select", "session_user", "setof", "similar", "smallint", "some",
        "substring", "symmetric", "system_user", "table", "tablesample", "then", "time", "timestamp", "to", "trailing",
        "treat", "trim", "true", "union", "unique", "user", "using", "values", "varchar", "variadic", "verbose",
        "when", "where", "window", "with", "xmlattributes", "xmlconcat", "xmlelement", "xmlexists", "xmlforest",
        "xmlnamespaces", "xmlparse", "xmlpi", "xmlroot", "xmlserialize", "xmltable",
    };

    /// <summary>
    /// Gets the PostgreSQL keywords that require a quoted identifier on at least one supported server major.
    /// </summary>
    internal static IReadOnlySet<string> PostgresKeywords => s_postgresKeywords;

    /// <summary>
    /// Creates a version-matched extension solution in a new directory using an atomic move from staging.
    /// </summary>
    /// <param name="name">The portable project name whose namespace is normalized for C#.</param>
    /// <param name="output">The destination directory, or null to use the project name.</param>
    /// <param name="extension">The SQL extension name, or null to derive it from the project name.</param>
    /// <param name="backgroundWorker">Whether to include a preloaded worker and its backend test.</param>
    /// <param name="framework">The selected MSTest, xUnit or NUnit consumer framework.</param>
    /// <param name="token">Cancellation for template I/O and the final move.</param>
    /// <returns>The absolute destination path.</returns>
    internal static async Task<string> CreateAsync(string name, string? output, string? extension, bool backgroundWorker, string framework, CancellationToken token)
    {
        ValidateName(name);
        string? frameworkDirectory = framework switch
        {
            "mstest" => null,
            "xunit" => "Xunit",
            "nunit" => "Nunit",
            _ => throw new ArgumentException("The test framework must be mstest, xunit or nunit.", nameof(framework)),
        };
        extension ??= ToExtensionName(name);
        if (extension.Length is < 1 or > 63 || !(char.IsAsciiLetterLower(extension[0]) || extension[0] == '_') ||
            !extension.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            throw new ArgumentException("The extension name must be 1–63 lowercase ASCII letters, digits, or underscores, beginning with a letter or underscore.");
        }

        string destination = Path.GetFullPath(output ?? name);
        if (Path.Exists(destination))
        {
            throw new IOException($"The destination already exists: {destination}. Choose a new directory.");
        }

        string templateRoot = Path.Join(AppContext.BaseDirectory, "Templates", "Extension");
        string version = (await File.ReadAllTextAsync(Path.Join(AppContext.BaseDirectory, "ankus-tool.version"), token)).Trim();
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__PROJECT__"] = name,
            ["__NAMESPACE__"] = ToNamespace(name),
            ["__EXTENSION__"] = extension,
            ["__EXTENSION_IDENTIFIER__"] = QuoteIdentifier(extension),
            ["__VERSION__"] = version,
            ["__PRELOAD__"] = backgroundWorker ? "true" : "false",
            ["__TEST_FRAMEWORK__"] = framework switch { "xunit" => "xUnit", "nunit" => "NUnit", _ => "MSTest" },
            ["__WORKER_STATE__"] = "ankus.worker." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(extension)))[..24],
        };
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Join(parent, ".ankus-new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var roots = new List<string> { templateRoot };
            if (backgroundWorker)
            {
                roots.Add(Path.Join(AppContext.BaseDirectory, "Templates", "BackgroundWorker"));
            }

            if (frameworkDirectory is not null)
            {
                roots.Add(Path.Join(AppContext.BaseDirectory, "Templates", "Frameworks", frameworkDirectory));
                if (backgroundWorker)
                {
                    roots.Add(Path.Join(AppContext.BaseDirectory, "Templates", "Frameworks", frameworkDirectory + "Worker"));
                }
            }

            foreach (string root in roots)
            {
                // Regression expectations must be written after their SQL, matching the initial setup baseline.
                foreach (string source in Directory.EnumerateFiles(root, "*.template", SearchOption.AllDirectories)
                    .OrderBy(static path => path.EndsWith(".out.template", StringComparison.Ordinal))
                    .ThenBy(static path => path, StringComparer.Ordinal))
                {
                    string relative = Replace(Path.GetRelativePath(root, source)[..^".template".Length], replacements);
                    string target = Path.Join(staging, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string text = Replace(await File.ReadAllTextAsync(source, token), replacements);
                    await File.WriteAllTextAsync(target, text, new UTF8Encoding(false), token);
                }
            }

            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static string Replace(string value, Dictionary<string, string> replacements)
        => TokenPattern().Replace(value, match => replacements[match.Value]);

    [GeneratedRegex("__(PROJECT|NAMESPACE|EXTENSION|EXTENSION_IDENTIFIER|VERSION|PRELOAD|WORKER_STATE|TEST_FRAMEWORK)__",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Writes an identifier as PostgreSQL's quote_identifier does on every supported server major.
    /// </summary>
    /// <param name="identifier">The exact catalog identifier.</param>
    /// <returns>
    /// The identifier unchanged when it starts with a lowercase ASCII letter or underscore, contains only lowercase ASCII
    /// letters, digits and underscores, and is not a reserved, column-name or type-or-function-name keyword; otherwise the
    /// identifier in double quotes with embedded quotes doubled.
    /// </returns>
    internal static string QuoteIdentifier(string identifier)
    {
        bool safe = identifier.Length != 0 && (char.IsAsciiLetterLower(identifier[0]) || identifier[0] == '_') &&
            identifier.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_') &&
            !s_postgresKeywords.Contains(identifier);
        return safe ? identifier : "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name != name.Trim() || name.Split('.').Any(static part =>
                part.Length == 0 || !part.All(static c => char.IsLetterOrDigit(c) || c is '_' or '-' or ' ' ||
                    char.GetUnicodeCategory(c) is UnicodeCategory.LetterNumber or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                        UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format)))
        {
            throw new ArgumentException("The project name must contain 1–128 Unicode identifier characters, spaces, hyphens, or dots, without empty dot-separated segments or surrounding whitespace.");
        }

        string first = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" ||
            (first.Length == 4 && (first.StartsWith("COM", StringComparison.Ordinal) || first.StartsWith("LPT", StringComparison.Ordinal)) && first[3] is >= '1' and <= '9'))
        {
            throw new ArgumentException("The project name is reserved on Windows. Choose a portable name.");
        }
    }

    /// <summary>
    /// Applies the .NET template's namespace normalization before escaping C# keywords.
    /// </summary>
    /// <param name="name">The validated portable project name.</param>
    /// <returns>The C# namespace used by both generated project types.</returns>
    private static string ToNamespace(string name)
    {
        var result = new StringBuilder();
        for (int index = 0; index < name.Length; index++)
        {
            char value = name[index];
            if (index + 1 < name.Length && char.IsSurrogatePair(value, name[index + 1]))
            {
                result.Append('_');
                index++;
                continue;
            }

            bool first = result.Length == 0 || result[^1] == '.';
            bool start = value == '_' || char.GetUnicodeCategory(value) is UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
            bool part = start || char.GetUnicodeCategory(value) is UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.ConnectorPunctuation or UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.Format;
            if (first && !start && part)
            {
                result.Append('_').Append(value);
            }
            else
            {
                result.Append(first ? start ? value : '_' : part || value == '.' && index + 1 < name.Length ? value : '_');
            }
        }

        return string.Join('.', result.ToString().Split('.').Select(static identifier =>
            s_keywords.Contains(identifier) ? "@" + identifier : identifier));
    }

    /// <summary>
    /// Applies the same ASCII snake-case and leading-digit rules as the installed .NET template.
    /// </summary>
    /// <param name="name">The validated portable project name.</param>
    /// <returns>The lowercase SQL extension identity.</returns>
    private static string ToExtensionName(string name)
    {
        var result = new StringBuilder();
        for (int index = 0; index < name.Length; index++)
        {
            char c = name[index];
            if (char.IsAsciiLetterUpper(c) && index > 0 && char.IsAsciiLetterOrDigit(name[index - 1]) &&
                (char.IsAsciiLetterLower(name[index - 1]) || char.IsAsciiDigit(name[index - 1]) ||
                 (index + 1 < name.Length && char.IsAsciiLetterLower(name[index + 1]))))
            {
                result.Append('_');
            }

            result.Append(char.IsAsciiLetterOrDigit(c) || c == '_' ? char.ToLowerInvariant(c) : '_');
        }

        if (char.IsAsciiDigit(result[0]))
        {
            result.Insert(0, '_');
        }

        return result.ToString();
    }
}
