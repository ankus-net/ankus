using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates per-enum semantic analysis, cached rendering and extension graph composition.
/// </summary>
internal static class EnumPipeline
{
    /// <summary>
    /// Registers independent immutable enum contracts and their managed, native and SQL emission.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The complete detached enum outputs in discovery order.</returns>
    internal static IncrementalValueProvider<EquatableArray<EnumOutput>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<EnumAnalysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgEnumAttribute", static (node, _) => node is BaseTypeDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token)).WithTrackingName("EnumAnalysis");
        IncrementalValuesProvider<EnumDeclaration?> models = analysis.Select(static (value, _) => value.Declaration)
            .WithTrackingName("EnumModel");
        IncrementalValuesProvider<EnumEmission> emission = models.Select(static (model, _) => Emit(model))
            .WithTrackingName("EnumEmission");
        return analysis.Collect().Combine(emission.Collect()).Select(static (value, _) =>
            Join(value.Left, value.Right)).WithTrackingName("EnumOutputs");
    }

    /// <summary>
    /// Resolves one attributed type into values and detached diagnostic coordinates.
    /// </summary>
    private static EnumAnalysis Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) => problems.Add(
            new(descriptor, GeneratorLocation.Create(location, context.SemanticModel.Compilation), new(arguments))), cancellationToken);
        AttributeMetadataFailure? metadata = null;
        EnumDeclaration? declaration = EnumDeclaration.Create(type, diagnostics, value => metadata = value, cancellationToken);
        return new(DeclarationIdentity.Create(type), type.ToDisplayString(), type.Name, declaration,
            SqlDeclarationOptions.Read(context.Attributes[0], context.SemanticModel.Compilation, cancellationToken)!, GeneratorLocation.Create(type.Locations.FirstOrDefault(), context.SemanticModel.Compilation), new(problems), metadata);
    }

    /// <summary>
    /// Renders only the values that affect an enum's generated contracts.
    /// </summary>
    private static EnumEmission Emit(EnumDeclaration? declaration)
    {
        if (declaration is null)
        {
            return new(string.Empty, string.Empty, string.Empty);
        }

        var registration = new StringBuilder();
        var native = new StringBuilder();
        declaration.EmitRegistration(registration);
        declaration.EmitNativeTypeCheck(native);
        return new(declaration.CreateSql(), registration.ToString(), native.ToString());
    }

    /// <summary>
    /// Joins projections of the same declaration stream without retaining mutable compiler state.
    /// </summary>
    private static EquatableArray<EnumOutput> Join(ImmutableArray<EnumAnalysis> analysis, ImmutableArray<EnumEmission> emission)
        => new(analysis.Select((value, index) => new EnumOutput(value, emission[index])));

    /// <summary>
    /// Reports a cached validation failure against its current declaration location.
    /// </summary>
    /// <param name="analysis">The detached enum analysis.</param>
    /// <param name="compilation">The current compilation owning diagnostic source trees.</param>
    /// <param name="context">The diagnostic destination.</param>
    internal static void Report(EnumAnalysis analysis, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        if (analysis.Metadata is not null)
        {
            analysis.Metadata.Report(analysis.Location?.Resolve(compilation), context);
        }
        else
        {
            foreach (GeneratorProblem problem in analysis.Problems)
            {
                problem.Report(compilation, context);
            }
        }
    }

    /// <summary>
    /// Keeps type contracts and graph options separate from rendered text.
    /// </summary>
    /// <param name="Identity">The assembly-qualified semantic declaration identity.</param>
    /// <param name="Display">The managed name for sorting, selection and provenance.</param>
    /// <param name="Name">The unqualified managed name for diagnostics.</param>
    /// <param name="Declaration">The validated type contract, or null on failure.</param>
    /// <param name="Options">The authored SQL graph and replacement policy.</param>
    /// <param name="Location">Detached source coordinates.</param>
    /// <param name="Problems">The detached precise contract failures.</param>
    /// <param name="Metadata">The exact attribute decoding failure, if any.</param>
    internal sealed record EnumAnalysis(DeclarationIdentity Identity, string Display, string Name, EnumDeclaration? Declaration,
        SqlDeclarationOptions Options, GeneratorLocation? Location, EquatableArray<GeneratorProblem> Problems, AttributeMetadataFailure? Metadata = null);

    /// <summary>
    /// Contains the independently cached source fragments for one enum contract.
    /// </summary>
    /// <param name="Sql">The generated CREATE TYPE statement.</param>
    /// <param name="Registration">The closed managed label registration.</param>
    /// <param name="Native">The native type identity check.</param>
    internal sealed record EnumEmission(string Sql, string Registration, string Native);

    /// <summary>
    /// Supplies one enum's semantic data and cached fragments to extension composition.
    /// </summary>
    /// <param name="Analysis">The detached semantic model.</param>
    /// <param name="Emission">The corresponding rendered contracts.</param>
    internal sealed record EnumOutput(EnumAnalysis Analysis, EnumEmission Emission);
}
