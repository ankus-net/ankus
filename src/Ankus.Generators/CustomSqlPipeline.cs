using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates assembly SQL constants, tracked file selection and validated text from current graph composition.
/// </summary>
internal static class CustomSqlPipeline
{
    /// <summary>
    /// Registers value-comparable inputs and per-block content resolution without retaining compiler objects.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <param name="projectDirectory">The independently evaluated project directory.</param>
    /// <returns>The resolved blocks with current graph options and diagnostic coordinates.</returns>
    internal static IncrementalValueProvider<EquatableArray<Output>> Register(IncrementalGeneratorInitializationContext context,
        IncrementalValueProvider<string> projectDirectory)
    {
        IncrementalValuesProvider<Analysis> analysis = context.CompilationProvider
            .Select(static (compilation, token) => Read(compilation, token))
            .SelectMany(static (values, _) => values).WithTrackingName("CustomSqlAnalysis");
        IncrementalValuesProvider<Input> inputs = analysis.Select(static (value, _) => value.Input)
            .WithTrackingName("CustomSqlInput");
        IncrementalValueProvider<ImmutableArray<string>> paths = context.AdditionalTextsProvider.Select(static (file, _) => file.Path).Collect();
        IncrementalValueProvider<EquatableArray<string>> selectedPaths = inputs.Collect()
            .Combine(paths).Combine(projectDirectory)
            .Select(static (value, _) => new EquatableArray<string>(value.Left.Left.Where(static input => input.File && input.ValidArguments)
                .Select(input => CustomSql.SelectFilePath(input.Content, value.Left.Right, value.Right).Path)
                .Where(static path => path is not null).Select(static path => path!)
                .Distinct(StringComparer.Ordinal).OrderBy(static path => path, StringComparer.Ordinal)))
            .WithTrackingName("CustomSqlSelectedPaths");
        IncrementalValueProvider<EquatableArray<FileInput>> fileInputs = context.AdditionalTextsProvider.Combine(selectedPaths)
            .Where(static value => value.Right.Contains(value.Left.Path, StringComparer.Ordinal))
            .Select(static (value, _) => value.Left)
            .Select(static (file, token) => new FileInput(file.Path, file.GetText(token)?.ToString()))
            .Collect().Select(static (values, _) => new EquatableArray<FileInput>(values));
        IncrementalValuesProvider<Selection> selection = inputs.Combine(fileInputs).Combine(projectDirectory).Combine(paths)
            .Select(static (value, _) => CustomSql.Select(value.Left.Left.Left, value.Left.Left.Right, value.Left.Right, value.Right))
            .WithTrackingName("CustomSqlSelection");
        IncrementalValuesProvider<Resolution> resolution = selection.Select(static (value, _) => CustomSql.Resolve(value))
            .WithTrackingName("CustomSqlResolution");
        return analysis.Collect().Combine(resolution.Collect()).Select(static (value, _) =>
            new EquatableArray<Output>(value.Left.Select((item, index) => new Output(item, value.Right[index]))));
    }

    /// <summary>
    /// Reads authored assembly constants and detaches diagnostic coordinates from the current compilation.
    /// </summary>
    private static EquatableArray<Analysis> Read(Compilation compilation, CancellationToken cancellationToken)
        => new(compilation.Assembly.GetAttributes().Where(static attribute => attribute.AttributeClass?.ToDisplayString() is
            "Ankus.PgSqlAttribute" or "Ankus.PgSqlFileAttribute").Select(attribute => Read(attribute, compilation, cancellationToken)));

    /// <summary>
    /// Reads one block, naming a path-only file block after the file as pgrx's <c>extension_sql_file!</c> does.
    /// </summary>
    /// <param name="attribute">The transient authored attribute.</param>
    /// <param name="compilation">The current compilation owning the source.</param>
    /// <param name="cancellationToken">The current generator token.</param>
    /// <returns>The detached block.</returns>
    private static Analysis Read(AttributeData attribute, Compilation compilation, CancellationToken cancellationToken)
    {
        bool file = attribute.AttributeClass?.Name == "PgSqlFileAttribute";
        bool derived = file && attribute.ConstructorArguments.Length == 1;
        int content = derived ? 0 : 1;
        string? text = attribute.ConstructorArguments.ElementAtOrDefault(content).Value as string;
        return new(new(attribute.ConstructorArguments.Length == 2 || derived,
                derived ? FileStem(text) : attribute.ConstructorArguments.ElementAtOrDefault(0).Value as string, text, file, derived),
            AttributeValues.Get(attribute, "Order", 0), AttributeValues.Get(attribute, "Relocatable", false),
            SqlDeclarationOptions.Read(attribute, compilation, cancellationToken)!,
            GeneratorLocation.Create(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation(), compilation),
            ArgumentLocation(attribute, 0, compilation, cancellationToken), ArgumentLocation(attribute, content, compilation, cancellationToken),
            GeneratorLocation.Create(FunctionDeclarationDiagnostics.Option(attribute, "Order", cancellationToken), compilation));
    }

