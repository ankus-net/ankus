using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Resolves assembly SQL declarations from compile-time constants and tracked compiler inputs.
/// </summary>
internal static class CustomSql
{
    /// <summary>
    /// Adds resolved blocks using current graph options and diagnostic source trees.
    /// </summary>
    /// <param name="outputs">The cached SQL blocks and current detached declaration metadata.</param>
    /// <param name="compilation">The current compilation used to resolve diagnostic coordinates.</param>
    /// <param name="graph">The installation dependency graph.</param>
    /// <param name="blocks">The successfully read SQL blocks, indexed by dependency identifier.</param>
    /// <returns>Whether every custom block explicitly permits relocation.</returns>
    internal static bool Add(EquatableArray<CustomSqlPipeline.Output> outputs, GeneratorSourceResolver compilation, SqlGraph graph,
        out Dictionary<string, SqlEntity> blocks)
    {
        blocks = new(StringComparer.Ordinal);
        bool relocatable = true;
        foreach (CustomSqlPipeline.Output output in outputs)
        {
            CustomSqlPipeline.Analysis analysis = output.Analysis;
            CustomSqlPipeline.Resolution resolution = output.Resolution;
            Location? location = analysis.Location?.Resolve(compilation);
            if (resolution.Error is not null)
            {
                graph.Error(ErrorLocation(analysis, resolution.Error.Value)?.Resolve(compilation) ?? location, CustomSqlDiagnostics.Descriptor(resolution.Error.Value));
                continue;
            }

            if (analysis.Order is < 0 or > 2)
            {
                graph.Error(analysis.OrderLocation?.Resolve(compilation) ?? location, CustomSqlDiagnostics.Descriptor(CustomSqlDiagnosticKind.Order));
                continue;
            }

            var entity = new SqlEntity("2:sql:" + resolution.Name, resolution.Sql!, location)
            {
                Order = analysis.Order,
                OrderLocation = analysis.OrderLocation?.Resolve(compilation),
                SourceFile = analysis.Input.File ? analysis.Input.Content : null,
            };
            graph.ConfigureOptions(entity, analysis.Options, resolution.Name, analysis.NameLocation?.Resolve(compilation));
            graph.Add(entity);
            if (!blocks.ContainsKey(resolution.Name!))
            {
                blocks.Add(resolution.Name!, entity);
            }

            relocatable &= analysis.Relocatable;
        }

        return relocatable;
    }

    /// <summary>
    /// Selects exact tracked content without reading the filesystem or retaining compiler objects.
    /// </summary>
    /// <param name="input">The detached authored name, content and declaration kind.</param>
    /// <param name="files">The compiler's tracked file paths and content.</param>
    /// <param name="projectDirectory">The compiler-visible project directory.</param>
    /// <param name="trackedPaths">All compiler-tracked paths, retaining ambiguity even when only selected contents were read.</param>
    /// <returns>The selected content or an owned diagnostic message.</returns>
    internal static CustomSqlPipeline.Selection Select(CustomSqlPipeline.Input input,
        EquatableArray<CustomSqlPipeline.FileInput> files, string projectDirectory, IEnumerable<string>? trackedPaths = null)
    {
        if (!input.ValidArguments)
        {
            return new(input.Name, null, CustomSqlDiagnosticKind.Arguments);
        }

        // A name taken from the path is reported only after the path itself is valid.
        CustomSqlPipeline.FileSelection? selection = input.DerivedName
            ? SelectFilePath(input.Content, trackedPaths ?? files.Select(static file => file.Path), projectDirectory)
            : null;
        if (selection?.Error is { } pathError)
        {
            return new(input.Name, null, pathError);
        }

        if (TextError(input.Name, CustomSqlDiagnosticKind.EmptyName, CustomSqlDiagnosticKind.NameZero, CustomSqlDiagnosticKind.NameUnicode) is { } nameError)
        {
            return new(input.Name, null, nameError);
        }

        if (!input.File)
        {
            return new(input.Name, input.Content, null);
        }

        selection ??= SelectFilePath(input.Content, trackedPaths ?? files.Select(static file => file.Path), projectDirectory);
        if (selection.Error is not null)
        {
            return new(input.Name, null, selection.Error);
        }

        CustomSqlPipeline.FileInput selected = files.Single(file => file.Path == selection.Path);
        return selected.Text is null ? new(input.Name, null, CustomSqlDiagnosticKind.FileUnreadable) : new(input.Name, selected.Text, null);
    }

