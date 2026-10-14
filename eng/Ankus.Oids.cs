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
        throw new InvalidOperationException("Run from the repository root: dotnet run --file eng/Ankus.Oids.cs -- <pgrx-checkout> [--check]");
    }

    const string Revision = "fc91c63ebad11784647b50ee7e265c1fd9c9924f";
    var definitions = new Dictionary<string, (uint Value, List<int> Versions)>(StringComparer.Ordinal);
    for (int major = 13; major <= 19; major++)
    {
        string source = await ReadCatalog(args[0], Revision, major);
        var values = new HashSet<uint>();
        bool inEnum = false;
        foreach (string line in source.Split('\n').Select(static line => line.Trim()))
        {
            if (line == "pub enum BuiltinOid {")
            {
                inEnum = true;
                continue;
            }

            if (!inEnum)
            {
                continue;
            }

            if (line == "}")
            {
                break;
            }

            string[] fields = line.TrimEnd(',').Split(" = ", StringSplitOptions.None);
            if (fields.Length != 2 || fields[0].Length == 0 || fields[0].Any(static character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
                !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint value) || value == 0 || !values.Add(value))
            {
                throw new InvalidOperationException($"Invalid or duplicate PG{major} OID: {line}");
            }

            string name = fields[0];
            if (definitions.TryGetValue(name, out (uint Value, List<int> Versions) previous))
            {
                if (previous.Value != value || previous.Versions.Contains(major))
                {
                    throw new InvalidOperationException($"Conflicting native symbol: {name}");
                }

                previous.Versions.Add(major);
            }
            else
            {
                definitions.Add(name, (value, [major]));
            }
        }

        if (values.Count == 0)
        {
            throw new InvalidOperationException($"No built-in OID definitions for PG{major}.");
        }
    }

    var enumeration = new StringBuilder("""
        // Regenerate from the pinned pgrx catalogs with eng/Ankus.Oids.cs.
        namespace Ankus;

        /// <summary>
        /// Names numeric constants from pgrx's PostgreSQL 13–19 built-in OID catalogs.
        /// </summary>
        /// <remarks>
        /// Members retain exact unsigned values. Renamed symbols share one member, named after the newest source spelling.
        /// Use PgBuiltInOids for version-aware conversion and native names. A member does not imply availability in every
        /// server version, catalog existence, or object category. The source naming heuristic also includes non-object constants.
        /// Zero is not a defined member. Ordinary PostgreSQL oid datums continue to use uint, independently of this catalog.
        /// </remarks>
        public enum PgBuiltInOid : uint
        {
        """);
    enumeration.Append('\n');
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (IGrouping<uint, KeyValuePair<string, (uint Value, List<int> Versions)>> group in definitions.GroupBy(static entry => entry.Value.Value).OrderBy(static group => group.Key))
    {
        KeyValuePair<string, (uint Value, List<int> Versions)> canonical = group.MaxBy(static entry => entry.Value.Versions.Max());
        string name = GetName(canonical.Key);
        if (!names.Add(name))
        {
            throw new InvalidOperationException($"Duplicate managed name: {name}");
        }

        enumeration.Append("    /// <summary>\n    /// The <c>").Append(canonical.Key).Append("</c> constant (<c>")
            .Append(group.Key.ToString(CultureInfo.InvariantCulture)).Append("</c>).\n    /// </summary>\n    /// <remarks>\n");
        foreach (KeyValuePair<string, (uint Value, List<int> Versions)> alias in group.OrderBy(static entry => entry.Value.Versions.Min()))
        {
            enumeration.Append("    /// <c>").Append(alias.Key).Append("</c> in PostgreSQL ")
                .Append(string.Join(", ", alias.Value.Versions)).Append(".\n");
        }

        enumeration.Append("    /// </remarks>\n    ").Append(name).Append(" = ")
            .Append(group.Key.ToString(CultureInfo.InvariantCulture)).Append(",\n\n");
    }

    enumeration.Length--;
    enumeration.Append("}\n");

    var lookup = new StringBuilder("""
        // Regenerate from the pinned pgrx catalogs with eng/Ankus.Oids.cs.
        namespace Ankus;

        public static partial class PgBuiltInOids
        {
            private static string? LookupName(uint value, int postgresMajor) => value switch
            {
        """);
    lookup.Append('\n');
    foreach ((string nativeName, (uint value, List<int> versions)) in definitions.OrderBy(static entry => entry.Value.Value).ThenBy(static entry => entry.Key, StringComparer.Ordinal))
    {
        lookup.Append("        ").Append(value.ToString(CultureInfo.InvariantCulture));
        if (versions.Count != 7)
        {
            lookup.Append(" when postgresMajor is ").Append(string.Join(" or ", versions));
        }

        lookup.Append(" => \"").Append(nativeName).Append("\",\n");
    }

    lookup.Append("        _ => null,\n    };\n}\n");
    Write("src/Ankus.Runtime/PgBuiltInOid.cs", enumeration.ToString(), args.Length == 2);
    Write("src/Ankus.Runtime/PgBuiltInOids.Catalog.cs", lookup.ToString(), args.Length == 2);
    Console.WriteLine($"OID catalog: {definitions.Count} native names, {names.Count} numeric members, PostgreSQL 13–19.");
    return 0;
}
catch (Exception error) when (error is InvalidOperationException or IOException)
{
    // An invalid catalog, an unreadable checkout or a stale generated file is reported as one line.
    Console.Error.WriteLine(error.Message);
    return 1;
}

