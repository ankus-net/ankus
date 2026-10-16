#:property TargetFramework=net10.0
#:property PackAsTool=false
#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Text;

try
{
    if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--check") || !File.Exists("Ankus.slnx"))
    {
        throw new InvalidOperationException("Run from the repository root: dotnet run --file eng/Ankus.SqlStates.cs -- <postgres-checkout> [--check]");
    }

    (int Major, string Tag)[] sources =
    [
        (13, "REL_13_23"), (14, "REL_14_24"), (15, "REL_15_19"), (16, "REL_16_15"),
        (17, "REL_17_11"), (18, "REL_18_6"), (19, "REL_19_RC1"),
    ];
    var definitions = new Dictionary<string, (string Code, string? Condition, List<int> Versions)>(StringComparer.Ordinal);
    foreach ((int major, string tag) in sources)
    {
        string source = await ReadCatalog(args[0], tag);
        int entries = 0;
        foreach (string line in source.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("Section:", StringComparison.Ordinal))
            {
                continue;
            }

            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length is < 3 or > 4 || fields[0].Length != 5 ||
                fields[0].Any(static character => character is not (>= '0' and <= '9' or >= 'A' and <= 'Z')) ||
                fields[1] is not ("E" or "W" or "S") || !fields[2].StartsWith("ERRCODE_", StringComparison.Ordinal) ||
                fields[2].Length <= 8 || fields[2].Any(static character => character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) ||
                (fields.Length == 4 && fields[3].Any(static character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))))
            {
                throw new InvalidOperationException($"Invalid catalog entry in {tag}: {line}");
            }

            string code = fields[0];
            string macro = fields[2];
            string? condition = fields.Length == 4 ? fields[3] : null;
            if (definitions.TryGetValue(macro, out (string Code, string? Condition, List<int> Versions) previous))
            {
                if (previous.Code != code || previous.Condition != condition || previous.Versions.Contains(major))
                {
                    throw new InvalidOperationException($"Conflicting or duplicate definition of {macro} in {tag}.");
                }

                previous.Versions.Add(major);
            }
            else
            {
                definitions.Add(macro, (code, condition, [major]));
            }

            entries++;
        }

        if (entries == 0)
        {
            throw new InvalidOperationException($"No SQLSTATE entries in {tag}.");
        }
    }

    var generated = new StringBuilder("""
        // Regenerate from the pinned PostgreSQL catalogs with eng/Ankus.SqlStates.cs.
        namespace Ankus;

        /// <summary>
        /// Provides named PostgreSQL SQLSTATE strings for error reporting, diagnostics and exception filters.
        /// </summary>
        /// <remarks>
        /// Includes the union of the PostgreSQL 13 through 18 and 19 RC1 catalogs, including aliases and retired names.
        /// A constant does not imply that its associated server feature exists in every PostgreSQL version.
        /// These names supplement the string-based diagnostic APIs; extension-specific SQLSTATE strings remain supported.
        /// </remarks>
        public static class PgSqlStates
        {
        """);
    generated.Append('\n');
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach ((string macro, (string code, string? condition, List<int> versions)) in definitions.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
    {
        string name = GetName(macro, condition);
        if (!names.Add(name))
        {
            throw new InvalidOperationException($"Duplicate generated C# name: {name}.");
        }

        string description = condition ?? macro[8..].ToLowerInvariant();
        generated.Append("    /// <summary>\n    /// The PostgreSQL <c>").Append(description).Append("</c> SQLSTATE (<c>").Append(code)
            .Append("</c>).\n    /// </summary>\n    /// <remarks>\n    /// PostgreSQL macro <c>").Append(macro).Append("</c>.");
        if (versions.Count != sources.Length)
        {
            generated.Append(" Present in the PostgreSQL ")
                .Append(string.Join(", ", versions.Select(static version => version == 19 ? "19 RC1" : version.ToString(CultureInfo.InvariantCulture))))
                .Append(" source catalogs.");
        }

        generated.Append("\n    /// </remarks>\n    public const string ").Append(name).Append(" = \"").Append(code).Append("\";\n\n");
    }

    generated.Length--;
    generated.Append("}\n");
    string output = generated.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    const string OutputPath = "src/Ankus.Runtime/PgSqlStates.cs";
    if (args.Length == 2)
    {
        if (!File.Exists(OutputPath) || File.ReadAllText(OutputPath).Replace("\r\n", "\n", StringComparison.Ordinal) != output)
        {
            throw new InvalidOperationException("PgSqlStates.cs does not match the pinned PostgreSQL catalogs. Regenerate it.");
        }
    }
    else
    {
        File.WriteAllText(OutputPath, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    Console.WriteLine($"SQLSTATE catalog: {definitions.Count} names, {definitions.Values.Select(static value => value.Code).Distinct(StringComparer.Ordinal).Count()} values.");
    return 0;
}
catch (Exception error) when (error is InvalidOperationException or IOException)
{
    // An invalid catalog, an unreadable checkout or a stale generated file is reported as one line.
    Console.Error.WriteLine(error.Message);
    return 1;
}

// Reads one pinned catalog without changing the reference checkout or fetching network data.
static async Task<string> ReadCatalog(string checkout, string tag)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    string[] arguments = ["-C", Path.GetFullPath(checkout), "show", tag + ":src/backend/utils/errcodes.txt"];
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> errors = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    string result = await output;
    string error = await errors;
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Cannot read {tag}: {error.Trim()}");
    }

    return result;
}

// Uses PostgreSQL condition names while disambiguating warning and routine classes and preserving native aliases.
static string GetName(string macro, string? condition)
{
    string raw = condition ?? macro[8..].ToLowerInvariant();
    string prefix = macro.StartsWith("ERRCODE_WARNING_", StringComparison.Ordinal) ? "Warning" :
        macro.StartsWith("ERRCODE_S_R_E_", StringComparison.Ordinal) ? "SqlRoutine" :
        macro.StartsWith("ERRCODE_E_R_E_", StringComparison.Ordinal) ? "ExternalRoutine" :
        macro.StartsWith("ERRCODE_E_R_I_E_", StringComparison.Ordinal) ? "ExternalRoutineInvocation" : string.Empty;
    return prefix + string.Concat(raw.Split('_').Select(static word => word switch
    {
        "sqlclient" => "SqlClient",
        "sqlserver" => "SqlServer",
        "sqlconnection" => "SqlConnection",
        "sqlstate" => "SqlState",
        "pstatement" => "PreparedStatement",
        "xquery" => "XQuery",
        _ => char.ToUpperInvariant(word[0]) + word[1..],
    }));
}