    /// <summary>
    /// Resolves a unique tracked path before reading contents, retaining exact-match precedence and relative suffix fallback.
    /// </summary>
    /// <param name="path">The authored SQL file path.</param>
    /// <param name="files">The compiler's tracked paths without file contents.</param>
    /// <param name="projectDirectory">The compiler-visible project directory.</param>
    /// <returns>The unique original path or the existing path-selection diagnostic.</returns>
    internal static CustomSqlPipeline.FileSelection SelectFilePath(string? path, IEnumerable<string> files, string projectDirectory)
    {
        if (TextError(path, CustomSqlDiagnosticKind.EmptyPath, CustomSqlDiagnosticKind.PathZero, CustomSqlDiagnosticKind.PathUnicode) is { } pathError)
        {
            return new(null, pathError);
        }

        if (!Path.IsPathRooted(path!) && !Path.IsPathRooted(projectDirectory))
        {
            return new(null, CustomSqlDiagnosticKind.ProjectDirectory);
        }

        string? fullPath = Normalize(path, projectDirectory);
        if (fullPath is null)
        {
            return new(null, CustomSqlDiagnosticKind.PathInvalid);
        }

        StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string[] matches = [.. files.Where(file => string.Equals(Normalize(file, projectDirectory), fullPath, comparison))];
        string? relativePath = NormalizeRelative(path, projectDirectory);
        if (matches.Length == 0 && relativePath is not null)
        {
            string suffix = Path.DirectorySeparatorChar + relativePath;
            matches =
            [
                .. files.Where(file => Normalize(file, projectDirectory) is string candidate
                    && candidate.EndsWith(suffix, comparison)),
            ];
        }

        return matches.Length == 1 ? new(matches[0], null) : new(null,
            matches.Length == 0 ? CustomSqlDiagnosticKind.FileMissing : CustomSqlDiagnosticKind.FileAmbiguous);
    }

    /// <summary>
    /// Validates selected SQL text independently of graph options, source coordinates and other files.
    /// </summary>
    /// <param name="selection">The selected name, exact text and input diagnostic.</param>
    /// <returns>The validated block or an owned diagnostic message.</returns>
    internal static CustomSqlPipeline.Resolution Resolve(CustomSqlPipeline.Selection selection)
        => selection.Error is not null ? new(selection.Name, null, selection.Error) :
            TextError(selection.Sql, CustomSqlDiagnosticKind.NullSql, CustomSqlDiagnosticKind.SqlZero, CustomSqlDiagnosticKind.SqlUnicode, allowEmpty: true) is { } error
                ? new(selection.Name, null, error)
                : new(selection.Name, selection.Sql, null);

    /// <summary>
    /// Preserves the current validation precedence while retaining a precise text-boundary cause.
    /// </summary>
    /// <param name="value">The authored or tracked text.</param>
    /// <param name="empty">The empty-value contract.</param>
    /// <param name="zero">The zero-character contract.</param>
    /// <param name="unicode">The Unicode contract.</param>
    /// <param name="allowEmpty">Whether empty and whitespace SQL remains a valid dependency anchor.</param>
    /// <returns>The first failed contract, or none for exact valid text.</returns>
    private static CustomSqlDiagnosticKind? TextError(string? value, CustomSqlDiagnosticKind empty,
        CustomSqlDiagnosticKind zero, CustomSqlDiagnosticKind unicode, bool allowEmpty = false)
    {
        if (value is null || !allowEmpty && string.IsNullOrWhiteSpace(value))
        {
            return empty;
        }

        return value.Contains('\0') ? zero : !SqlText.IsText(value) ? unicode : null;
    }

    /// <summary>
    /// Attributes each cause to its authored argument while file-content errors identify the selected path.
    /// </summary>
    /// <param name="analysis">The current detached declaration coordinates.</param>
    /// <param name="kind">The independently cached validation cause.</param>
    /// <returns>The specific current source coordinate.</returns>
    private static GeneratorLocation? ErrorLocation(CustomSqlPipeline.Analysis analysis, CustomSqlDiagnosticKind kind)
        => kind switch
        {
            CustomSqlDiagnosticKind.Arguments => analysis.Location,
            CustomSqlDiagnosticKind.EmptyName or CustomSqlDiagnosticKind.NameZero or CustomSqlDiagnosticKind.NameUnicode => analysis.NameLocation,
            _ => analysis.ContentLocation,
        };

    private static string? NormalizeRelative(string? path, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return null;
        }

        if (!Path.IsPathRooted(projectDirectory))
        {
            return null;
        }

        string root = Path.GetPathRoot(projectDirectory)!;
        string anchor = Join(root, "__ankus_project__");
        string? normalized = Normalize(path, anchor);
        string prefix = anchor + Path.DirectorySeparatorChar;
        StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (normalized is null || !normalized.StartsWith(prefix, comparison))
        {
            return null;
        }

        return normalized.Substring(prefix.Length);
    }

    private static string? Normalize(string? path, string projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || !SqlText.IsText(path!))
        {
            return null;
        }

        try
        {
            path = path!.Replace('\\', '/');
            if (Path.DirectorySeparatorChar == '\\' && Path.IsPathRooted(path) && Path.GetPathRoot(path) is not { Length: >= 3 })
            {
                return null;
            }

            if (!Path.IsPathRooted(path))
            {
                if (!Path.IsPathRooted(projectDirectory))
                {
                    return null;
                }

                path = Join(projectDirectory, path);
            }

            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Joins a directory and a relative path. Callers pass only a relative second path, so this needs neither
    /// <see cref="Path.Combine(string, string)"/>, which would let a rooted one replace the directory, nor
    /// <c>Path.Join</c>, which .NET Standard 2.0 lacks.
    /// </summary>
    private static string Join(string directory, string path)
        => directory.Length > 0 && directory[directory.Length - 1] is '/' or '\\' ? directory + path : directory + Path.DirectorySeparatorChar + path;
}
