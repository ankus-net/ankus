using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies independently correctable custom SQL input contracts without retaining formatted error text.
/// </summary>
internal enum CustomSqlDiagnosticKind
{
    /// <summary>
    /// Custom SQL requires both constructor arguments.
    /// </summary>
    Arguments,

    /// <summary>
    /// Custom SQL requires a nonempty dependency name.
    /// </summary>
    EmptyName,

    /// <summary>
    /// Custom SQL dependency name cannot contain zero characters.
    /// </summary>
    NameZero,

    /// <summary>
    /// Custom SQL dependency name requires well-formed Unicode.
    /// </summary>
    NameUnicode,

    /// <summary>
    /// Custom SQL requires a defined installation order.
    /// </summary>
    Order,

    /// <summary>
    /// Custom SQL text cannot be null.
    /// </summary>
    NullSql,

    /// <summary>
    /// Custom SQL text cannot contain zero characters.
    /// </summary>
    SqlZero,

    /// <summary>
    /// Custom SQL text requires well-formed Unicode.
    /// </summary>
    SqlUnicode,

    /// <summary>
    /// Custom SQL file requires a nonempty path.
    /// </summary>
    EmptyPath,

    /// <summary>
    /// Custom SQL file path cannot contain zero characters.
    /// </summary>
    PathZero,

    /// <summary>
    /// Custom SQL file path requires well-formed Unicode.
    /// </summary>
    PathUnicode,

    /// <summary>
    /// Custom SQL file requires a valid filesystem path.
    /// </summary>
    PathInvalid,

    /// <summary>
    /// Relative custom SQL files require a project directory.
    /// </summary>
    ProjectDirectory,

    /// <summary>
    /// Custom SQL file is not a tracked compiler input.
    /// </summary>
    FileMissing,

    /// <summary>
    /// Custom SQL file matches multiple compiler inputs.
    /// </summary>
    FileAmbiguous,

    /// <summary>
    /// Tracked custom SQL file cannot be read.
    /// </summary>
    FileUnreadable,
}

