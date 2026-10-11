using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies one installation graph encoding bound exceeded by a complete extension.
/// </summary>
internal enum InstallationGraphLimit
{
    /// <summary>
    /// The graph contains more than 100,000 declarations.
    /// </summary>
    Declarations,

    /// <summary>
    /// One declaration has more than 100,000 names, dependencies or attachments.
    /// </summary>
    Entries,

    /// <summary>
    /// The encoded graph exceeds 32 MiB.
    /// </summary>
    Size,
}

/// <summary>
/// Reports independently correctable installation graph, SQL replacement and generated SQL identity contracts.
/// </summary>
internal static class SqlGraphDiagnostics
{
    private const string GraphHelp = "https://willibrandon.github.io/ankus/custom-sql/#dependency-graph-diagnostics";
    private const string ReplacementHelp = "https://willibrandon.github.io/ankus/custom-sql/#sql-replacement-diagnostics";
    private const string OperatorHelp = "https://willibrandon.github.io/ankus/operators-and-casts/#declaration-diagnostics";
    private const string GeneratedOperatorHelp = "https://willibrandon.github.io/ankus/operators-and-casts/#generated-type-operators";

    /// <summary>
    /// Requires a nonblank explicit dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_blankId = Create("ANKUS490", "SQL dependency identifier cannot be blank",
        "Give Id at least one non-whitespace character, or omit Id when no explicit dependency identifier is needed", GraphHelp);

    /// <summary>
    /// Rejects zero characters in an explicit dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_idZero = Create("ANKUS491", "SQL dependency identifier cannot contain zero characters",
        "Remove embedded zero characters from the dependency identifier", GraphHelp);

    /// <summary>
    /// Requires lossless Unicode in an explicit dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_idUnicode = Create("ANKUS492", "SQL dependency identifier requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the dependency identifier", GraphHelp);

    /// <summary>
    /// Rejects a null Requires or Before list.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_nullList = Create("ANKUS493", "SQL dependency list cannot be null",
        "Omit Requires or Before, or supply an array of dependency identifiers instead of null", GraphHelp);

    /// <summary>
    /// Requires each Requires or Before entry to be nonblank.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_blankReference = Create("ANKUS494", "SQL dependency reference cannot be blank",
        "Give each Requires and Before entry a nonnull dependency identifier containing at least one non-whitespace character", GraphHelp);

    /// <summary>
    /// Rejects zero characters in a Requires or Before entry.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_referenceZero = Create("ANKUS495", "SQL dependency reference cannot contain zero characters",
        "Remove embedded zero characters from the Requires or Before entry", GraphHelp);

    /// <summary>
    /// Requires lossless Unicode in a Requires or Before entry.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_referenceUnicode = Create("ANKUS496", "SQL dependency reference requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the Requires or Before entry", GraphHelp);

    /// <summary>
    /// Escapes an authored dependency identifier for a diagnostic message, so a quote, backslash or line break in it
    /// cannot end the quoted name or start a new line. Ordinary identifiers are unchanged.
    /// </summary>
    /// <param name="name">The authored identifier.</param>
    /// <returns>The identifier with C#-style escapes for backslash, single quote and control characters.</returns>
    internal static string Quote(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char value in name)
        {
            _ = value switch
            {
                '\\' => builder.Append("\\\\"),
                '\'' => builder.Append("\\'"),
                '\n' => builder.Append("\\n"),
                '\r' => builder.Append("\\r"),
                '\t' => builder.Append("\\t"),
                _ when char.IsControl(value) => builder.Append("\\u").Append(((int)value).ToString("x4", CultureInfo.InvariantCulture)),
                _ => builder.Append(value),
            };
        }

        return builder.ToString();
    }

    /// <summary>
    /// Requires each dependency identifier to name one SQL declaration.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateId = Create("ANKUS497", "Duplicate SQL dependency identifier",
        "Dependency identifier '{0}' is already declared in this extension; give each SQL declaration a distinct Id or PgSql name", GraphHelp);

    /// <summary>
    /// Requires each Requires or Before entry to name a declared dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_missingDependency = Create("ANKUS498", "SQL dependency identifier is not declared",
        "No SQL declaration in this extension has dependency identifier '{0}'; declare it with Id, PgSql or PgSqlFile, or correct this entry", GraphHelp);

    /// <summary>
    /// Requires an acyclic installation order.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_cycle = Create("ANKUS499", "Installation SQL dependency cycle",
        "Installation SQL dependencies form the cycle {0}; remove or reverse one explicit dependency in it", GraphHelp);

    /// <summary>
    /// Permits only one bootstrap SQL block.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_bootstrap = Create("ANKUS500", "Only one bootstrap SQL block is allowed",
        "Another PgSql or PgSqlFile block already uses PgSqlOrder.Bootstrap; give this block a different Order", GraphHelp);

    /// <summary>
    /// Permits only one final SQL block.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_finalize = Create("ANKUS501", "Only one final SQL block is allowed",
        "Another PgSql or PgSqlFile block already uses PgSqlOrder.Finalize; give this block a different Order", GraphHelp);

    /// <summary>
    /// Rejects replacement text on a declaration whose SQL is disabled.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_disabledReplacement = Create("ANKUS502", "Disabled SQL cannot have replacement text",
        "GenerateSql cannot be false when Sql supplies replacement text, including empty text; remove GenerateSql = false or Sql", ReplacementHelp);

    /// <summary>
    /// Rejects zero characters in replacement SQL.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_replacementZero = Create("ANKUS503", "SQL replacement cannot contain zero characters",
        "Remove embedded zero characters from the Sql replacement", ReplacementHelp);

    /// <summary>
    /// Requires lossless Unicode in replacement SQL.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_replacementUnicode = Create("ANKUS504", "SQL replacement requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the Sql replacement", ReplacementHelp);

    /// <summary>
    /// Requires binary callbacks for binary replacement tokens.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_binaryToken = Create("ANKUS505", "SQL replacement token requires the binary protocol",
        "The Sql replacement token {0} requires BinaryProtocol = true; enable the binary protocol or remove the token", ReplacementHelp);

    /// <summary>
    /// Requires distinct generated enum type identities.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateEnumType = Create("ANKUS506", "Duplicate PostgreSQL enum type name",
        "PostgreSQL type {0} is already generated in this extension; give this enum a distinct PgEnum Name or Schema",
        "https://willibrandon.github.io/ankus/enums/#declaration-diagnostics");

    /// <summary>
    /// Requires distinct generated base type identities.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateBaseType = Create("ANKUS507", "Duplicate PostgreSQL base type name",
        "PostgreSQL type {0} is already generated in this extension; give this type a distinct PgType Name or Schema",
        "https://willibrandon.github.io/ankus/custom-types/#declaration-diagnostics");

    /// <summary>
    /// Requires distinct operator name and operand signatures.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateOperator = Create("ANKUS508", "Duplicate PostgreSQL operator signature",
        "PostgreSQL operator {0} is already declared in this extension; change this operator's name, schema or operand types", OperatorHelp);

    /// <summary>
    /// Requires distinct cast source and target pairs.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateCast = Create("ANKUS509", "Duplicate PostgreSQL cast signature",
        "PostgreSQL cast {0} is already declared in this extension; keep one cast for each source and target type", OperatorHelp);

    /// <summary>
    /// Requires generated operator support functions to have unclaimed signatures.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateGeneratedFunction = Create("ANKUS510", "Generated operator function signature is already declared",
        "Function {0} generated for '{1}' is already declared in this extension; remove the conflicting declaration or this generated-operator attribute",
        GeneratedOperatorHelp);

    /// <summary>
    /// Requires generated comparison operators to have unclaimed signatures.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateGeneratedOperator = Create("ANKUS511", "Generated operator signature is already declared",
        "Operator {0} generated for '{1}' is already declared in this extension; remove the conflicting operator or this generated-operator attribute",
        GeneratedOperatorHelp);

    /// <summary>
    /// Bounds the number of embedded installation declarations.
    /// </summary>
    private static readonly DiagnosticDescriptor s_declarationLimit = Create("ANKUS512", "Installation graph has too many declarations",
        "The embedded installation graph cannot exceed 100,000 SQL declarations; split the extension", GraphHelp);

    /// <summary>
    /// Bounds each declaration's embedded name, dependency and attachment lists.
    /// </summary>
    private static readonly DiagnosticDescriptor s_entryLimit = Create("ANKUS513", "Installation declaration has too many graph entries",
        "An embedded installation declaration cannot have more than 100,000 names, dependencies or attachments; split the declaration", GraphHelp);

    /// <summary>
    /// Bounds the complete embedded installation graph.
    /// </summary>
    private static readonly DiagnosticDescriptor s_sizeLimit = Create("ANKUS514", "Installation graph exceeds 32 MiB",
        "The embedded installation graph cannot exceed 32 MiB; reduce or split its installation SQL", GraphHelp);

    /// <summary>
    /// Selects the fixed descriptor for an exceeded installation graph bound.
    /// </summary>
    /// <param name="limit">The exceeded encoding bound.</param>
    /// <returns>The corresponding location-free extension diagnostic.</returns>
    internal static DiagnosticDescriptor Descriptor(InstallationGraphLimit limit) => limit switch
    {
        InstallationGraphLimit.Declarations => s_declarationLimit,
        InstallationGraphLimit.Entries => s_entryLimit,
        InstallationGraphLimit.Size => s_sizeLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(limit)),
    };

    private static DiagnosticDescriptor Create(string id, string title, string message, string help)
        => new(id, title, message, "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: help);
}
