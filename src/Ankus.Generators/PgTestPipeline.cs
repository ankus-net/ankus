using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates backend-test analysis, host discovery catalogs, and native boundaries into comparable contracts.
/// </summary>
internal static class PgTestPipeline
{
    /// <summary>
    /// Registers independent rendering for test metadata and native execution without evaluating author code.
    /// </summary>
    /// <param name="context">The incremental generator registration context.</param>
    /// <returns>The current diagnostics and independently cached catalog and boundary artifacts.</returns>
    internal static IncrementalValueProvider<Output> Register(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<Analysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Ankus.PgTestAttribute", static (node, _) => node is MethodDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token)).WithTrackingName("PgTestAnalysis");
        IncrementalValuesProvider<FunctionPipeline.FunctionModel?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName("PgTestModel");
        IncrementalValuesProvider<FunctionEmission?> emission = models.Select(static (value, _) => value is null ? null :
            PgFunctionEmitter.EmitBoundary(value.Invocation, [.. value.Parameters], value.Result!, value.Callback))
            .WithTrackingName("PgTestEmission");
        IncrementalValuesProvider<FunctionSqlModel?> sqlModels = analysis.Select(static (value, _) => value.Declaration is null ? null :
            FunctionSqlModel.Create(value.Declaration.Function, value.Model!.Result, null,
                value.Model.Callback.Replace("ankus_managed_", "ankus_fn_"))).WithTrackingName("PgTestSqlModel");
        IncrementalValuesProvider<FunctionSqlEmission?> sql = sqlModels.Select(static (value, _) => value is null ? null :
            FunctionSqlEmission.Create(value)).WithTrackingName("PgTestSqlEmission");
        IncrementalValuesProvider<PgTestDeclaration> declarations = analysis.Where(static value => value.Declaration is not null)
            .Select(static (value, _) => value.Declaration!);
        IncrementalValuesProvider<PgTestCatalogModel> catalogs = declarations.Collect().SelectMany(static (values, _) =>
            values.OrderBy(static value => value.Order, StringComparer.Ordinal).GroupBy(static value => value.Owner)
                .Select(static group => new PgTestCatalogModel(group.Key, new(group.Select(static value => value.Case)))).ToImmutableArray())
            .WithTrackingName("PgTestCatalogModel");
        IncrementalValuesProvider<CatalogEmission> catalogEmission = catalogs.Select(static (value, _) =>
            new CatalogEmission(value.Container.Managed, PgTestDeclaration.EmitCatalog(value))).WithTrackingName("PgTestCatalogEmission");
        return analysis.Collect().Combine(emission.Collect()).Combine(sql.Collect()).Combine(catalogEmission.Collect())
            .Select(static (value, _) => new Output(new(value.Left.Left.Left.Select((item, index) =>
                new TestOutput(item, value.Left.Left.Right[index], value.Left.Right[index]))), new(value.Right)));
    }

    /// <summary>
    /// Validates one test while all compiler objects remain transient.
    /// </summary>
    private static Analysis Analyze(GeneratorAttributeSyntaxContext attribute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = (IMethodSymbol)attribute.TargetSymbol;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, attribute.SemanticModel.Compilation), new(arguments))), cancellationToken);
        PgTestDeclaration? declaration = PgTestDeclaration.Create(method, diagnostics);
        FunctionPipeline.FunctionModel? model = null;
        if (declaration is not null)
        {
            SetResult? set = SetResult.Create(method, diagnostics, out bool validSet);
            if (validSet && SqlTypeReference.Validate(method, ref set, diagnostics) && NumericConstraint.Validate(method, diagnostics))
            {
                model = new(new(FunctionParameter.Create(method)), FunctionType.CreateResult(method), null,
                    MethodInvocation.Create(method), PgFunctionGenerator.GetCallbackName(method, declaration.Case.FunctionName));
            }
            else
            {
                declaration = null;
            }
        }

        return new(DeclarationIdentity.Create(method), declaration, model, new(problems),
            GeneratorLocation.Create(method.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
    }

    /// <summary>
    /// Keeps current diagnostic coordinates outside both rendering cache contracts.
    /// </summary>
    /// <param name="Identity">The assembly-qualified method identity.</param>
    /// <param name="Declaration">The validated discovery and SQL contracts, or null after a validation failure.</param>
    /// <param name="Model">The detached invocation model, or null after validation failure.</param>
    /// <param name="Problems">The independently located validation diagnostics.</param>
    /// <param name="Location">The current declaration coordinates used for canonical selection.</param>
    internal sealed record Analysis(DeclarationIdentity Identity, PgTestDeclaration? Declaration, FunctionPipeline.FunctionModel? Model,
        EquatableArray<GeneratorProblem> Problems, GeneratorLocation? Location);

    /// <summary>
    /// Supplies one current test and its independently rendered native boundary and SQL definition.
    /// </summary>
    /// <param name="Analysis">The semantic contracts and current diagnostic coordinates.</param>
    /// <param name="Emission">The cached managed/native boundary, or null after validation failure.</param>
    /// <param name="Sql">The cached SQL fragments, or null after validation failure.</param>
    internal sealed record TestOutput(Analysis Analysis, FunctionEmission? Emission, FunctionSqlEmission? Sql);

    /// <summary>
    /// Retains one rendered catalog independently of other owners and native test inclusion.
    /// </summary>
    /// <param name="Owner">The qualified catalog owner.</param>
    /// <param name="Source">The complete owner's discovery fragment.</param>
    internal sealed record CatalogEmission(string Owner, string Source);

    /// <summary>
    /// Composes the current inventory without making catalog rendering depend on native inclusion.
    /// </summary>
    /// <param name="Tests">The analyzed test methods and cached native artifacts.</param>
    /// <param name="Catalogs">The independently rendered host discovery catalogs.</param>
    internal sealed record Output(EquatableArray<TestOutput> Tests, EquatableArray<CatalogEmission> Catalogs);
}
