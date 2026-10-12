using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Detaches benchmark declarations and configuration before extension composition.
/// </summary>
internal static class PgBenchmarkPipeline
{
    private const string Help = "https://willibrandon.github.io/ankus/benchmarks/";
    private static readonly DiagnosticDescriptor s_static = new(
        "ANKUS130", "PostgreSQL benchmark must be static", "Benchmark '{0}' must be static; the benchmark runner calls it without an instance",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_async = new(
        "ANKUS527", "PostgreSQL benchmark must be synchronous",
        "Benchmark '{0}' cannot use async; measure work that completes on the calling PostgreSQL thread",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_generic = new(
        "ANKUS528", "PostgreSQL benchmark cannot be generic", "Benchmark '{0}' cannot declare type parameters; the runner calls one closed method",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_abstract = new(
        "ANKUS529", "PostgreSQL benchmark must have an implementation", "Benchmark '{0}' must have a body for the runner to call",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_result = new(
        "ANKUS530", "PostgreSQL benchmark must return void",
        "Benchmark '{0}' must return void; register the measured routine with its PgBencher instead of returning a value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_access = new(
        "ANKUS531", "PostgreSQL benchmark must be accessible",
        "Benchmark '{0}' must be public, internal or protected internal so the generated runner can call it",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_parameter = new(
        "ANKUS532", "PostgreSQL benchmark requires one PgBencher parameter", "Benchmark '{0}' must declare exactly one Ankus.PgBencher parameter",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_reference = new(
        "ANKUS533", "PostgreSQL benchmark bencher must be passed by value", "Benchmark '{0}' must receive its PgBencher by value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_container = new(
        "ANKUS534", "Invalid PostgreSQL benchmark container",
        "'{0}' cannot contain benchmark '{1}'; benchmarks belong to non-generic, non-file-local public or internal classes",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_setup = new(
        "ANKUS131", "Invalid PostgreSQL benchmark setup",
        "Benchmark '{0}' setup '{1}' must identify one accessible synchronous static void method with no parameters; {2}",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_configuration = new(
        "ANKUS132", "Invalid PostgreSQL benchmark configuration", "Benchmark '{0}' has an invalid {1} value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);
    private static readonly DiagnosticDescriptor s_conflict = new(
        "ANKUS133", "Conflicting PostgreSQL benchmark role", "Benchmark '{0}' cannot also declare an extension export, backend test, initialization callback, or worker",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: Help);

    /// <summary>
    /// Registers one independently comparable analysis value per attributed method.
    /// </summary>
    internal static IncrementalValueProvider<EquatableArray<Output>> Register(IncrementalGeneratorInitializationContext context)
        => context.SyntaxProvider.ForAttributeWithMetadataName("Ankus.PgBenchmarkAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token)).Collect()
            .Select(static (values, _) => new EquatableArray<Output>(values))
            .WithTrackingName("PgBenchmarkAnalysis");

    private static Output Analyze(GeneratorAttributeSyntaxContext attribute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = (IMethodSymbol)attribute.TargetSymbol;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, attribute.SemanticModel.Compilation), new(arguments))), cancellationToken);
        Model? model = Create(method, diagnostics);
        return new(DeclarationIdentity.Create(method), model, new(problems),
            GeneratorLocation.Create(method.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
    }

    private static Model? Create(IMethodSymbol method, GeneratorDiagnostics diagnostics)
    {
        if (!ValidDeclaration(method, diagnostics))
        {
            return null;
        }

        if (method.GetAttributes().Any(static value => value.AttributeClass?.ToDisplayString() is
            "Ankus.PgFunctionAttribute" or "Ankus.PgTestAttribute" or "Ankus.PgOperatorAttribute" or "Ankus.PgCastAttribute" or
            "Ankus.PgTriggerAttribute" or "Ankus.PgEventTriggerAttribute" or "Ankus.PgInitializeAttribute" or
            "Ankus.PgModuleLoadAttribute" or "Ankus.PgBackgroundWorkerAttribute"))
        {
            diagnostics.Report(s_conflict, method.Locations.FirstOrDefault(), method.Name);
            return null;
        }

        AttributeData benchmark = method.GetAttributes().First(static value =>
            value.AttributeClass?.ToDisplayString() == "Ankus.PgBenchmarkAttribute");
        string? setupName = AttributeValues.Get<string?>(benchmark, "Setup", null);
        string? setupTarget = null;
        if (setupName is not null)
        {
            IMethodSymbol[] candidates = [.. method.ContainingType.GetMembers(setupName).OfType<IMethodSymbol>()];
            string? problem = candidates.Length switch
            {
                0 => $"'{method.ContainingType.Name}' has no method named '{setupName}'",
                1 => SetupProblem(candidates[0]),
                _ => $"'{setupName}' has {candidates.Length} overloads",
            };
            if (problem is not null)
            {
                diagnostics.Report(s_setup, BenchmarkOption(benchmark, "Setup", method), method.Name, setupName, problem);
                return null;
            }

            setupTarget = Target(candidates[0]);
        }

        int transaction = AttributeValues.Get(benchmark, "Transaction", 0);
        int sampleSize = AttributeValues.Get(benchmark, "SampleSize", 100);
        int measurement = AttributeValues.Get(benchmark, "MeasurementTimeMilliseconds", 5_000);
        int warmup = AttributeValues.Get(benchmark, "WarmupTimeMilliseconds", 3_000);
        int resamples = AttributeValues.Get(benchmark, "ResampleCount", 100_000);
        double noise = AttributeValues.Get(benchmark, "NoiseThreshold", 0.01);
        double significance = AttributeValues.Get(benchmark, "SignificanceLevel", 0.05);
        int throughput = AttributeValues.Get(benchmark, "Throughput", 0);
        long throughputPerIteration = AttributeValues.Get(benchmark, "ThroughputPerIteration", 0L);
        if (transaction is < 0 or > 2)
        {
            return Invalid("Transaction");
        }

        if (sampleSize < 10)
        {
            return Invalid("SampleSize");
        }

        if (measurement <= 0)
        {
            return Invalid("MeasurementTimeMilliseconds");
        }

        if (warmup <= 0)
        {
            return Invalid("WarmupTimeMilliseconds");
        }

        if (resamples <= 0)
        {
            return Invalid("ResampleCount");
        }

        if (double.IsNaN(noise) || double.IsInfinity(noise) || noise < 0)
        {
            return Invalid("NoiseThreshold");
        }

        if (double.IsNaN(significance) || double.IsInfinity(significance) || significance is <= 0 or >= 1)
        {
            return Invalid("SignificanceLevel");
        }

        if (throughput is < 0 or > 3)
        {
            return Invalid("Throughput");
        }

        if (throughput == 0 ? throughputPerIteration != 0 : throughputPerIteration <= 0)
        {
            return Invalid("ThroughputPerIteration");
        }

        string display = method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        using SHA256 hash = SHA256.Create();
        byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(method.ContainingAssembly.Name + ":" + display));
        string suffix = string.Concat(digest.Take(16).Select(static value => value.ToString("x2", CultureInfo.InvariantCulture)));
        string runName = "ankus_bench_" + suffix;
        string describeName = runName + "_describe";
        return new(display, Target(method), setupTarget, setupName, runName, describeName,
            PgFunctionGenerator.GetCallbackName(method, runName), PgFunctionGenerator.GetCallbackName(method, describeName),
            transaction, sampleSize, measurement, warmup, resamples, noise, significance, throughput, throughputPerIteration);

        Model? Invalid(string option)
        {
            diagnostics.Report(s_configuration, BenchmarkOption(benchmark, option, method), method.Name, option);
            return null;
        }
    }

    /// <summary>
    /// Reports the first rule the benchmark method or one of its containers breaks, at the syntax that breaks it.
    /// </summary>
    /// <param name="method">The attributed method.</param>
    /// <param name="diagnostics">The diagnostic destination and cancellation token.</param>
    /// <returns>Whether the method can be called by the generated runner.</returns>
    private static bool ValidDeclaration(IMethodSymbol method, GeneratorDiagnostics diagnostics)
    {
        CancellationToken cancellationToken = diagnostics.CancellationToken;
        var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as MethodDeclarationSyntax;
        Location? name = method.Locations.FirstOrDefault();
        if (!method.IsStatic)
        {
            return Reject(s_static, name);
        }

        if (method.IsAsync)
        {
            return Reject(s_async, Modifier(SyntaxKind.AsyncKeyword));
        }

        if (method.IsGenericMethod)
        {
            return Reject(s_generic, syntax?.TypeParameterList?.GetLocation() ?? name);
        }

        if (method.IsAbstract)
        {
            return Reject(s_abstract, Modifier(SyntaxKind.AbstractKeyword));
        }

        if (!method.ReturnsVoid)
        {
            return Reject(s_result, FunctionDeclarationDiagnostics.Result(method, cancellationToken));
        }

        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            return Reject(s_access, name);
        }

        ParameterSyntax? parameter = syntax?.ParameterList.Parameters.Count == 1 ? syntax.ParameterList.Parameters[0] : null;
        if (method.Parameters.Length != 1 || method.Parameters[0].Type is not INamedTypeSymbol { Name: "PgBencher", Arity: 0 } bencher ||
            bencher.ContainingNamespace.ToDisplayString() != "Ankus")
        {
            return Reject(s_parameter, method.Parameters.Length == 1
                ? parameter?.Type?.GetLocation() ?? method.Parameters[0].Locations.FirstOrDefault()
                : syntax?.ParameterList.GetLocation() ?? name);
        }

        if (method.Parameters[0].RefKind != RefKind.None)
        {
            return Reject(s_reference, parameter?.GetLocation() ?? method.Parameters[0].Locations.FirstOrDefault());
        }

        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.TypeKind != TypeKind.Class || type.IsGenericType || type.IsFileLocal ||
                type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                diagnostics.Report(s_container, type.Locations.FirstOrDefault(), type.Name, method.Name);
                return false;
            }
        }

        return true;

        bool Reject(DiagnosticDescriptor descriptor, Location? location)
        {
            diagnostics.Report(descriptor, location ?? name, method.Name);
            return false;
        }

        Location? Modifier(SyntaxKind kind)
            => syntax?.Modifiers.FirstOrDefault(token => token.IsKind(kind)) is { RawKind: not 0 } token ? token.GetLocation() : name;
    }

    /// <summary>
    /// Finds the first way a setup method breaks the shape the runner calls.
    /// </summary>
    /// <param name="setup">The named setup method.</param>
    /// <returns>The problem the diagnostic states, or null when the runner can call it.</returns>
    private static string? SetupProblem(IMethodSymbol setup)
    {
        string name = $"'{setup.Name}'";
        if (!setup.IsStatic)
        {
            return name + " is not static";
        }

        if (setup.IsAsync)
        {
            return name + " is async";
        }

        if (setup.IsGenericMethod)
        {
            return name + " is generic";
        }

        if (setup.IsAbstract)
        {
            return name + " has no body the runner can call";
        }

        if (!setup.ReturnsVoid)
        {
            return $"{name} returns {setup.ReturnType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}";
        }

        if (setup.Parameters.Length != 0)
        {
            return $"{name} takes {setup.Parameters.Length} parameter{(setup.Parameters.Length == 1 ? string.Empty : "s")}";
        }

        return setup.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal
            ? null
            : name + " is not public or internal";
    }

    private static string Target(IMethodSymbol method)
        => method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".@" + method.Name;

    private static Location? BenchmarkOption(AttributeData attribute, string name, IMethodSymbol method)
    {
        SyntaxNode? syntax = attribute.ApplicationSyntaxReference?.GetSyntax();
        AttributeArgumentSyntax? argument = syntax?.DescendantNodes().OfType<AttributeArgumentSyntax>()
            .FirstOrDefault(value => value.NameEquals?.Name.Identifier.ValueText == name);
        return argument?.Expression.GetLocation() ?? method.Locations.FirstOrDefault();
    }

    /// <summary>
    /// Contains one validated benchmark independent of compiler symbols and physical source lines.
    /// </summary>
    internal sealed record Model(
        string Display,
        string Target,
        string? SetupTarget,
        string? SetupName,
        string RunName,
        string DescribeName,
        string RunCallback,
        string DescribeCallback,
        int Transaction,
        int SampleSize,
        int MeasurementTimeMilliseconds,
        int WarmupTimeMilliseconds,
        int ResampleCount,
        double NoiseThreshold,
        double SignificanceLevel,
        int Throughput,
        long ThroughputPerIteration);

    /// <summary>
    /// Retains one benchmark and its separately located diagnostics.
    /// </summary>
    internal sealed record Output(DeclarationIdentity Identity, Model? Model, EquatableArray<GeneratorProblem> Problems, GeneratorLocation? Location);
}
