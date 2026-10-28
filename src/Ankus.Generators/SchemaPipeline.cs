using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Resolves schema declarations into immutable contracts before rendering and merging graph nodes.
/// </summary>
internal static class SchemaPipeline
{
    /// <summary>
    /// Registers independently cached schema analysis and SQL emission.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>Detached declaration identities, policies, diagnostics and emitted SQL.</returns>
    internal static IncrementalValueProvider<EquatableArray<SchemaOutput>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<SchemaAnalysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgSchemaAttribute", static (node, _) => node is TypeDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token)).WithTrackingName("SchemaAnalysis");
        IncrementalValuesProvider<SchemaDeclaration?> models = analysis.Select(static (value, _) => value.Declaration)
            .WithTrackingName("SchemaModel");
        IncrementalValuesProvider<string> emission = models.Select(static (value, _) => value is { Create: true }
            ? "CREATE SCHEMA IF NOT EXISTS " + SqlText.Identifier(value.Name) + ";\n" : string.Empty)
            .WithTrackingName("SchemaEmission");
        return analysis.Collect().Combine(emission.Collect()).Select(static (value, _) =>
            new EquatableArray<SchemaOutput>(value.Left.Select((item, index) => new SchemaOutput(item, value.Right[index]))));
    }

    /// <summary>
    /// Reads only the semantic values and current coordinates needed by a schema declaration.
    /// </summary>
    private static SchemaAnalysis Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = (INamedTypeSymbol)context.TargetSymbol;
        AttributeData attribute = context.Attributes[0];
        string? name = attribute.ConstructorArguments.FirstOrDefault().Value as string;
        bool create = AttributeValues.Get(attribute, "Create", true);
        // PostgreSQL's IsReservedName compares the exact catalog name; the quoted "PG_x" or "Pg_x" is not reserved.
        SchemaDeclaration? declaration = SqlText.IsIdentifier(name) && (!create || !name!.StartsWith("pg_", StringComparison.Ordinal))
            ? new(name!, create) : null;
        GeneratorProblem? problem = declaration is null
            ? new(SqlText.IsIdentifier(name) ? FunctionDeclarationDiagnostics.ReservedSchema : FunctionDeclarationDiagnostics.Schema,
                GeneratorLocation.Create(FunctionDeclarationDiagnostics.ConstructorArgument(attribute, cancellationToken), context.SemanticModel.Compilation),
                new([]))
            : null;
        return new(DeclarationIdentity.Create(type), type.ToDisplayString(), type.Name, declaration,
            SqlDeclarationOptions.Read(attribute, context.SemanticModel.Compilation, cancellationToken)!, GeneratorLocation.Create(type.Locations.FirstOrDefault(), context.SemanticModel.Compilation), problem);
    }

    /// <summary>
    /// Reports an invalid cached declaration on the current source tree.
    /// </summary>
    /// <param name="analysis">The detached semantic analysis.</param>
    /// <param name="compilation">The compilation owning the current source trees.</param>
    /// <param name="context">The diagnostic destination.</param>
    internal static void Report(SchemaAnalysis analysis, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
        => analysis.Problem!.Report(compilation, context);

    /// <summary>
    /// Contains only the schema values that affect its generated SQL.
    /// </summary>
    /// <param name="Name">The exact catalog identifier.</param>
    /// <param name="Create">Whether installation creates the schema.</param>
    internal sealed record SchemaDeclaration(string Name, bool Create);

    /// <summary>
    /// Separates schema rendering inputs from graph identity, policy and source attribution.
    /// </summary>
    /// <param name="Identity">The assembly-qualified declaration identity.</param>
    /// <param name="Display">The managed name used for deterministic ordering and provenance.</param>
    /// <param name="Name">The unqualified name used by diagnostics.</param>
    /// <param name="Declaration">The validated rendering contract, or null for an invalid schema.</param>
    /// <param name="Options">The immutable graph options.</param>
    /// <param name="Location">The detached current declaration coordinates.</param>
    /// <param name="Problem">The specific schema failure at its authored value, or null for a valid declaration.</param>
    internal sealed record SchemaAnalysis(DeclarationIdentity Identity, string Display, string Name, SchemaDeclaration? Declaration,
        SqlDeclarationOptions Options, GeneratorLocation? Location, GeneratorProblem? Problem);

    /// <summary>
    /// Supplies one schema declaration and its cached SQL to installation graph composition.
    /// </summary>
    /// <param name="Analysis">The semantic declaration and graph policy.</param>
    /// <param name="Sql">The independently rendered creation statement, or empty when not created.</param>
    internal sealed record SchemaOutput(SchemaAnalysis Analysis, string Sql);
}
