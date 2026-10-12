using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ankus.Generators;

/// <summary>
/// Generates managed dispatchers, native PostgreSQL error boundaries, and SQL declarations for attributed functions.
/// </summary>
[Generator]
public sealed class PgFunctionGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor s_invalidName = new(
        "ANKUS002", "Invalid PostgreSQL function name",
        "SQL name '{0}' must start with a lowercase ASCII letter or underscore and contain 1-63 lowercase ASCII letters, digits, or underscores",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://willibrandon.github.io/ankus/function-declarations/#function-names");

    private static readonly DiagnosticDescriptor s_duplicateSqlSignature = new(
        "ANKUS207", "Duplicate PostgreSQL input signature",
        "SQL input signature '{0}' is already declared in this extension; use a different SQL name, schema, or input parameter types",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://willibrandon.github.io/ankus/function-declarations/#function-names");

    /// <summary>
    /// Registers semantic attribute discovery and deterministic extension source generation.
    /// </summary>
    /// <param name="context">The incremental generation context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<IMethodSymbol> functions = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgFunctionAttribute",
            static (node, _) => node is MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);
        IncrementalValuesProvider<IMethodSymbol> tests = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgTestAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);
        IncrementalValuesProvider<IMethodSymbol> benchmarkMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgBenchmarkAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);
        IncrementalValuesProvider<IMethodSymbol> operators = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgOperatorAttribute",
            static (node, _) => node is MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);
        IncrementalValuesProvider<IMethodSymbol> casts = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgCastAttribute",
            static (node, _) => node is MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);
        IncrementalValuesProvider<IMethodSymbol> triggers = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgTriggerAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);
        IncrementalValuesProvider<IMethodSymbol> eventTriggers = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgEventTriggerAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);
        IncrementalValueProvider<ImmutableArray<IMethodSymbol>> methods = functions.Collect().Combine(operators.Collect()).Combine(casts.Collect())
            .Combine(triggers.Collect()).Combine(eventTriggers.Collect())
            .Select(static (input, _) => input.Left.Left.Left.Left.AddRange(input.Left.Left.Left.Right).AddRange(input.Left.Left.Right)
                .AddRange(input.Left.Right).AddRange(input.Right)
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToImmutableArray())
            .Combine(tests.Collect()).Select(static (input, _) => input.Left.AddRange(input.Right)
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToImmutableArray())
            .Combine(benchmarkMethods.Collect()).Select(static (input, _) => input.Left.AddRange(input.Right)
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default).ToImmutableArray());
        IncrementalValueProvider<EquatableArray<PgBenchmarkPipeline.Output>> benchmarks = PgBenchmarkPipeline.Register(context);
        IncrementalValueProvider<FunctionPipeline.MethodInputs> methodInputs = MethodInventoryPipeline.Register(context, methods)
            .Combine(FunctionPipeline.Register(context)).Combine(TriggerPipeline.Register(context))
            .Combine(BackgroundWorkerPipeline.Register(context)).Combine(LifecyclePipeline.Register(context))
            .Combine(OperatorCastPipeline.Register(context)).Combine(PgTestPipeline.Register(context))
            .Combine(benchmarks).Combine(OutputPluginPipeline.Register(context)).Combine(CustomScanPipeline.Register(context))
            .Select(static (value, _) => new FunctionPipeline.MethodInputs(value.Left.Left.Left.Left.Left.Left.Left.Left.Left,
                value.Left.Left.Left.Left.Left.Left.Left.Left.Right, value.Left.Left.Left.Left.Left.Left.Left.Right,
                value.Left.Left.Left.Left.Left.Left.Right, value.Left.Left.Left.Left.Left.Right, value.Left.Left.Left.Left.Right,
                value.Left.Left.Left.Right, value.Left.Left.Right, value.Left.Right, value.Right));
        IncrementalValueProvider<EquatableArray<EnumPipeline.EnumOutput>> enums = EnumPipeline.Register(context);
        IncrementalValueProvider<EquatableArray<CustomTypePipeline.Output>> customTypes = CustomTypePipeline.Register(context);
        IncrementalValuesProvider<INamedTypeSymbol> datumTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgDatumTypeAttribute",
            static (node, _) => node is BaseTypeDeclarationSyntax,
            static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol);
        IncrementalValuesProvider<INamedTypeSymbol> rangeTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgRangeTypeAttribute",
            static (node, _) => node is BaseTypeDeclarationSyntax,
            static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol);
        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol>> derivedOperators = DerivedTypes(context, "Ankus.PgEqualityAttribute").Collect()
            .Combine(DerivedTypes(context, "Ankus.PgOrderingAttribute").Collect())
            .Combine(DerivedTypes(context, "Ankus.PgHashingAttribute").Collect())
            .Select(static (value, _) => value.Left.Left.AddRange(value.Left.Right).AddRange(value.Right)
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default).ToImmutableArray());
        IncrementalValueProvider<EquatableArray<SchemaPipeline.SchemaOutput>> schemas = SchemaPipeline.Register(context);
        IncrementalValueProvider<EquatableArray<AggregatePipeline.Output>> aggregates = AggregatePipeline.Register(context);
        IncrementalValuesProvider<ISymbol> requires = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgRequiresAttribute", static (_, _) => true, static (attributeContext, _) => attributeContext.TargetSymbol);
        IncrementalValuesProvider<ISymbol> before = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgBeforeAttribute", static (_, _) => true, static (attributeContext, _) => attributeContext.TargetSymbol);
        IncrementalValuesProvider<ISymbol> plannerSupport = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgSupportFunctionAttribute", static (_, _) => true, static (attributeContext, _) => attributeContext.TargetSymbol);
        IncrementalValueProvider<ImmutableArray<ISymbol>> referenceDeclarations = requires.Collect().Combine(before.Collect()).Combine(plannerSupport.Collect())
            .Select(static (input, _) => input.Left.Left.AddRange(input.Left.Right).AddRange(input.Right));
        IncrementalValueProvider<EquatableArray<SqlReferenceModel>> references = SqlReferencePipeline.Register(context, referenceDeclarations);
        IncrementalValueProvider<GucPipeline.PropertyInputs> propertyInputs = NativeCallbackPipeline.Register(context)
            .Combine(GucPipeline.Register(context)).Combine(GucPipeline.RegisterRuntimeUse(context))
            .Select(static (value, _) => new GucPipeline.PropertyInputs(value.Left.Left, value.Left.Right, value.Right));
        IncrementalValueProvider<EquatableArray<SqlProviderModel>> providers = SqlProviderPipeline.Register(context);
        IncrementalValueProvider<GucPrefixPipeline.Output> prefixes = GucPrefixPipeline.Register(context);
        IncrementalValueProvider<(string Directory, bool IncludeTests, bool IncludeBenchmarks, string? Version)> projectDirectory =
            context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
                (BuildProperty(options.GlobalOptions, "MSBuildProjectDirectory") ?? string.Empty,
                    string.Equals(BuildProperty(options.GlobalOptions, "AnkusIncludeTests"), "true", StringComparison.OrdinalIgnoreCase),
                    string.Equals(BuildProperty(options.GlobalOptions, "AnkusIncludeBenchmarks"), "true", StringComparison.OrdinalIgnoreCase),
                    BuildProperty(options.GlobalOptions, "Version")));
        IncrementalValueProvider<NativeModuleMagic.ModuleOutput> module = NativeModuleMagic.Register(context,
            projectDirectory.Select(static (settings, _) => settings.Version));
        IncrementalValueProvider<EquatableArray<CustomSqlPipeline.Output>> sqlBlocks = CustomSqlPipeline.Register(context,
            projectDirectory.Select(static (settings, _) => settings.Directory));
        IncrementalValueProvider<DatumPipeline.Output> mappings = DatumPipeline.Register(context, methods, datumTypes.Collect(), rangeTypes.Collect(),
            aggregates, derivedOperators, projectDirectory.Select(static (settings, _) => settings.IncludeTests));
        IncrementalValueProvider<NativeCompilationPipeline.Output> nativeCompilation = NativeCompilationPipeline.Register(context);
        IncrementalValueProvider<ExtensionCompositionInput> inputs = methodInputs.Combine(schemas).Combine(providers).Combine(sqlBlocks).Combine(projectDirectory).Combine(enums).Combine(aggregates).Combine(propertyInputs).Combine(prefixes).Combine(customTypes).Combine(mappings).Combine(references.Combine(module).Combine(nativeCompilation))
            .Select(static (input, _) => new ExtensionCompositionInput(
                input.Left.Left.Left.Left.Left.Left.Left.Left.Left.Left.Left, input.Left.Left.Left.Left.Left.Left.Left.Left.Left.Left.Right,
                input.Left.Left.Left.Left.Left.Left.Left.Left.Left.Right, input.Left.Left.Left.Left.Left.Left.Left.Left.Right, input.Left.Left.Left.Left.Left.Left.Left.Right,
                input.Left.Left.Left.Left.Left.Left.Right, input.Left.Left.Left.Left.Left.Right, input.Left.Left.Left.Left.Right, input.Left.Left.Left.Right, input.Left.Left.Right, input.Left.Right,
                input.Right.Left.Left, input.Right.Left.Right, input.Right.Right))
            .WithTrackingName("ExtensionCompositionInput");
        IncrementalValueProvider<ImmutableArray<GeneratorSourceTree>> trees = context.CompilationProvider
            .SelectMany(static (compilation, _) => compilation.SyntaxTrees)
            .Select(static (tree, token) => GeneratorSourceTree.Create(tree, token)).WithTrackingName("ExtensionSourceTree").Collect();
        IncrementalValueProvider<string?> benchmarkSourceLines = benchmarks.Combine(projectDirectory).Combine(trees)
            .Select(static (value, token) => value.Left.Right.IncludeBenchmarks
                ? RenderBenchmarkSourceLines(value.Left.Left, value.Right, token) : null)
            .WithTrackingName("BenchmarkSourceLineEmission");
        context.RegisterSourceOutput(benchmarkSourceLines, static (output, source) =>
        {
            if (source is not null)
            {
                output.AddSource("BenchmarkSourceLines.g.cs", source);
            }
        });
        IncrementalValueProvider<GeneratorSourceMap> sources = inputs.Combine(trees)
            .Select(static (value, token) => GeneratorSourceMap.Create(value.Left.Locations(), value.Right, token))
            .WithTrackingName("ExtensionSourceMap");
        IncrementalValueProvider<GeneratorCompositionContext.Output> composition = inputs
            .Select(static (value, token) => Compose(value, GeneratorSourceMap.Anchors(value.Locations()), token))
            .WithTrackingName("ExtensionComposition");
        IncrementalValueProvider<EquatableArray<InstallationGraphModel.Node>> semanticNodes = composition
            .Select(static (value, _) => value.Manifest?.Metadata.Graph.Nodes ?? new EquatableArray<InstallationGraphModel.Node>([]))
            .WithTrackingName("ExtensionSemanticGraph");
        IncrementalValuesProvider<InstallationGraphModel.Node> graphNodes = semanticNodes.Combine(sources)
            .SelectMany(static (value, _) => InstallationGraphModel.Attribute(value.Left, value.Right))
            .WithTrackingName("ExtensionGraphNode");
        IncrementalValuesProvider<string> sqlComponents = graphNodes.Select(static (value, _) => value.Provenance)
            .WithTrackingName("ExtensionSqlProvenanceModel")
            .Select(static (value, _) => SqlProvenance.Render(value))
            .WithTrackingName("ExtensionSqlComponentEmission");
        IncrementalValueProvider<string> installation = sqlComponents.Collect().Select(static (value, _) => new EquatableArray<string>(value))
            .WithTrackingName("ExtensionInstallationModel")
            .Select(static (value, _) => RenderInstallation(value)).WithTrackingName("ExtensionInstallationEmission");
        IncrementalValueProvider<InstallationGraphEncoding.Output> graph = graphNodes.Collect().Combine(sqlComponents.Collect())
            .Select(static (value, _) => new EquatableArray<InstallationGraphModel.EncodedNode>(value.Left.Select((node, index) =>
                new InstallationGraphModel.EncodedNode(node.Key, node.Kind, value.Right[index], node.Owner, node.Names, node.Dependencies, node.Attachments))))
            .WithTrackingName("ExtensionGraphModel")
            .Select(static (value, _) => InstallationGraphEncoding.Encode(value)).WithTrackingName("ExtensionGraphEmission");
        IncrementalValuesProvider<GeneratorCompositionContext.Artifact> artifacts = composition
            .SelectMany(static (value, _) => value.Artifacts).WithTrackingName("ExtensionArtifactPlan");
        context.RegisterSourceOutput(artifacts.Combine(graph).Where(static value => !value.Left.RequiresGraph || value.Right.Limit is null)
            .Select(static (value, _) => value.Left).Select(static (value, _) => (value.Name, Source: value.Plan.Render()))
            .WithTrackingName("ExtensionArtifactEmission"), static (output, artifact) => output.AddSource(artifact.Name, artifact.Source));
        IncrementalValueProvider<GeneratorSourcePlan?> native = composition.Select(static (value, _) => value.Manifest?.Native)
            .WithTrackingName("ExtensionNativePlan");
        IncrementalValueProvider<string?> nativeSource = native.Select(static (value, _) => value?.Render())
            .WithTrackingName("ExtensionNativeEmission");
        IncrementalValueProvider<GeneratorSourcePlan?> exports = composition.Select(static (value, _) => value.Manifest?.Exports)
            .WithTrackingName("ExtensionExportPlan");
        IncrementalValueProvider<string?> exportSource = exports.Select(static (value, _) => value?.Render())
            .WithTrackingName("ExtensionExportEmission");
        IncrementalValueProvider<GeneratorCompositionContext.ManifestMetadata?> metadata = composition.Select(static (value, _) => value.Manifest?.Metadata)
            .WithTrackingName("ExtensionManifestMetadata");
        context.RegisterSourceOutput(metadata.Combine(nativeSource).Combine(exportSource).Combine(installation).Combine(graph)
            .Select(static (value, _) => RenderManifest(value.Left.Left.Left.Left, value.Left.Left.Left.Right, value.Left.Left.Right, value.Left.Right, value.Right))
            .WithTrackingName("ExtensionManifestEmission"), static (output, manifest) =>
            {
                if (manifest is not null)
                {
                    output.AddSource("ExtensionManifest.g.cs", manifest);
                }
            });
        context.RegisterSourceOutput(graph, static (output, value) =>
        {
            if (value.Limit is { } limit)
            {
                output.ReportDiagnostic(Diagnostic.Create(SqlGraphDiagnostics.Descriptor(limit), null));
            }
        });
        context.RegisterSourceOutput(composition.Select(static (value, _) => value.Problems).WithTrackingName("ExtensionProblems")
            .Combine(context.CompilationProvider), static (output, value) =>
            {
                foreach (GeneratorProblem problem in value.Left)
                {
                    problem.Report(value.Right, output);
                }
            });
    }

    /// <summary>
    /// Reads a compiler-visible project setting while retaining defaults for unset properties.
    /// </summary>
    /// <param name="options">The evaluated global compiler options.</param>
    /// <param name="name">The MSBuild property name.</param>
    /// <returns>The authored value, or null when unset.</returns>
    private static string? BuildProperty(AnalyzerConfigOptions options, string name)
        => options.TryGetValue("build_property." + name, out string? value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>
    /// Uses semantic attribute indexing for one declared value-operator capability, including attribute aliases.
    /// </summary>
    /// <param name="context">The incremental registration context.</param>
    /// <param name="metadataName">The exact public value-operator attribute.</param>
    /// <returns>The matching type symbols confined to transient semantic mapping analysis.</returns>
    private static IncrementalValuesProvider<INamedTypeSymbol> DerivedTypes(IncrementalGeneratorInitializationContext context, string metadataName)
        => context.SyntaxProvider.ForAttributeWithMetadataName(metadataName,
            static (node, _) => node is BaseTypeDeclarationSyntax,
            static (attributeContext, _) => (INamedTypeSymbol)attributeContext.TargetSymbol);

    /// <summary>
    /// Validates and composes detached declaration contracts without consulting compiler objects.
    /// </summary>
    /// <param name="input">The complete immutable declaration inventory.</param>
    /// <param name="sources">The detached source attribution.</param>
    /// <param name="cancellationToken">The current composition cancellation token.</param>
    /// <returns>The immutable source artifacts and original diagnostics.</returns>
    private static GeneratorCompositionContext.Output Compose(ExtensionCompositionInput input, GeneratorSourceMap sources, CancellationToken cancellationToken)
    {
        var resolver = new GeneratorSourceResolver(sources);
        var context = new GeneratorCompositionContext(resolver, cancellationToken);
        Generate(context, input.Methods, input.Schemas, input.Providers, input.CustomBlocks, input.Settings, input.Enums, input.Aggregates, input.Properties,
            input.Prefixes, input.CustomTypes, input.Mappings, resolver, input.References, input.Module, input.NativeCompilation);
        return context.Freeze();
    }

    private static void Generate(GeneratorCompositionContext context, FunctionPipeline.MethodInputs methodInputs, EquatableArray<SchemaPipeline.SchemaOutput> schemaTypes,
        EquatableArray<SqlProviderModel> providers, EquatableArray<CustomSqlPipeline.Output> customBlocks,
        (string Directory, bool IncludeTests, bool IncludeBenchmarks, string? Version) settings,
        EquatableArray<EnumPipeline.EnumOutput> enumTypes, EquatableArray<AggregatePipeline.Output> aggregateOutputs, GucPipeline.PropertyInputs propertyInputs,
        GucPrefixPipeline.Output prefixOutput, EquatableArray<CustomTypePipeline.Output> customTypes, DatumPipeline.Output mappingOutput, GeneratorSourceResolver compilation,
        EquatableArray<SqlReferenceModel> references, NativeModuleMagic.ModuleOutput module, NativeCompilationPipeline.Output nativeCompilation)
    {
        EquatableArray<MethodInventoryModel> methods = methodInputs.Methods;
        ILookup<DeclarationIdentity, FunctionPipeline.FunctionOutput> functionModels = methodInputs.Functions.ToLookup(static value => value.Analysis.Identity);
        ILookup<DeclarationIdentity, TriggerPipeline.TriggerOutput> triggerModels = methodInputs.Triggers.ToLookup(static value => value.Analysis.Identity);
        ILookup<DeclarationIdentity, OperatorCastPipeline.Output> operatorModels = methodInputs.OperatorCasts.ToLookup(static value => value.Analysis.Identity);
        bool referencedCallbacks = nativeCompilation.Analysis.ReferencedCallbacks;
        if (!referencedCallbacks && !module.Declared && references.IsEmpty && methods.IsEmpty && methodInputs.Workers.IsEmpty && methodInputs.OutputPlugins.IsEmpty && methodInputs.CustomScans.IsEmpty && methodInputs.Lifecycle.IsEmpty && schemaTypes.IsEmpty && providers.IsEmpty && customBlocks.IsEmpty && enumTypes.IsEmpty && aggregateOutputs.IsEmpty && propertyInputs.Callbacks.IsEmpty && propertyInputs.Settings.IsEmpty && !propertyInputs.RuntimeSettings && !prefixOutput.Analysis.Declared && customTypes.IsEmpty && !mappingOutput.Analysis.Declared)
        {
            return;
        }

        ILookup<DeclarationIdentity, PgTestPipeline.TestOutput> testModels = methodInputs.Tests.Tests.ToLookup(static value => value.Analysis.Identity);
        var tests = new Dictionary<MethodInventoryModel, PgTestPipeline.TestOutput>();
        var invalidTests = new HashSet<MethodInventoryModel>();
        foreach (MethodInventoryModel method in methods.Where(static method => method.Test).OrderBy(static method => method.Display, StringComparer.Ordinal))
        {
            PgTestPipeline.TestOutput test = testModels[method.Identity]
                .First(value => value.Analysis.Location == method.Location);
            foreach (GeneratorProblem problem in test.Analysis.Problems)
            {
                problem.Report(compilation, context);
            }

            if (test.Analysis.Declaration is null)
            {
                invalidTests.Add(method);
                continue;
            }

            tests.Add(method, test);
        }

        if (tests.Count != 0)
        {
            context.AddSource("PostgresTests.g.cs", "// <auto-generated />\n#nullable enable\n" +
                string.Concat(methodInputs.Tests.Catalogs.Select(static value => value.Source)));
        }

        methods = new(methods.Where(method => !invalidTests.Contains(method) &&
            (settings.IncludeTests || !tests.ContainsKey(method))));

        var benchmarks = new List<PgBenchmarkPipeline.Output>();
        foreach (PgBenchmarkPipeline.Output benchmark in methodInputs.Benchmarks.OrderBy(static value =>
            value.Model?.Display, StringComparer.Ordinal))
        {
            foreach (GeneratorProblem problem in benchmark.Problems)
            {
                problem.Report(compilation, context);
            }

            if (settings.IncludeBenchmarks && benchmark.Model is not null)
            {
                benchmarks.Add(benchmark);
            }
        }

        methods = new(methods.Where(static method => !method.Benchmark));

        foreach (GeneratorProblem problem in mappingOutput.Analysis.Problems)
        {
            problem.Report(compilation, context);
        }

        if (!mappingOutput.Analysis.Valid)
        {
            return;
        }

        EquatableArray<DatumTypeModel> mappings = mappingOutput.Analysis.Models;
        EquatableArray<DerivedOperatorModel> selectedDerivedTypes = mappingOutput.Analysis.Derived;

        var names = new HashSet<string>(StringComparer.Ordinal);
        var relatedNames = new HashSet<string>(StringComparer.Ordinal);
        var managed = new GeneratorSourceBuilder();
        string? magic = module.Source;
        if (magic is null)
        {
            NativeModuleMagic.Report(module, compilation, context);
            return;
        }

        GeneratorSourceBuilder native = new GeneratorSourceBuilder(NativeBridge.Source).Append(magic).Append(NativeSignalRoutingBridge.Source);
        foreach (GeneratorProblem problem in prefixOutput.Analysis.Problems)
        {
            problem.Report(compilation, context);
        }

        EquatableArray<string> prefixes = prefixOutput.Analysis.Prefixes;
        List<GucPipeline.Output> gucOutputs = GucPipeline.Select(propertyInputs.Settings, compilation, context);
        List<GucModel> gucs = [.. gucOutputs.Select(static output => output.Analysis.Model!)];
        List<NativeCallbackPipeline.Output> callbacks = NativeCallbackPipeline.Select(propertyInputs.Callbacks, compilation, context);
        OutputPluginEmission? outputPlugin = OutputPluginPipeline.Select(methodInputs.OutputPlugins, compilation, context);
        CustomScanPipeline.Emission? customScans = CustomScanPipeline.Select(methodInputs.CustomScans, compilation, context);

        bool hasGucHooks = gucs.Any(static guc => guc.HasHooks);
        bool hasGucCheck = gucs.Any(static guc => guc.Check is not null);
        bool hasGucShow = gucs.Any(static guc => guc.Show is not null);
        // The output plugin export and custom scan providers use the native callback boundary's initialization.
        bool hasNativeCallbacks = callbacks.Count != 0 || referencedCallbacks || outputPlugin is not null || customScans is not null;
        List<BackgroundWorkerPipeline.WorkerOutput> workers = BackgroundWorkerPipeline.Select(methodInputs.Workers, compilation, context);
        bool hasWorkers = workers.Count != 0;
        bool hasFunctionCallbacks = !methodInputs.Lifecycle.IsEmpty || hasWorkers || hasNativeCallbacks || !methods.IsEmpty ||
            benchmarks.Count != 0 || !aggregateOutputs.IsEmpty || !customTypes.IsEmpty || !selectedDerivedTypes.IsEmpty;
        bool hasBackend = hasFunctionCallbacks || hasGucCheck;
        // Run-time definitions arrive through the guarded backend, so they need managed callers.
        bool hasRuntimeGucs = propertyInputs.RuntimeSettings && hasBackend;
        bool hasDispatchers = hasFunctionCallbacks || hasGucHooks;
        var aggregateMethods = new HashSet<DeclarationIdentity>(aggregateOutputs.SelectMany(static value => value.Analysis.Selected));
        bool hasMemoryFunctionCallbacks = !methodInputs.Lifecycle.IsEmpty || hasWorkers || hasNativeCallbacks || hasGucHooks || hasRuntimeGucs ||
            benchmarks.Count != 0 || !aggregateOutputs.IsEmpty || !customTypes.IsEmpty || !selectedDerivedTypes.IsEmpty ||
            methods.Any(method => !aggregateMethods.Contains(method.Identity));
        if (hasMemoryFunctionCallbacks)
        {
            native.AppendLine(nativeCompilation.Binding);
            native.AppendLine(nativeCompilation.Layouts);
            native.AppendLine(NativeMemoryBridge.CleanupBinding);
        }

        if (hasBackend)
        {
            native.AppendLine(NativeBridge.ReadBuffers);
            native.AppendLine(NativeBridge.WriteBuffer);
            native.AppendLine(NativeGeometryTypes.Source);
            native.AppendLine(NativeExtendedTypes.Source);
            native.AppendLine(NativeTemporalTypes.Source);
            native.AppendLine(NativeItemPointerTypes.Source);
            native.AppendLine(NativeEnumBridge.Source);
            native.AppendLine(NativeCustomTypeBridge.Source);
            if (!customTypes.IsEmpty)
            {
                native.AppendLine(NativeCustomTypeBridge.TextSource);
                if (customTypes.Any(static type => type.Analysis.Model?.BinaryProtocol == true))
                {
                    native.AppendLine(NativeCustomTypeBridge.BinarySource);
                }
            }

            native.AppendLine(NativeSpiBridge.Source);
            native.AppendLine(NativeTriggerBridge.Declarations);
            native.AppendLine(NativeEnumBridge.Operations);
            native.AppendLine(NativeLookupBridge.Source);
            native.AppendLine(NativeRangeBridge.Source);
            native.AppendLine(NativeArrayBridge.Source);
            native.AppendLine(NativeTupleBridge.Source);
            native.AppendLine(NativeTupleBridge.Operations);
            native.AppendLine(NativeRelationBridge.Source);
            native.AppendLine(NativeScalarFunctions.Source);
            native.AppendLine(NativeTemporalOperations.Source);
            native.AppendLine(NativeNumericOperations.Source);
            native.AppendLine(NativeNetworkOperations.Source);
            native.AppendLine(NativeGeometryOperations.Source);
            native.AppendLine(NativeRangeOperations.Source);
            native.AppendLine(NativeCursorBridge.Source);
            native.AppendLine(NativeSessionBridge.Source);
            native.AppendLine(NativeSqlHelpers.Source);
            native.AppendLine(NativeErrorBridge.Source);
            native.AppendLine(NativeRecoveryBridge.Source);
            native.AppendLine(NativeRecoveryBridge.Terminal);
            if (hasMemoryFunctionCallbacks)
            {
                native.AppendLine(NativeMemoryBridge.Source);
            }

            native.AppendLine(NativeTransactionBridge.Source);
            native.AppendLine(NativeDatumBridge.Source);
            native.AppendLine(NativeMappedRangeBridge.Source);
            native.AppendLine(NativeMappedArrayBridge.Source);
            native.AppendLine(NativeFunctionBridge.Source);
            native.AppendLine(NativeFunctionInvocation.Source);

            if (!aggregateOutputs.IsEmpty || selectedDerivedTypes.Any(static model => model.Value?.DatumType is not null) ||
                methods.Any(static method => method.Sequence || method.RawTransport))
            {
                native.AppendLine(NativeDatumBridge.PolymorphicInput);
            }

            if (hasFunctionCallbacks)
            {
                native.AppendLine(NativeErrorBridge.RaiseError);
            }

            native.AppendLine(NativeGucBridge.ReadBinding);
            native.AppendLine(GuardedBackend.Source);
            if (!aggregateOutputs.IsEmpty)
            {
                native.AppendLine(NativeAggregateBridge.Source);
            }

            if (methods.Any(static method => method.Trigger))
            {
                native.AppendLine(NativeTriggerBridge.Source);
            }

            if (methods.Any(static method => method.EventTrigger))
            {
                native.AppendLine(NativeEventTriggerBridge.Source);
            }

            if (methods.Any(static method => method.Sequence))
            {
                native.AppendLine(NativeSetBridge.Source);
            }
        }

        if (gucs.Count != 0 || hasRuntimeGucs)
        {
            native.AppendLine("#include <math.h>");
            if (hasGucHooks && !hasBackend)
            {
                native.AppendLine(NativeErrorBridge.Source);
                native.AppendLine(NativeRecoveryBridge.Source);
                native.AppendLine(NativeRecoveryBridge.Terminal);
                native.AppendLine("struct AnkusRequest;");
                native.AppendLine("struct AnkusResult;");
                native.AppendLine("typedef int (*AnkusExecute)(struct AnkusRequest *, struct AnkusResult *, AnkusError *);");
                native.AppendLine(NativeMemoryBridge.Source);
            }

            native.AppendLine(NativeGucBridge.Declarations);
            native.AppendLine(NativeGucBridge.Registration);

            if (hasDispatchers || hasRuntimeGucs)
            {
                // A run-time definition may supply any hook, so it needs the complete hook transport.
                native.AppendLine(NativeGucBridge.GetManagedDeclarations(hasGucCheck || hasRuntimeGucs, hasGucHooks || hasRuntimeGucs,
                    hasGucShow || hasRuntimeGucs));
            }
        }

        var graph = new SqlGraph(context, settings.Directory, compilation);
        var schemas = new Dictionary<string, SqlEntity>(StringComparer.Ordinal);
        bool fixedSchema = !schemaTypes.IsEmpty;
        foreach (SchemaPipeline.SchemaOutput output in schemaTypes.OrderBy(static value => value.Analysis.Display, StringComparer.Ordinal))
        {
            SchemaPipeline.SchemaAnalysis analysis = output.Analysis;
            if (analysis.Declaration is { } declared)
            {
                if (!schemas.TryGetValue(declared.Name, out SqlEntity? entity))
                {
                    entity = new SqlEntity("0:schema:" + declared.Name, string.Empty, analysis.Location?.Resolve(compilation)) { Kind = "schema" };
                    schemas.Add(declared.Name, entity);
                    graph.Add(entity);
                }

                if (declared.Create)
                {
                    entity.Sql = output.Sql;
                    if (declared.Name is not ("public" or "pg_catalog"))
                    {
                        entity.Attachments.Add("SCHEMA " + SqlText.Identifier(declared.Name));
                    }
                }

                entity.SelectionNames.UnionWith([declared.Name, SqlText.Identifier(declared.Name), analysis.Display]);
                graph.ConfigureOptions(entity, analysis.Options);
                graph.Register(analysis.Identity, analysis.Display, entity);
            }
            else
            {
                SchemaPipeline.Report(analysis, compilation, context);
            }
        }

        fixedSchema |= !CustomSql.Add(customBlocks, compilation, graph, out Dictionary<string, SqlEntity> sqlBlocks);
        SqlFunctionProviders.Add(providers, sqlBlocks, graph, compilation);
        var typeProviders = new SqlTypeProviders(graph);
        var exports = new GeneratorSourceBuilder("Pg_magic_func\n");
        managed.AppendLine("// <auto-generated />");
        managed.AppendLine("#nullable enable");
        managed.AppendLine("namespace Ankus.Generated;");
        managed.AppendLine("internal static unsafe class ExtensionDispatchers");
        managed.AppendLine("{");

        var enumEntities = new Dictionary<string, SqlEntity>(StringComparer.Ordinal);
        var enumNames = new HashSet<string>(StringComparer.Ordinal);
        if (!enumTypes.IsEmpty || !customTypes.IsEmpty || mappings.Count != 0)
        {
            managed.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
            managed.AppendLine("    internal static void RegisterTypes()");
            managed.AppendLine("    {");
        }

        if (hasBackend)
        {
            native.AppendLine("static bool ankus_enum_supported(Oid type)");
            native.AppendLine("{");
            native.AppendLine("    (void) type;");
        }

        foreach (EnumPipeline.EnumOutput output in enumTypes.OrderBy(static value => value.Analysis.Display, StringComparer.Ordinal))
        {
            EnumPipeline.EnumAnalysis analysis = output.Analysis;
            EnumDeclaration? enumeration = analysis.Declaration;
            if (enumeration is null)
            {
                EnumPipeline.Report(analysis, compilation, context);
                continue;
            }

            var entity = new SqlEntity("1:type:" + enumeration.Managed, output.Emission.Sql, analysis.Location?.Resolve(compilation)) { Kind = "enum" };
            entity.SelectionNames.UnionWith([enumeration.Name, enumeration.Sql, analysis.Display]);
            if (enumeration.Schema is not null)
            {
                entity.SelectionNames.Add(enumeration.Schema + "." + enumeration.Name);
            }

            entity.Attachments.Add("TYPE " + (enumeration.Schema is null ? "\0" : string.Empty) + enumeration.Sql);
            graph.ConfigureOptions(entity, analysis.Options);
            fixedSchema |= !SqlGeneration.ApplyOptions(analysis.Options, entity, [], [], graph);
            graph.Add(entity);
            graph.Register(analysis.Identity, analysis.Display, entity);
            if (!enumNames.Add(enumeration.Sql))
            {
                graph.Error(entity.Location, SqlGraphDiagnostics.s_duplicateEnumType, enumeration.Sql);
            }

            enumEntities.Add(enumeration.Managed, entity);
            typeProviders.Reserve(enumeration.Name, enumeration.Schema, entity);
            if (enumeration.Schema is not null)
            {
                fixedSchema = true;
                if (schemas.TryGetValue(enumeration.Schema, out SqlEntity? schema))
                {
                    entity.Dependencies.Add(schema);
                }
            }

            managed.Append(output.Emission.Registration);
            if (hasBackend)
            {
                native.Append(output.Emission.Native);
            }
        }

        if (hasBackend)
        {
            native.AppendLine("    return false;");
            native.AppendLine("}");
            native.AppendLine();
        }

        if (!customTypes.IsEmpty)
        {
            foreach (CustomTypePipeline.Output output in customTypes.OrderBy(static type => type.Analysis.Display, StringComparer.Ordinal))
            {
                CustomTypePipeline.Report(output.Analysis, compilation, context);
                managed.Append(output.Emission?.Registration);
            }
        }

        foreach (string registrationSource in mappingOutput.Registrations)
        {
            managed.Append(registrationSource);
        }

        if (!enumTypes.IsEmpty || !customTypes.IsEmpty || mappings.Count != 0)
        {
            managed.AppendLine("    }");
            managed.AppendLine();
        }

        (LifecycleEmission? initializer, LifecycleEmission? moduleLoad) = LifecyclePipeline.Select(methodInputs.Lifecycle, compilation, context);
        bool ensureManagedReady = initializer is not null || moduleLoad is not null || hasGucHooks || hasNativeCallbacks || hasWorkers;
        if (ensureManagedReady)
        {
            native.AppendLine("static void ankus_ensure_initialized(void);");
        }

        if (hasBackend)
        {
            native.AppendLine("static bool ankus_custom_type_supported(Oid type)");
            native.AppendLine("{");
            native.AppendLine("    (void) type;");
        }

        CustomTypePipeline.Output[] baseTypes = [.. customTypes.Where(static type => type.Emission is not null)
            .OrderBy(static type => type.Analysis.Display, StringComparer.Ordinal)];
        foreach (CustomTypePipeline.Output output in baseTypes)
        {
            native.Append(output.Emission!.TypeCheck);
        }

        if (hasBackend)
        {
            native.AppendLine("    return false;");
            native.AppendLine("}");
            native.AppendLine();
        }

        foreach (CustomTypePipeline.Output output in baseTypes)
        {
            CustomTypeModel custom = output.Analysis.Model!;
            CustomTypePipeline.Emission emission = output.Emission!;
            managed.Append(emission.Serializer);
            names.Add(custom.Function("in") + "(cstring)");
            names.Add(custom.Function("out") + "(" + custom.Sql + ")");
            if (custom.BinaryProtocol)
            {
                names.Add(custom.Function("recv") + "(internal)");
                names.Add(custom.Function("send") + "(" + custom.Sql + ")");
            }

            managed.Append(emission.Io.Managed);
            foreach (NativeFunctionEmission boundary in emission.Io.Native)
            {
                boundary.AppendTo(native, ensureManagedReady);
            }

            exports.Append(emission.Io.Exports);
            var entity = new SqlEntity("1:type:" + custom.Managed, emission.Io.Sql, output.Analysis.Location?.Resolve(compilation)) { Kind = "type" };
            entity.SelectionNames.UnionWith([custom.Name, custom.Sql, output.Analysis.Display]);
            if (custom.Schema is not null)
            {
                entity.SelectionNames.Add(custom.Schema + "." + custom.Name);
            }

            string typePrefix = custom.Schema is null ? "\0" : string.Empty;
            entity.Attachments.UnionWith(["TYPE " + typePrefix + custom.Sql, "FUNCTION " + typePrefix + custom.Function("in") + "(cstring)",
                "FUNCTION " + typePrefix + custom.Function("out") + "(" + typePrefix + custom.Sql + ")"]);
            if (custom.BinaryProtocol)
            {
                entity.Attachments.UnionWith(["FUNCTION " + typePrefix + custom.Function("recv") + "(internal)",
                    "FUNCTION " + typePrefix + custom.Function("send") + "(" + typePrefix + custom.Sql + ")"]);
            }

            graph.ConfigureOptions(entity, output.Analysis.Options);
            fixedSchema |= !SqlGeneration.ApplyOptions(output.Analysis.Options, entity, [],
                [
                    ("@INPUT_FUNCTION_NAME@", custom.NativeFunction("in")),
                    ("@OUTPUT_FUNCTION_NAME@", custom.NativeFunction("out")),
                    ("@RECEIVE_FUNCTION_NAME@", custom.BinaryProtocol ? custom.NativeFunction("recv") : null),
                    ("@SEND_FUNCTION_NAME@", custom.BinaryProtocol ? custom.NativeFunction("send") : null),
                ], graph);
            graph.Add(entity);
            graph.Register(output.Analysis.Identity, output.Analysis.Display, entity);
            if (!enumNames.Add(custom.Sql))
            {
                graph.Error(entity.Location, SqlGraphDiagnostics.s_duplicateBaseType, custom.Sql);
            }

            enumEntities.Add(custom.Managed, entity);
            typeProviders.Reserve(custom.Name, custom.Schema, entity);
            if (custom.Schema is not null)
            {
                fixedSchema = true;
                if (schemas.TryGetValue(custom.Schema, out SqlEntity? schema))
                {
                    entity.Dependencies.Add(schema);
                }
            }
        }

        fixedSchema |= !typeProviders.Add(providers, sqlBlocks, schemas, mappings, compilation);
        if (ensureManagedReady)
        {
            native.AppendLine(PgModuleLoadEmitter.State);
        }

        if (initializer is not null || moduleLoad is not null || hasNativeCallbacks || hasGucHooks || hasWorkers)
        {
            native.AppendLine(NativeForkHostBridge.Source);
        }

        var registration = new StringBuilder();
        if (gucs.Count != 0 || hasRuntimeGucs)
        {
            var definitions = new List<string>();
            foreach (GucPipeline.Output guc in gucOutputs)
            {
                GucEmission emission = guc.Emission!;
                string symbol = emission.Symbol;
                managed.Append(emission.Managed);
                native.Append(emission.Native);
                definitions.Add(symbol);
                registration.AppendLine($"        ankus_guc_register(&{symbol});");
            }

            if (hasDispatchers || hasRuntimeGucs)
            {
                // C before C23 has no empty initializers; a run-time-only registry keeps one unused slot.
                native.AppendLine(definitions.Count == 0
                    ? "static AnkusGuc *ankus_guc_definitions[1] = { NULL };"
                    : "static AnkusGuc *ankus_guc_definitions[] = { " + string.Join(", ", definitions.Select(static symbol => "&" + symbol)) + " };");
                native.AppendLine($"static const int ankus_guc_count = {definitions.Count};");
                if (hasRuntimeGucs)
                {
                    native.AppendLine(NativeGucBridge.RuntimeDefinitions);
                }

                native.AppendLine(NativeGucBridge.GetManagedSource(hasGucCheck || hasRuntimeGucs, hasGucHooks || hasRuntimeGucs,
                    hasGucShow || hasRuntimeGucs));
                if (hasRuntimeGucs)
                {
                    native.AppendLine(NativeGucBridge.RuntimeHooks);
                    native.AppendLine(NativeGucBridge.RuntimeDefinitionEntry);
                    managed.Append(PgGucEmitter.RuntimeHookDispatcher);
                }

                registration.Insert(0, (hasBackend ? "        ankus_read_guc = ankus_guc_read;\n" : string.Empty) +
                    "        ankus_guc_prepare_encoding();\n");
            }

            if (gucs.Count != 0)
            {
                context.AddSource("GucProperties.g.cs", "// <auto-generated />\n#nullable enable\n" +
                    string.Concat(gucOutputs.Select(static output => output.Emission!.Property)));
            }
        }

        native.Append(prefixOutput.Emission.Native);
        registration.Append(prefixOutput.Emission.Registration);
        if (customScans is not null)
        {
            // Registration is native-only and precedes managed startup; the bridge follows the shared callback boundary.
            native.AppendLine("static void ankus_custom_scan_register(void);");
            registration.AppendLine("        ankus_custom_scan_register();");
        }

        if (initializer is not null || moduleLoad is not null || hasNativeCallbacks || hasWorkers)
        {
            native.AppendLine(NativeErrorBridge.InitializationLogging);
            native.AppendLine(NativeSharedMemoryBridge.Initialization);
            registration.Insert(0, "        ankus_shared_initialize = ankus_shared_run_initializer;\n");
        }

        if (moduleLoad is not null)
        {
            managed.Append(moduleLoad.Managed);
            native.Append(moduleLoad.Native);
        }

        if (initializer is not null || moduleLoad is not null || gucs.Count != 0 || !prefixes.IsEmpty || hasNativeCallbacks || hasWorkers)
        {
            PgInitializeEmitter.Emit(initializer, initializer is null
                    ? GetCallbackName(nativeCompilation.Analysis.Assembly, "initialize") : initializer.Declaration.Callback,
                hasGucHooks || hasRuntimeGucs, registration.ToString(), managed, native, exports, hasNativeCallbacks || hasWorkers, moduleLoad is not null);
        }

        if (hasNativeCallbacks)
        {
            native.AppendLine(NativeCallbackBridge.Source);
        }

        if (callbacks.Count != 0)
        {
            context.AddSource("NativeCallbackProperties.g.cs", "// <auto-generated />\n#nullable enable\n" +
                string.Concat(callbacks.Select(static callback => callback.Emission)));
        }

        outputPlugin?.AppendTo(managed, native, exports);
        if (customScans is not null)
        {
            managed.Append(customScans.Managed);
            native.Append(customScans.Native);
        }

        foreach (BackgroundWorkerPipeline.WorkerOutput worker in workers)
        {
            worker.Emission!.AppendTo(managed, native, exports);
        }

        var operatorEntities = new Dictionary<string, SqlEntity>(StringComparer.Ordinal);
        bool hasVarlenaReader = false;
        foreach (MethodInventoryModel method in methods.OrderBy(static method => method.Display, StringComparer.Ordinal))
        {
            if (aggregateMethods.Contains(method.Identity) || method.Initializer || method.NativeEntry)
            {
                continue;
            }

            bool trigger = method.Trigger;
            bool eventTrigger = method.EventTrigger;
            bool contextParameter = trigger || eventTrigger;
            GeneratorLocation? methodLocation = method.Location;
            FunctionPipeline.FunctionOutput? functionOutput = functionModels[method.Identity]
                .FirstOrDefault(value => value.Analysis.Location == methodLocation);
            TriggerPipeline.TriggerOutput? triggerOutput = contextParameter ? triggerModels[method.Identity]
                .FirstOrDefault(value => value.Analysis.Location == methodLocation) : null;
            if (triggerOutput is { Analysis.Model: null })
            {
                foreach (GeneratorProblem problem in triggerOutput.Analysis.Problems)
                {
                    problem.Report(compilation, context);
                }

                continue;
            }

            FunctionPipeline.FunctionAnalysis? analysis = functionOutput?.Analysis;
            if (analysis is { Model: null })
            {
                foreach (GeneratorProblem problem in analysis.Problems)
                {
                    problem.Report(compilation, context);
                }

                continue;
            }

            tests.TryGetValue(method, out PgTestPipeline.TestOutput? testOutput);
            FunctionPipeline.FunctionModel? model = testOutput?.Analysis.Model ?? analysis?.Model;
            FunctionParameter[] parameters = contextParameter ? [] : [.. model!.Parameters];
            SetResult? set = model?.Set;
            FunctionType? scalarResult = model?.Result;
            FunctionDeclaration declaration = (triggerOutput?.Declaration ?? testOutput?.Analysis.Declaration?.Function ?? functionOutput?.Declaration)!;
            string name = declaration.Name;

            string signature = declaration.QualifiedName + "(" + (contextParameter ? string.Empty : string.Join(",", parameters.Where(static parameter => !parameter.IsInjected).Select(
                static parameter => parameter.Type!.Sql))) + ")";
            if (!IsValidName(name))
            {
                context.Report(s_invalidName, methodLocation?.Resolve(compilation), name);
                continue;
            }

            if (!names.Add(signature))
            {
                context.Report(s_duplicateSqlSignature, methodLocation?.Resolve(compilation), signature);
                continue;
            }

            string callback = (model?.Callback ?? triggerOutput?.Analysis.Model?.Callback)!;
            FunctionEmission? functionEmission = testOutput?.Emission ?? functionOutput?.Emission;
            FunctionSqlEmission? functionSql = testOutput?.Sql ?? functionOutput?.Sql;
            SqlFunction sql;
            if (contextParameter)
            {
                FunctionEmission emission = triggerOutput!.Emission!;
                emission.AppendTo(managed, native, exports, ensureManagedReady);
                sql = new(declaration, triggerOutput.Sql!.Compose(typeProviders), false);
            }
            else if (set is null)
            {
                if (!hasVarlenaReader && parameters.Any(static parameter => parameter.Type?.IsVarlena == true))
                {
                    native.AppendLine(NativeCustomTypeBridge.BorrowSource);
                    hasVarlenaReader = true;
                }

                FunctionEmission emission = functionEmission!;
                emission.AppendTo(managed, native, exports, ensureManagedReady);
                sql = new(declaration, functionSql!.Compose(typeProviders), emission.IsPlannerSupport);
            }
            else
            {
                FunctionEmission emission = functionEmission!;
                emission.AppendTo(managed, native, exports, ensureManagedReady);
                sql = new(declaration, functionSql!.Compose(typeProviders), false);
            }

            var entity = new SqlEntity("1:function:" + method.Display, sql, methodLocation?.Resolve(compilation)) { Kind = "function" };
            entity.SelectionNames.UnionWith([name, declaration.QualifiedName, signature, method.Name, method.Display,
                method.Owner + "." + method.Name]);
            if (declaration.Schema is not null)
            {
                entity.SelectionNames.Add(declaration.Schema + "." + name);
            }

            entity.Attachments.Add("FUNCTION " + SqlSchemaTemplate.Function(declaration, contextParameter ? [] :
                parameters.Where(static parameter => !parameter.IsInjected).Select(static parameter => parameter.Type!), typeProviders));
            SqlDeclarationOptions? functionOptions = triggerOutput?.Analysis.Options ?? analysis?.Options ?? method.Options;
            if (functionOptions is not null)
            {
                graph.ConfigureOptions(entity, functionOptions);
            }

            graph.Add(entity);
            graph.Register(method.Identity, method.Display, entity);
            List<SqlEntity> related = contextParameter ? [] :
                OperatorCastDeclaration.Add(operatorModels[method.Identity]
                    .Where(value => value.Analysis.Location == methodLocation), entity, graph, relatedNames, context, operatorEntities, typeProviders, compilation);
            fixedSchema |= !SqlGeneration.ApplyOptions(functionOptions, entity, related,
                [("@FUNCTION_NAME@", callback.Replace("ankus_managed_", "ankus_fn_"))], graph);
            fixedSchema |= declaration.UsesExtensionSchema && functionOptions?.GenerateSql != false && functionOptions?.Sql is null;

            IEnumerable<FunctionType> contracts = contextParameter ? [] : parameters.Where(static parameter => !parameter.IsInjected).Select(static parameter => parameter.Type!)
                .Concat(set is null ? [scalarResult!] : set.Columns);
            foreach (FunctionType contract in contracts)
            {
                typeProviders.Require(entity, contract);
                EnumDeclaration? enumeration = (contract.Element ?? contract).Enumeration;
                string? typeIdentity = enumeration?.Managed ?? (contract.Element ?? contract).CustomType?.Managed;
                if (typeIdentity is not null && enumEntities.TryGetValue(typeIdentity, out SqlEntity? enumEntity))
                {
                    entity.Dependencies.Add(enumEntity);
                }

                FunctionType leaf = contract.Element ?? contract;
                if ((leaf.DatumType is { External: false } mapping ? mapping.Schema : leaf.Binding?.DependencySchema) is { } typeSchema)
                {
                    fixedSchema = true;
                    if (schemas.TryGetValue(typeSchema, out SqlEntity? schema))
                    {
                        entity.Dependencies.Add(schema);
                    }
                }
            }

            if (declaration.Schema is not null)
            {
                fixedSchema = true;
                if (schemas.TryGetValue(declaration.Schema, out SqlEntity? schema))
                {
                    entity.Dependencies.Add(schema);
                }
            }
        }

        if (benchmarks.Count != 0)
        {
            fixedSchema = true;
            if (!schemas.TryGetValue("benches", out SqlEntity? benchmarkSchema))
            {
                benchmarkSchema = new("0:schema:benches", "CREATE SCHEMA benches;\n", null) { Kind = "schema" };
                benchmarkSchema.SelectionNames.UnionWith(["benches", "\"benches\""]);
                benchmarkSchema.Attachments.Add("SCHEMA \"benches\"");
                schemas.Add("benches", benchmarkSchema);
                graph.Add(benchmarkSchema);
            }

            foreach (PgBenchmarkPipeline.Output output in benchmarks)
            {
                PgBenchmarkPipeline.Model benchmark = output.Model!;
                Location? location = output.Location?.Resolve(compilation);
                FileLinePositionSpan span = location?.GetLineSpan() ?? default;
                string sourceFile = BenchmarkSourcePath(span.Path, settings.Directory);
                (FunctionEmission run, FunctionEmission describe) = EmitBenchmark(benchmark, sourceFile);
                run.AppendTo(managed, native, exports, ensureManagedReady);
                describe.AppendTo(managed, native, exports, ensureManagedReady);

                string runQualified = "benches." + SqlText.Identifier(benchmark.RunName);
                string describeQualified = "benches." + SqlText.Identifier(benchmark.DescribeName);
                string options = "VOLATILE PARALLEL UNSAFE CALLED ON NULL INPUT SECURITY INVOKER NOT LEAKPROOF COST 1";
                string runSql = "CREATE FUNCTION " + runQualified + "(baseline jsonb DEFAULT NULL)\nRETURNS jsonb AS 'MODULE_PATHNAME', '" +
                    run.NativeName + "' LANGUAGE c " + options + ";\n";
                string describeSql = "CREATE FUNCTION " + describeQualified + "()\nRETURNS jsonb AS 'MODULE_PATHNAME', '" +
                    describe.NativeName + "' LANGUAGE c " + options + ";\n";
                var runEntity = new SqlEntity("1:benchmark:" + benchmark.Display + ":run", runSql, location) { Kind = "function" };
                runEntity.SelectionNames.UnionWith([benchmark.RunName, runQualified, benchmark.Display]);
                runEntity.Attachments.Add("FUNCTION " + runQualified + "(jsonb)");
                runEntity.Dependencies.Add(benchmarkSchema);
                graph.Add(runEntity);
                var describeEntity = new SqlEntity("1:benchmark:" + benchmark.Display + ":describe", describeSql, location) { Kind = "function" };
                describeEntity.SelectionNames.UnionWith([benchmark.DescribeName, describeQualified, benchmark.Display]);
                describeEntity.Attachments.Add("FUNCTION " + describeQualified + "()");
                describeEntity.Dependencies.Add(benchmarkSchema);
                graph.Add(describeEntity);
            }
        }

        bool validOperators = true;
        foreach (DerivedOperatorModel model in selectedDerivedTypes)
        {
            Dictionary<string, DerivedHelperEmission> helpers = mappingOutput.Helpers.Where(value =>
                value.Slot.Type == model.Identity && value.Slot.Managed == model.Managed)
                .ToDictionary(static value => value.Slot.Role, static value => value.Emission, StringComparer.Ordinal);
            DerivedSqlEmission? sql = mappingOutput.Sql.Single(value => value.Type == model.Identity && value.Managed == model.Managed).Emission;
            validOperators &= DerivedOperatorDeclaration.Emit(model, enumEntities, typeProviders, schemas, names, relatedNames, operatorEntities, graph,
                context, compilation, helpers, sql, ensureManagedReady, managed, native, exports, out bool relocatable);
            fixedSchema |= !relocatable;
        }

        if (!validOperators)
        {
            return;
        }

        var supportFunctions = new Dictionary<string, (DeclarationIdentity Method, SqlEntity Entity)>(StringComparer.Ordinal);
        var reportedAggregateProblems = new HashSet<GeneratorProblem>();
        foreach (AggregatePipeline.Output output in aggregateOutputs.OrderBy(static value => value.Analysis.Display, StringComparer.Ordinal))
        {
            AggregatePipeline.Analysis analysis = output.Analysis;
            foreach (GeneratorProblem problem in analysis.Problems)
            {
                if (reportedAggregateProblems.Add(problem))
                {
                    problem.Report(compilation, context);
                }
            }

            if (analysis.Model is not { } aggregate || output.Sql is not { } aggregateSql)
            {
                continue;
            }

            if (!names.Add(aggregate.Signature))
            {
                context.Report(s_duplicateSqlSignature, analysis.Location?.Resolve(compilation), aggregate.Signature);
                continue;
            }

            var entity = new SqlEntity("2:aggregate:" + analysis.Display, aggregateSql.Compose(typeProviders), analysis.Location?.Resolve(compilation)) { Kind = "aggregate" };
            entity.SelectionNames.UnionWith([aggregate.Name, aggregate.QualifiedName, aggregate.Signature, analysis.Display]);
            if (aggregate.Schema is not null)
            {
                entity.SelectionNames.Add(aggregate.Schema + "." + aggregate.Name);
            }

            entity.Attachments.Add("AGGREGATE " + aggregateSql.Identity(typeProviders));
            graph.ConfigureOptions(entity, analysis.Options);
            fixedSchema |= !SqlGeneration.ApplyOptions(analysis.Options, entity, [], [], graph);
            graph.Add(entity);
            graph.Register(analysis.Identity, analysis.Display, entity);
            AddSchemaDependency(entity, aggregate.Schema);
            foreach (AggregatePipeline.HelperOutput helperOutput in output.Helpers)
            {
                AggregatePipeline.HelperAnalysis helperAnalysis = helperOutput.Analysis;
                AggregateHelperModel helper = helperAnalysis.Model;
                if (supportFunctions.TryGetValue(helper.Signature, out (DeclarationIdentity Method, SqlEntity Entity) existing) &&
                    existing.Method == helperAnalysis.Identity)
                {
                    entity.Dependencies.Add(existing.Entity);
                    graph.InheritRequirements(entity, existing.Entity);
                    continue;
                }

                if (!names.Add(helper.Signature))
                {
                    context.Report(s_duplicateSqlSignature, helperAnalysis.Location?.Resolve(compilation), helper.Signature);
                    continue;
                }

                string callback = helperAnalysis.Callback;
                managed.Append(helperOutput.Boundary.Managed);
                helperOutput.Boundary.Native.AppendTo(native, ensureManagedReady);
                exports.Append(helperOutput.Boundary.Exports);
                var helperSql = new SqlFunction(helper.Declaration, helperOutput.Sql.Compose(typeProviders), helperOutput.SqlModel.IsPlannerSupport, requiresAggregateContext: true);
                var support = new SqlEntity("1:aggregate-helper:" + analysis.Display + ":" + helper.Role, helperSql, helperAnalysis.Location?.Resolve(compilation)) { Kind = "function" };
                support.SelectionNames.UnionWith([helper.Declaration.Name, helper.Declaration.QualifiedName, helper.Signature, helperAnalysis.Name, helperAnalysis.Display,
                    helperAnalysis.Container + "." + helperAnalysis.Name]);
                support.Attachments.Add("FUNCTION " + helperOutput.SqlModel.Name + "(" +
                    string.Join(",", helperOutput.SqlModel.Parameters.Select(parameter => parameter.Type.Emit(typeProviders))) + ")");
                if (helper.Declaration.Schema is not null)
                {
                    support.SelectionNames.Add(helper.Declaration.Schema + "." + helper.Declaration.Name);
                }

                if (helperAnalysis.Options is not null)
                {
                    graph.ConfigureOptions(support, helperAnalysis.Options);
                }

                fixedSchema |= !SqlGeneration.ApplyOptions(helperAnalysis.Options, support, [],
                    [("@FUNCTION_NAME@", callback.Replace("ankus_managed_", "ankus_fn_"))], graph);
                fixedSchema |= helper.Declaration.UsesExtensionSchema && helperAnalysis.Options?.GenerateSql != false && helperAnalysis.Options?.Sql is null;
                graph.Add(support);
                graph.Register(helperAnalysis.Identity, helperAnalysis.Display, support);
                graph.InheritRequirements(entity, support);
                supportFunctions.Add(helper.Signature, (helperAnalysis.Identity, support));
                entity.Dependencies.Add(support);
                AddSchemaDependency(support, helper.Declaration.Schema);
                foreach (AggregateType contract in helper.Types.Concat([helper.Result]))
                {
                    FunctionType? datum = contract.Datum;
                    typeProviders.Require(support, datum);
                    EnumDeclaration? enumeration = (datum?.Element ?? datum)?.Enumeration;
                    string? typeIdentity = enumeration?.Managed ?? (datum?.Element ?? datum)?.CustomType?.Managed;
                    if (typeIdentity is not null && enumEntities.TryGetValue(typeIdentity, out SqlEntity? enumEntity))
                    {
                        support.Dependencies.Add(enumEntity);
                    }

                    FunctionType? leaf = datum?.Element ?? datum;
                    AddSchemaDependency(support, leaf?.DatumType is { External: false } mapping ? mapping.Schema : leaf?.Binding?.DependencySchema);
                }
            }
        }

        managed.AppendLine("}");
        graph.ResolveReferences(references, compilation);
        InstallationGraphModel? installation = graph.Freeze();
        if (installation is null)
        {
            return;
        }

        context.AddSource("ExtensionDispatchers.g.cs", managed.Freeze(), requiresGraph: true);
        context.SetManifest(new(native.Freeze(), exports.Freeze(), new(installation, !fixedSchema, hasNativeCallbacks)));

        void AddSchemaDependency(SqlEntity entity, string? schema)
        {
            if (schema is not null)
            {
                fixedSchema = true;
                if (schemas.TryGetValue(schema, out SqlEntity? dependency))
                {
                    entity.Dependencies.Add(dependency);
                }
            }
        }
    }

    /// <summary>
    /// Renders the generated run and describe entry points for one validated benchmark.
    /// </summary>
    private static (FunctionEmission Run, FunctionEmission Describe) EmitBenchmark(
        PgBenchmarkPipeline.Model benchmark,
        string sourceFile)
    {
        FunctionType jsonb = FunctionType.Jsonb();
        FunctionType nullableJsonb = FunctionType.Jsonb(nullable: true);
        FunctionParameter baseline = FunctionParameter.Synthetic("baseline", nullableJsonb, 0);
        string definition = "new global::Ankus.CompilerServices.PgBenchmarkDefinition(" +
            string.Join(", ",
            [
                SymbolDisplay.FormatLiteral("benches", quote: true),
                SymbolDisplay.FormatLiteral(benchmark.Display, quote: true),
                SymbolDisplay.FormatLiteral(benchmark.RunName, quote: true),
                benchmark.SetupName is null ? "null" : SymbolDisplay.FormatLiteral(benchmark.SetupName, quote: true),
                "(global::Ankus.PgBenchmarkTransactionMode)" + benchmark.Transaction.ToString(CultureInfo.InvariantCulture),
                SymbolDisplay.FormatLiteral(sourceFile, quote: true),
                "global::Ankus.CompilerServices.AnkusGeneratedBenchmarkSources.Line(" +
                    SymbolDisplay.FormatLiteral(benchmark.RunName, quote: true) + ")",
                "new global::Ankus.CompilerServices.PgBenchmarkConfiguration(" + string.Join(", ",
                [
                    benchmark.SampleSize.ToString(CultureInfo.InvariantCulture),
                    benchmark.MeasurementTimeMilliseconds.ToString(CultureInfo.InvariantCulture),
                    benchmark.WarmupTimeMilliseconds.ToString(CultureInfo.InvariantCulture),
                    benchmark.ResampleCount.ToString(CultureInfo.InvariantCulture),
                    benchmark.NoiseThreshold.ToString("R", CultureInfo.InvariantCulture),
                    benchmark.SignificanceLevel.ToString("R", CultureInfo.InvariantCulture),
                    .. benchmark.Throughput == 0 ? Array.Empty<string>() :
                    [
                        "(global::Ankus.PgBenchmarkThroughput)" + benchmark.Throughput.ToString(CultureInfo.InvariantCulture),
                        benchmark.ThroughputPerIteration.ToString(CultureInfo.InvariantCulture) + "L",
                    ],
                ]) + ")",
            ]) + ")";
        string setup = benchmark.SetupTarget is null ? "null" : "static () => " + benchmark.SetupTarget + "()";
        string benchmarkCall = "static bencher => " + benchmark.Target + "(bencher)";
        string runInvocation = "global::Ankus.CompilerServices.PgBenchmarkRunner.Run(" + definition + ", " + setup + ", " +
            benchmarkCall + ", " + baseline.ReadExpression() + ")";
        var runManaged = new StringBuilder();
        PgFunctionEmitter.EmitManaged(benchmark.RunCallback, jsonb, runInvocation, string.Empty, false, runManaged);
        string runNativeName = benchmark.RunCallback.Replace("ankus_managed_", "ankus_fn_");
        var run = new FunctionEmission(runManaged.ToString(),
            PgFunctionEmitter.CreateNative(runNativeName, benchmark.RunCallback, [nullableJsonb], jsonb),
            new StringBuilder().AppendLine(runNativeName).AppendLine("pg_finfo_" + runNativeName).ToString(), runNativeName, false);

        string describeInvocation = "global::Ankus.CompilerServices.PgBenchmarkRunner.Describe(" + definition + ")";
        var describeManaged = new StringBuilder();
        PgFunctionEmitter.EmitManaged(benchmark.DescribeCallback, jsonb, describeInvocation, string.Empty, false, describeManaged);
        string describeNativeName = benchmark.DescribeCallback.Replace("ankus_managed_", "ankus_fn_");
        var describe = new FunctionEmission(describeManaged.ToString(),
            PgFunctionEmitter.CreateNative(describeNativeName, benchmark.DescribeCallback, [], jsonb),
            new StringBuilder().AppendLine(describeNativeName).AppendLine("pg_finfo_" + describeNativeName).ToString(), describeNativeName, false);
        return (run, describe);
    }

    /// <summary>
    /// Keeps benchmark descriptors portable across developer and CI workspaces.
    /// </summary>
    private static string BenchmarkSourcePath(string path, string projectDirectory)
        => SqlProvenance.RelativePath(path, projectDirectory);

    /// <summary>
    /// Renders physical benchmark lines independently from semantic extension composition.
    /// </summary>
    private static string? RenderBenchmarkSourceLines(
        EquatableArray<PgBenchmarkPipeline.Output> benchmarks,
        ImmutableArray<GeneratorSourceTree> trees,
        CancellationToken cancellationToken)
    {
        PgBenchmarkPipeline.Output[] selected = [.. benchmarks.Where(static value => value.Model is not null)];
        if (selected.Length == 0)
        {
            return null;
        }

        GeneratorSourceMap sources = GeneratorSourceMap.Create(selected.Select(static value => value.Location), trees,
            cancellationToken);
        var resolver = new GeneratorSourceResolver(sources);
        var source = new StringBuilder("""
            // <auto-generated />
            #nullable enable
            namespace Ankus.CompilerServices;
            internal static class AnkusGeneratedBenchmarkSources
            {
                internal static int Line(string functionName)
                    => functionName switch
                    {

            """);
        foreach (PgBenchmarkPipeline.Output output in selected.OrderBy(static value => value.Model!.RunName, StringComparer.Ordinal))
        {
            int line = output.Location?.Resolve(resolver).GetLineSpan().StartLinePosition.Line + 1 ?? 0;
            source.Append("            ").Append(SymbolDisplay.FormatLiteral(output.Model!.RunName, quote: true))
                .Append(" => ").Append(line.ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        }

        source.AppendLine("            _ => 0,")
            .AppendLine("        };")
            .AppendLine("}");
        return source.ToString();
    }

    /// <summary>
    /// Encodes the manifest only after its graph, native source or export inputs change.
    /// </summary>
    /// <param name="metadata">The validated installation metadata, or no extension.</param>
    /// <param name="nativeSource">The independently cached native source.</param>
    /// <param name="exportManifest">The independently cached linker exports.</param>
    /// <param name="installation">The independently cached complete installation script.</param>
    /// <param name="graph">The independently encoded graph and size validation result.</param>
    /// <returns>The established compiler manifest, or no source after composition fails.</returns>
    private static string? RenderManifest(GeneratorCompositionContext.ManifestMetadata? metadata, string? nativeSource, string? exportManifest,
        string installation, InstallationGraphEncoding.Output graph)
    {
        if (metadata is null || graph.Limit is not null)
        {
            return null;
        }

        return "// <auto-generated />\n" +
            Metadata("Ankus.NativeSource", nativeSource!) +
            Metadata("Ankus.Sql", installation) +
            Metadata("Ankus.SqlGraph", graph.Graph!) +
            Metadata("Ankus.Relocatable", metadata.Relocatable ? "true" : "false") +
            Metadata("Ankus.Exports", exportManifest!) +
            (metadata.NativeCallbacks ? Metadata("Ankus.NativeCallbacks", "1") : string.Empty);
    }

    /// <summary>
    /// Joins current component SQL independently of native sources and encoded graph fields.
    /// </summary>
    /// <param name="components">The ordered cached connected-component texts.</param>
    /// <returns>The established complete installation script without schema insertion markers.</returns>
    private static string RenderInstallation(EquatableArray<string> components)
    {
        string sql = string.Concat(components).Replace("\0", string.Empty);
        return SqlProvenance.Preamble + (sql.Length == 0 ? "-- No installable objects declared.\n" : sql);
    }

    private static string Metadata(string key, string value)
        => $"[assembly: global::System.Reflection.AssemblyMetadata(\"{key}\", {SymbolDisplay.FormatLiteral(value, quote: true)})]\n";

    internal static string NormalizeLineEndings(string value)
    {
        if (value.IndexOf('\r') < 0)
        {
            return value;
        }

        var result = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];

            if (current == '\r')
            {
                result.Append('\n');

                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                result.Append(current);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Resolves the authored SQL name or its conventional managed-name default.
    /// </summary>
    /// <param name="method">The attributed method.</param>
    /// <returns>The unquoted SQL name to validate before graph composition.</returns>
    internal static string GetSqlName(IMethodSymbol method)
    {
        AttributeData? attribute = method.GetAttributes().FirstOrDefault(
            static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute");
        foreach (KeyValuePair<string, TypedConstant> argument in attribute?.NamedArguments ?? [])
        {
            if (argument.Key == "Name" && argument.Value.Value is string value)
            {
                return value;
            }
        }

        return method.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion
            ? "op_" + SqlText.SnakeCase(method.MetadataName.Substring(3)) : SqlText.SnakeCase(method.Name);
    }

    private static bool IsValidName(string name)
        => name.Length is > 0 and <= 63 && name[0] is >= 'a' and <= 'z' or '_' &&
            name.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    /// <summary>
    /// Derives a native callback identity from the assembly name and managed overload.
    /// </summary>
    /// <param name="method">The attributed method.</param>
    /// <param name="sqlName">The selected unquoted SQL name.</param>
    /// <returns>The assembly-specific managed callback symbol.</returns>
    internal static string GetCallbackName(IMethodSymbol method, string sqlName)
        => GetCallbackName(SymbolScope(method.ContainingAssembly) + ":" + method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), sqlName);

    /// <summary>
    /// Scopes native symbols to an extension assembly by name alone. SQL catalog entries name these symbols, so they
    /// must survive a new assembly version: an upgrade that replaces the library in place keeps the functions it does
    /// not recreate, as pgrx's version-independent wrapper names do.
    /// </summary>
    /// <param name="assembly">The declaring assembly.</param>
    /// <returns>The assembly's simple name.</returns>
    internal static string SymbolScope(IAssemblySymbol assembly) => assembly.Identity.Name;

    /// <summary>
    /// Keeps managed entry symbols distinct across assemblies, including synthetic initialization entries.
    /// </summary>
    internal static string GetCallbackName(string identity, string sqlName)
    {
        using SHA256 hash = SHA256.Create();
        byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(identity));
        string suffix = string.Concat(bytes.Take(16).Select(static value => value.ToString("x2", CultureInfo.InvariantCulture)));
        return "ankus_managed_" + suffix + "_" + sqlName;
    }
}