/// <summary>
/// Maps detached custom SQL causes to fixed actionable diagnostic contracts.
/// </summary>
internal static class CustomSqlDiagnostics
{
    /// <summary>
    /// Custom SQL requires both constructor arguments.
    /// </summary>
    private static readonly DiagnosticDescriptor s_arguments = new(
        "ANKUS354", "Custom SQL requires both constructor arguments",
        "Supply a dependency name and SQL text for PgSql, or a file path, optionally after a dependency name, for PgSqlFile",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL requires a nonempty dependency name.
    /// </summary>
    private static readonly DiagnosticDescriptor s_emptyName = new(
        "ANKUS355", "Custom SQL requires a nonempty dependency name",
        "Supply a nonnull dependency name containing at least one non-whitespace character",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL dependency name cannot contain zero characters.
    /// </summary>
    private static readonly DiagnosticDescriptor s_nameZero = new(
        "ANKUS356", "Custom SQL dependency name cannot contain zero characters",
        "Remove embedded zero characters from the custom SQL dependency name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL dependency name requires well-formed Unicode.
    /// </summary>
    private static readonly DiagnosticDescriptor s_nameUnicode = new(
        "ANKUS357", "Custom SQL dependency name requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the custom SQL dependency name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL requires a defined installation order.
    /// </summary>
    private static readonly DiagnosticDescriptor s_order = new(
        "ANKUS358", "Custom SQL requires a defined installation order",
        "Choose a defined PgSqlOrder value instead of casting an undefined numeric value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL text cannot be null.
    /// </summary>
    private static readonly DiagnosticDescriptor s_nullSql = new(
        "ANKUS359", "Custom SQL text cannot be null",
        "Supply a nonnull SQL string; empty SQL is valid for dependency ordering",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL text cannot contain zero characters.
    /// </summary>
    private static readonly DiagnosticDescriptor s_sqlZero = new(
        "ANKUS360", "Custom SQL text cannot contain zero characters",
        "Remove embedded zero characters from the inline SQL or tracked SQL file",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL text requires well-formed Unicode.
    /// </summary>
    private static readonly DiagnosticDescriptor s_sqlUnicode = new(
        "ANKUS361", "Custom SQL text requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the inline SQL or tracked SQL file; Ankus preserves exact UTF-8 text without replacement",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL file requires a nonempty path.
    /// </summary>
    private static readonly DiagnosticDescriptor s_emptyPath = new(
        "ANKUS362", "Custom SQL file requires a nonempty path",
        "Supply a nonnull PgSqlFile path containing at least one non-whitespace character",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL file path cannot contain zero characters.
    /// </summary>
    private static readonly DiagnosticDescriptor s_pathZero = new(
        "ANKUS363", "Custom SQL file path cannot contain zero characters",
        "Remove embedded zero characters from the PgSqlFile path",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL file path requires well-formed Unicode.
    /// </summary>
    private static readonly DiagnosticDescriptor s_pathUnicode = new(
        "ANKUS364", "Custom SQL file path requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the PgSqlFile path",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL file requires a valid filesystem path.
    /// </summary>
    private static readonly DiagnosticDescriptor s_pathInvalid = new(
        "ANKUS365", "Custom SQL file requires a valid filesystem path",
        "Use a PgSqlFile path that can be normalized on the compilation platform",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Relative custom SQL files require a project directory.
    /// </summary>
    private static readonly DiagnosticDescriptor s_projectDirectory = new(
        "ANKUS366", "Relative custom SQL files require a project directory",
        "Use Ankus.Sdk or expose the absolute MSBuildProjectDirectory with CompilerVisibleProperty before selecting a relative PgSqlFile path",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL file is not a tracked compiler input.
    /// </summary>
    private static readonly DiagnosticDescriptor s_fileMissing = new(
        "ANKUS367", "Custom SQL file is not a tracked compiler input",
        "Include the requested SQL file with AdditionalFiles; no tracked path matches the PgSqlFile declaration",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Custom SQL file matches multiple compiler inputs.
    /// </summary>
    private static readonly DiagnosticDescriptor s_fileAmbiguous = new(
        "ANKUS368", "Custom SQL file matches multiple compiler inputs",
        "Remove duplicate or ambiguous AdditionalFiles paths so the PgSqlFile declaration identifies exactly one tracked input",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Tracked custom SQL file cannot be read.
    /// </summary>
    private static readonly DiagnosticDescriptor s_fileUnreadable = new(
        "ANKUS369", "Tracked custom SQL file cannot be read",
        "Make the selected AdditionalFiles input readable by the compiler; its tracked text is unavailable",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#custom-sql-input-diagnostics");

    /// <summary>
    /// Selects the exact input contract without classifying a formatted message.
    /// </summary>
    /// <param name="kind">The detached validation cause.</param>
    /// <returns>The fixed error descriptor.</returns>
    internal static DiagnosticDescriptor Descriptor(CustomSqlDiagnosticKind kind) => kind switch
    {
        CustomSqlDiagnosticKind.Arguments => s_arguments,
        CustomSqlDiagnosticKind.EmptyName => s_emptyName,
        CustomSqlDiagnosticKind.NameZero => s_nameZero,
        CustomSqlDiagnosticKind.NameUnicode => s_nameUnicode,
        CustomSqlDiagnosticKind.Order => s_order,
        CustomSqlDiagnosticKind.NullSql => s_nullSql,
        CustomSqlDiagnosticKind.SqlZero => s_sqlZero,
        CustomSqlDiagnosticKind.SqlUnicode => s_sqlUnicode,
        CustomSqlDiagnosticKind.EmptyPath => s_emptyPath,
        CustomSqlDiagnosticKind.PathZero => s_pathZero,
        CustomSqlDiagnosticKind.PathUnicode => s_pathUnicode,
        CustomSqlDiagnosticKind.PathInvalid => s_pathInvalid,
        CustomSqlDiagnosticKind.ProjectDirectory => s_projectDirectory,
        CustomSqlDiagnosticKind.FileMissing => s_fileMissing,
        CustomSqlDiagnosticKind.FileAmbiguous => s_fileAmbiguous,
        CustomSqlDiagnosticKind.FileUnreadable => s_fileUnreadable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