// Reads the committed source without modifying the reference checkout or fetching data.
static async Task<string> ReadCatalog(string checkout, string revision, int major)
{
    var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    string[] arguments = ["-C", Path.GetFullPath(checkout), "show", $"{revision}:pgrx-pg-sys/src/include/pg{major}_oids.rs"];
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
        throw new InvalidOperationException($"Cannot read PG{major}: {error.Trim()}");
    }

    return result;
}

// Formats native words consistently without changing their values or classification.
static string GetName(string nativeName)
{
    if (nativeName.EndsWith("RelationId", StringComparison.Ordinal) || nativeName == "TemplateDbOid")
    {
        return nativeName;
    }

    return string.Concat(nativeName.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(Word));
}

// Preserves familiar PostgreSQL abbreviations and separates type suffixes.
static string Word(string word)
{
    if (word == "VOID")
    {
        return "Void";
    }

    if (word == "MULTIRANGE")
    {
        return "Multirange";
    }

    if (word.Length > 3 && word.EndsWith("OID", StringComparison.Ordinal))
    {
        return Word(word[..^3]) + "Oid";
    }

    if (word.Length > 5 && word.EndsWith("ARRAY", StringComparison.Ordinal))
    {
        return Word(word[..^5]) + "Array";
    }

    if (word.Length > 10 && word.EndsWith("MULTIRANGE", StringComparison.Ordinal))
    {
        return Word(word[..^10]) + "Multirange";
    }

    if (word.Length > 5 && word.EndsWith("RANGE", StringComparison.Ordinal))
    {
        return Word(word[..^5]) + "Range";
    }

    return word switch
    {
        "F" => "Function",
        "ANYELEMENT" => "AnyElement",
        "ANYNONARRAY" => "AnyNonArray",
        "ANYENUM" => "AnyEnum",
        "ANYCOMPATIBLE" => "AnyCompatible",
        "ANYCOMPATIBLENON" => "AnyCompatibleNon",
        "ANYNON" => "AnyNon",
        "REGCLASS" => "RegClass",
        "REGCOLLATION" => "RegCollation",
        "REGCONFIG" => "RegConfig",
        "REGDATABASE" => "RegDatabase",
        "REGDICTIONARY" => "RegDictionary",
        "REGNAMESPACE" => "RegNamespace",
        "REGOPER" => "RegOper",
        "REGOPERATOR" => "RegOperator",
        "REGPROC" => "RegProc",
        "REGPROCEDURE" => "RegProcedure",
        "REGROLE" => "RegRole",
        "REGTYPE" => "RegType",
        "DEFAULTTABLESPACE" => "DefaultTablespace",
        "GLOBALTABLESPACE" => "GlobalTablespace",
        "REFCURSOR" => "RefCursor",
        "TSVECTOR" => "TsVector",
        "GTSVECTOR" => "GtsVector",
        "TSQUERY" => "TsQuery",
        "BTREE" => "BTree",
        "SPGIST" => "SpGist",
        "BPCHAR" => "BpChar",
        "INT2VECTOR" => "Int2Vector",
        "OIDVECTOR" => "OidVector",
        "CSTRING" => "CString",
        "ACLITEM" => "AclItem",
        "JSONPATH" => "JsonPath",
        "NDISTINCT" => "NDistinct",
        "AUTHID" => "AuthId",
        "MINMAX" => "MinMax",
        _ => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant(),
    };
}

// Checks generated content without rewriting it, or writes deterministic LF-only source.
static void Write(string path, string text, bool check)
{
    string output = text.Replace("\r\n", "\n", StringComparison.Ordinal);
    if (check)
    {
        if (!File.Exists(path) || File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) != output)
        {
            throw new InvalidOperationException($"{path} does not match the pinned pgrx catalogs. Regenerate it.");
        }
    }
    else
    {
        File.WriteAllText(path, output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