    /// <summary>
    /// Returns the last path segment without its final extension, treating both separators alike so the name does not
    /// depend on the build host. This matches <c>PgSqlFileAttribute.Name</c>.
    /// </summary>
    /// <param name="path">The authored path.</param>
    /// <returns>The file stem, or null for a null path.</returns>
    internal static string? FileStem(string? path)
    {
        if (path is null)
        {
            return null;
        }

        string file = path.Substring(path.LastIndexOfAny(['/', '\\']) + 1);
        int extension = file.LastIndexOf('.');
        return extension > 0 ? file.Substring(0, extension) : file;
    }

    /// <summary>
    /// Locates a constructor value by its semantic parameter, preserving named and reordered arguments.
    /// </summary>
    /// <param name="attribute">The transient authored attribute.</param>
    /// <param name="position">The semantic constructor parameter index.</param>
    /// <param name="compilation">The current compilation owning the source.</param>
    /// <param name="cancellationToken">The current generator token.</param>
    /// <returns>The detached argument coordinates, or attribute coordinates for incomplete syntax.</returns>
    private static GeneratorLocation? ArgumentLocation(AttributeData attribute, int position, Compilation compilation, CancellationToken cancellationToken)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        string? parameter = attribute.AttributeConstructor?.Parameters.ElementAtOrDefault(position)?.Name;
        AttributeArgumentSyntax? argument = syntax?.ArgumentList?.Arguments.FirstOrDefault(item => parameter is not null
            && item.NameColon?.Name.Identifier.ValueText == parameter);
        if (argument is null && syntax?.ArgumentList?.Arguments.ElementAtOrDefault(position) is { NameColon: null, NameEquals: null } positional)
        {
            argument = positional;
        }

        return GeneratorLocation.Create(argument?.Expression.GetLocation() ?? syntax?.GetLocation(), compilation);
    }

    /// <summary>
    /// Contains only values needed to select and validate one SQL block's exact text.
    /// </summary>
    /// <param name="ValidArguments">Whether the constructor supplies the required arguments.</param>
    /// <param name="Name">The authored or file-derived dependency name, retaining invalid values.</param>
    /// <param name="Content">The authored inline SQL or additional-file path.</param>
    /// <param name="File">Whether content is a file path.</param>
    /// <param name="DerivedName">Whether the name comes from the file path, which is then validated first.</param>
    internal sealed record Input(bool ValidArguments, string? Name, string? Content, bool File, bool DerivedName = false);

    /// <summary>
    /// Retains graph-only settings and current diagnostic coordinates separately from content resolution.
    /// </summary>
    /// <param name="Input">The detached text resolution input.</param>
    /// <param name="Order">The authored installation ordering value.</param>
    /// <param name="Relocatable">Whether the block permits schema relocation.</param>
    /// <param name="Options">The detached dependency options.</param>
    /// <param name="Location">The current authored attribute coordinates.</param>
    /// <param name="NameLocation">The authored dependency-name argument.</param>
    /// <param name="ContentLocation">The authored inline text or tracked-file path argument.</param>
    /// <param name="OrderLocation">The explicitly authored ordering value.</param>
    internal sealed record Analysis(Input Input, int Order, bool Relocatable, SqlDeclarationOptions Options, GeneratorLocation? Location,
        GeneratorLocation? NameLocation, GeneratorLocation? ContentLocation, GeneratorLocation? OrderLocation);

    /// <summary>
    /// Owns compiler-tracked file content with ordered value equality.
    /// </summary>
    /// <param name="Path">The exact additional input path.</param>
    /// <param name="Text">The tracked contents, or null for an unreadable input.</param>
    internal sealed record FileInput(string Path, string? Text);

    /// <summary>
    /// Retains a unique tracked path without reading its file contents.
    /// </summary>
    /// <param name="Path">The original selected compiler path, or null when selection fails.</param>
    /// <param name="Error">The existing path-selection diagnostic.</param>
    internal sealed record FileSelection(string? Path, CustomSqlDiagnosticKind? Error);

    /// <summary>
    /// Contains only the selected SQL and diagnostics that affect text validation.
    /// </summary>
    /// <param name="Name">The authored block name.</param>
    /// <param name="Sql">The exact selected SQL text.</param>
    /// <param name="Error">An input selection error, or null on success.</param>
    internal sealed record Selection(string? Name, string? Sql, CustomSqlDiagnosticKind? Error);

    /// <summary>
    /// Owns a validated block's text or error independently of graph and compiler state.
    /// </summary>
    /// <param name="Name">The authored block name.</param>
    /// <param name="Sql">The validated exact SQL text, or null on failure.</param>
    /// <param name="Error">The validation error, or null on success.</param>
    internal sealed record Resolution(string? Name, string? Sql, CustomSqlDiagnosticKind? Error);

    /// <summary>
    /// Supplies cached text and current metadata for one installation graph node.
    /// </summary>
    /// <param name="Analysis">The current detached declaration metadata.</param>
    /// <param name="Resolution">The independently cached text resolution.</param>
    internal sealed record Output(Analysis Analysis, Resolution Resolution);
}
