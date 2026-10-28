using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates custom-type semantic validation, immutable storage rendering and current graph composition.
/// </summary>
internal static class CustomTypePipeline
{
    /// <summary>
    /// Registers independent per-type contract analysis and cached rendering.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The detached type contracts and their rendered fragments.</returns>
    internal static IncrementalValueProvider<EquatableArray<Output>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Analysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgTypeAttribute", static (node, _) => node is BaseTypeDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token)).WithTrackingName("CustomTypeAnalysis");
        IncrementalValuesProvider<CustomTypeModel?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName("CustomTypeModel");
        IncrementalValuesProvider<CustomTypeModel.RegistrationContract?> registrationModels = models.Select(static (value, _) =>
            value is null ? null : new CustomTypeModel.RegistrationContract(value.Name, value.Schema, value.Managed,
                value.IsValueType, value.NativeSize, value.NativePadding, value.Codec ?? "Codec_" + value.Symbol))
            .WithTrackingName("CustomTypeRegistrationModel");
        IncrementalValuesProvider<CustomTypeModel.SerializerContract?> serializerModels = models.Select(static (value, _) =>
            value is null ? null : new CustomTypeModel.SerializerContract(value.Managed, value.Symbol, value.NativeSize, value.NativePadding, value.TextCodec,
                value.Serializer))
            .WithTrackingName("CustomTypeSerializerModel");
        IncrementalValuesProvider<CustomTypeModel.CatalogContract?> catalogModels = models.Select(static (value, _) =>
            value is null ? null : new CustomTypeModel.CatalogContract(value.Name, value.Schema))
            .WithTrackingName("CustomTypeCatalogModel");
        IncrementalValuesProvider<CustomTypeIoModel?> ioModels = models.Select(static (value, _) => value?.Io)
            .WithTrackingName("CustomTypeIoModel");
        IncrementalValuesProvider<string> registration = registrationModels.Select(static (value, _) => RenderRegistration(value))
            .WithTrackingName("CustomTypeRegistrationEmission");
        IncrementalValuesProvider<string> serializer = serializerModels.Select(static (value, _) => RenderSerializer(value))
            .WithTrackingName("CustomTypeSerializerEmission");
        IncrementalValuesProvider<string> catalog = catalogModels.Select(static (value, _) => RenderCatalog(value))
            .WithTrackingName("CustomTypeCatalogEmission");
        IncrementalValuesProvider<PgTypeEmitter.Emission?> io = ioModels.Select(static (value, _) => value is null ? null : PgTypeEmitter.Create(value))
            .WithTrackingName("CustomTypeIoEmission");
        return analysis.Collect().Combine(registration.Collect()).Combine(serializer.Collect()).Combine(catalog.Collect()).Combine(io.Collect())
            .Select(static (value, _) => new EquatableArray<Output>(value.Left.Left.Left.Left.Select((item, index) => new Output(item,
                value.Right[index] is { } boundary ? new Emission(value.Left.Left.Left.Right[index], value.Left.Left.Right[index], value.Left.Right[index], boundary) : null))))
            .WithTrackingName("CustomTypeOutputs");
    }

    /// <summary>
    /// Validates one attributed type using compiler state only within this analysis invocation.
    /// </summary>
    private static Analysis Analyze(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = (INamedTypeSymbol)context.TargetSymbol;
        Compilation compilation = context.SemanticModel.Compilation;
        GeneratorLocation? typeLocation = GeneratorLocation.Create(type.Locations.FirstOrDefault(), compilation);
        GeneratorProblem? problem = null;
        AttributeMetadataFailure? metadata = null;
        CustomTypeDeclaration? declaration = CustomTypeDeclaration.Create(type, cancellationToken: cancellationToken,
            metadataFailure: value => metadata = value, diagnostic: (descriptor, location, arguments) =>
                problem = new(descriptor, GeneratorLocation.Create(location, compilation) ?? typeLocation, new(arguments)));
        return new(DeclarationIdentity.Create(type), type.ToDisplayString(), declaration?.Freeze(),
            SqlDeclarationOptions.Read(context.Attributes[0], compilation, cancellationToken)!,
            typeLocation, problem, metadata);
    }

    /// <summary>
    /// Renders only the validated registration constants.
    /// </summary>
    private static string RenderRegistration(CustomTypeModel.RegistrationContract? model)
    {
        var source = new StringBuilder();
        if (model is not null)
        {
            CustomTypeModel.EmitRegistration(model, source);
        }

        return source.ToString();
    }

    /// <summary>
    /// Renders only the validated graph and storage codec constants.
    /// </summary>
    private static string RenderSerializer(CustomTypeModel.SerializerContract? model)
    {
        var source = new StringBuilder();
        if (model is not null)
        {
            CustomTypeModel.EmitSerializer(model, source);
        }

        return source.ToString();
    }

    /// <summary>
    /// Renders only the validated catalog identity.
    /// </summary>
    private static string RenderCatalog(CustomTypeModel.CatalogContract? model)
    {
        var source = new StringBuilder();
        if (model is not null)
        {
            CustomTypeModel.EmitNativeTypeCheck(model, source);
        }

        return source.ToString();
    }

    /// <summary>
    /// Reports a detached validation failure against the current compilation's source tree.
    /// </summary>
    /// <param name="analysis">The cached semantic result and current coordinates.</param>
    /// <param name="compilation">The compilation owning the current source trees.</param>
    /// <param name="context">The diagnostic destination.</param>
    internal static void Report(Analysis analysis, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        if (analysis.Metadata is not null)
        {
            analysis.Metadata.Report(analysis.Location?.Resolve(compilation), context);
        }
        else if (analysis.Problem is not null)
        {
            analysis.Problem.Report(compilation, context);
        }
    }

    /// <summary>
    /// Holds storage contracts separately from graph options and declaration coordinates.
    /// </summary>
    /// <param name="Identity">The assembly-qualified semantic declaration identity.</param>
    /// <param name="Display">The managed graph, selection and sorting name.</param>
    /// <param name="Model">The validated storage model, or null after validation failed.</param>
    /// <param name="Options">The authored dependency and SQL replacement policy.</param>
    /// <param name="Location">The current detached declaration coordinates.</param>
    /// <param name="Problem">The optional precise validation failure.</param>
    /// <param name="Metadata">The exact attribute decoding failure, if any.</param>
    internal sealed record Analysis(DeclarationIdentity Identity, string Display, CustomTypeModel? Model,
        SqlDeclarationOptions Options, GeneratorLocation? Location, GeneratorProblem? Problem, AttributeMetadataFailure? Metadata = null);

    /// <summary>
    /// Carries the independently cached storage and I/O source fragments for one type.
    /// </summary>
    /// <param name="Registration">The closed managed registration.</param>
    /// <param name="Serializer">The generated owned storage codec, if any.</param>
    /// <param name="TypeCheck">The native catalog identity predicate.</param>
    /// <param name="Io">The managed, native and SQL I/O boundary fragments.</param>
    internal sealed record Emission(string Registration, string Serializer, string TypeCheck, PgTypeEmitter.Emission Io);

    /// <summary>
    /// Supplies one type's current metadata and cached rendering to extension composition.
    /// </summary>
    /// <param name="Analysis">The detached semantic validation result.</param>
    /// <param name="Emission">The rendered fragments, or null for an invalid type.</param>
    internal sealed record Output(Analysis Analysis, Emission? Emission);
}
