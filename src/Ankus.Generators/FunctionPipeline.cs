using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Detaches ordinary and operator/cast backing-function validation and conversion contracts from compiler state.
/// </summary>
internal static class FunctionPipeline
{
    /// <summary>
    /// Registers per-function semantic analysis before extension-wide graph composition.
    /// </summary>
    /// <param name="context">The incremental generator registration context.</param>
    /// <returns>The validated models and independently located diagnostics.</returns>
    internal static IncrementalValueProvider<EquatableArray<FunctionOutput>> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<FunctionAnalysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgFunctionAttribute", static (node, _) => node is MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token))
            .Where(static value => value is not null).Select(static (value, _) => value!)
            .WithTrackingName("FunctionAnalysis");
        IncrementalValueProvider<EquatableArray<FunctionOutput>> functions = Render(analysis, "Function");
        IncrementalValuesProvider<FunctionAnalysis> operators = Aliases(context, "Ankus.PgOperatorAttribute");
        IncrementalValuesProvider<FunctionAnalysis> casts = Aliases(context, "Ankus.PgCastAttribute");
        IncrementalValuesProvider<FunctionAnalysis> aliases = operators.Collect().Combine(casts.Collect())
            .SelectMany(static (value, _) => value.Left.Concat(value.Right).Distinct())
            .WithTrackingName("AliasFunctionAnalysis");
        return functions.Combine(Render(aliases, "AliasFunction")).Select(static (value, _) =>
            new EquatableArray<FunctionOutput>(value.Left.Concat(value.Right)));
    }

    /// <summary>
    /// Discovers alias-only backing functions once while preserving ordinary-function and callback priorities.
    /// </summary>
    private static IncrementalValuesProvider<FunctionAnalysis> Aliases(IncrementalGeneratorInitializationContext context, string attributeName)
        => context.SyntaxProvider.ForAttributeWithMetadataName(attributeName, static (node, _) => node is MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax,
            static (attribute, token) => attribute.TargetSymbol.GetAttributes().Any(static value =>
                value.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute") ? null : Analyze(attribute, token))
            .Where(static value => value is not null).Select(static (value, _) => value!);

    /// <summary>
    /// Renders shared scalar and set contracts in independently tracked ordinary or alias-only pipelines.
    /// </summary>
    private static IncrementalValueProvider<EquatableArray<FunctionOutput>> Render(IncrementalValuesProvider<FunctionAnalysis> analysis, string prefix)
    {
        IncrementalValuesProvider<FunctionModel?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName(prefix + "Model");
        IncrementalValuesProvider<FunctionEmission?> emission = models.Select(static (value, _) => value is null ? null :
            value.Set is null ? PgFunctionEmitter.EmitBoundary(value.Invocation, [.. value.Parameters], value.Result!, value.Callback) :
                PgSetEmitter.EmitBoundary(value.Invocation, [.. value.Parameters], value.Set, value.Callback))
            .WithTrackingName(prefix + "Emission");
        IncrementalValuesProvider<FunctionDeclaration?> declarations = analysis.Select(static (value, _) => value.Declaration)
            .WithTrackingName(prefix + "Declaration");
        IncrementalValuesProvider<FunctionSqlModel?> sqlModels = analysis.Select(static (value, _) => value.Declaration is null ? null :
            FunctionSqlModel.Create(value.Declaration, value.Model!.Result, value.Model.Set, value.Model.Callback.Replace("ankus_managed_", "ankus_fn_")))
            .WithTrackingName(prefix + "SqlModel");
        IncrementalValuesProvider<FunctionSqlEmission?> sql = sqlModels.Select(static (value, _) => value is null ? null :
            FunctionSqlEmission.Create(value)).WithTrackingName(prefix + "SqlEmission");
        return analysis.Collect().Combine(emission.Collect()).Combine(declarations.Collect()).Combine(sql.Collect()).Select(static (value, _) =>
            new EquatableArray<FunctionOutput>(value.Left.Left.Left.Select((item, index) =>
                new FunctionOutput(item, value.Left.Left.Right[index], value.Left.Right[index], value.Right[index]))));
    }

    /// <summary>
    /// Validates one attributed backing function while compiler objects remain confined to analysis.
    /// </summary>
    /// <param name="attribute">The semantically resolved method attribute.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    /// <returns>The detached contract and diagnostics, or null for a specialized callback role.</returns>
    internal static FunctionAnalysis? Analyze(GeneratorAttributeSyntaxContext attribute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = (IMethodSymbol)attribute.TargetSymbol;
        if (method.GetAttributes().Any(static value => value.AttributeClass?.ToDisplayString() is
            "Ankus.PgTestAttribute" or "Ankus.PgTriggerAttribute" or "Ankus.PgEventTriggerAttribute" or
            "Ankus.PgInitializeAttribute" or "Ankus.PgModuleLoadAttribute" or "Ankus.PgBackgroundWorkerAttribute" or
            "Ankus.PgOutputPluginAttribute"))
        {
            return null;
        }

        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, attribute.SemanticModel.Compilation), new(arguments))), cancellationToken);
        if (!SynchronousDeclaration.Validate(method, attribute.SemanticModel.Compilation, diagnostics))
        {
            return new(DeclarationIdentity.Create(method), null, null, SqlDeclarationOptions.Read(method.GetAttributes().FirstOrDefault(static value =>
                    value.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute"), attribute.SemanticModel.Compilation, cancellationToken), new(problems),
                GeneratorLocation.Create(method.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
        }

        FunctionParameter[] parameters = FunctionParameter.Create(method);
        SetResult? set = SetResult.Create(method, diagnostics, out bool validSet);
        FunctionType? result = set is null ? FunctionType.CreateResult(method) : null;
        bool valid = validSet && SqlTypeReference.Validate(method, ref set, diagnostics);
        valid = valid && FunctionSignature.Validate(method, parameters, set, result, diagnostics);
        valid = valid && SqlNullability.Validate(method, method.Parameters.Where((_, index) => !parameters[index].IsInjected),
            set is null ? [method.ReturnType] : SetResult.OutputTypes(method), diagnostics);
        valid = valid && NumericConstraint.Validate(method, diagnostics, set);
        string name = PgFunctionGenerator.GetSqlName(method);
        FunctionDeclaration? declaration = valid ? FunctionDeclaration.Create(method, name, diagnostics, set, parameterModels: parameters) : null;
        valid = valid && declaration is not null;
        return new(DeclarationIdentity.Create(method), valid ? new(new(parameters), result, set, MethodInvocation.Create(method),
            PgFunctionGenerator.GetCallbackName(method, name)) : null, declaration, SqlDeclarationOptions.Read(method.GetAttributes().FirstOrDefault(static value =>
                value.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute"), attribute.SemanticModel.Compilation, cancellationToken),
            new(problems), GeneratorLocation.Create(method.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
    }

    /// <summary>
    /// Contains the immutable SQL-slot and invocation values required by managed and native renderers.
    /// </summary>
    /// <param name="Parameters">The ordered SQL and injected argument contracts.</param>
    /// <param name="Result">The scalar result, or null for a set.</param>
    /// <param name="Set">The validated iterator output, or null for a scalar.</param>
    /// <param name="Invocation">The managed call and output policies.</param>
    /// <param name="Callback">The assembly-specific callback identity for this overload and SQL name.</param>
    internal sealed record FunctionModel(EquatableArray<FunctionParameter> Parameters, FunctionType? Result, SetResult? Set, MethodInvocation Invocation, string Callback);

    /// <summary>
    /// Separates a function's conversion model from source-dependent diagnostic coordinates.
    /// </summary>
    /// <param name="Identity">The assembly-qualified method identity.</param>
    /// <param name="Model">The validated conversion model, or null after a reported validation failure.</param>
    /// <param name="Declaration">The validated SQL options and input contracts, or null after a validation failure.</param>
    /// <param name="Options">The detached SQL generation and graph dependency policy.</param>
    /// <param name="Problems">Diagnostics to resolve on the current source trees.</param>
    /// <param name="Location">The declaring source coordinates, including invalid duplicate signatures.</param>
    internal sealed record FunctionAnalysis(DeclarationIdentity Identity, FunctionModel? Model, FunctionDeclaration? Declaration,
        SqlDeclarationOptions? Options, EquatableArray<GeneratorProblem> Problems, GeneratorLocation? Location);

    /// <summary>
    /// Supplies one analyzed function and its independently cached boundary artifacts.
    /// </summary>
    /// <param name="Analysis">The detached semantic values and current diagnostic coordinates.</param>
    /// <param name="Emission">The rendered boundary, or null after semantic validation failed.</param>
    /// <param name="Declaration">The independently cached SQL declaration contract, or null after validation failed.</param>
    /// <param name="Sql">The independently rendered SQL fragments, or null after validation failed.</param>
    internal sealed record FunctionOutput(FunctionAnalysis Analysis, FunctionEmission? Emission, FunctionDeclaration? Declaration, FunctionSqlEmission? Sql);

    /// <summary>
    /// Supplies detached method inventory and independently cached function contracts to graph composition.
    /// </summary>
    /// <param name="Methods">The current immutable selection and native capability inventory.</param>
    /// <param name="Functions">The independently analyzed ordinary functions.</param>
    /// <param name="Triggers">The independently analyzed row and event callbacks.</param>
    /// <param name="Workers">The independently analyzed background-worker entries.</param>
    /// <param name="Lifecycle">The canonical initialization phases with independently rendered callbacks.</param>
    /// <param name="OperatorCasts">The attached operator and cast catalog declarations and fragments.</param>
    /// <param name="Tests">The independently analyzed tests, discovery catalogs and native boundaries.</param>
    /// <param name="Benchmarks">The independently analyzed benchmark declarations.</param>
    /// <param name="OutputPlugins">The independently analyzed logical decoding output plugin initializers.</param>
    /// <param name="CustomScans">The independently analyzed custom scan providers.</param>
    internal sealed record MethodInputs(EquatableArray<MethodInventoryModel> Methods, EquatableArray<FunctionOutput> Functions,
        EquatableArray<TriggerPipeline.TriggerOutput> Triggers, EquatableArray<BackgroundWorkerPipeline.WorkerOutput> Workers,
        EquatableArray<LifecyclePipeline.LifecycleOutput> Lifecycle, EquatableArray<OperatorCastPipeline.Output> OperatorCasts,
        PgTestPipeline.Output Tests, EquatableArray<PgBenchmarkPipeline.Output> Benchmarks,
        EquatableArray<OutputPluginPipeline.Output> OutputPlugins, EquatableArray<CustomScanPipeline.Analysis> CustomScans);
}
