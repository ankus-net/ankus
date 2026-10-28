using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Detaches row and event trigger validation, invocation and SQL contracts before independent rendering.
/// </summary>
internal static class TriggerPipeline
{
    /// <summary>
    /// Registers both callback families while preserving event-trigger precedence for invalid combined markers.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The independently validated and rendered callbacks.</returns>
    internal static IncrementalValueProvider<EquatableArray<TriggerOutput>> Register(IncrementalGeneratorInitializationContext context)
        => Register(context, false).Combine(Register(context, true)).Select(static (value, _) =>
            new EquatableArray<TriggerOutput>(value.Left.Concat(value.Right)));

    /// <summary>
    /// Registers signature analysis and separate boundary and SQL render stages for one family.
    /// </summary>
    private static IncrementalValueProvider<EquatableArray<TriggerOutput>> Register(IncrementalGeneratorInitializationContext context, bool eventTrigger)
    {
        string prefix = eventTrigger ? "EventTrigger" : "Trigger";
        IncrementalValuesProvider<TriggerAnalysis> analysis = context.SyntaxProvider.ForAttributeWithMetadataName(
            eventTrigger ? "Ankus.PgEventTriggerAttribute" : "Ankus.PgTriggerAttribute",
            static (node, _) => node is MethodDeclarationSyntax,
            (attribute, token) => Analyze(attribute, eventTrigger, token))
            .Where(static value => value is not null).Select(static (value, _) => value!)
            .WithTrackingName(prefix + "Analysis");
        IncrementalValuesProvider<TriggerModel?> models = analysis.Select(static (value, _) => value.Model)
            .WithTrackingName(prefix + "Model");
        IncrementalValuesProvider<FunctionEmission?> emission = models.Select(static (value, _) => value is null ? null :
            value.EventTrigger ? PgEventTriggerEmitter.EmitBoundary(value.Target, value.Callback) :
                PgTriggerEmitter.EmitBoundary(value.Target, value.Callback)).WithTrackingName(prefix + "Emission");
        IncrementalValuesProvider<FunctionDeclaration?> declarations = analysis.Select(static (value, _) => value.Declaration)
            .WithTrackingName(prefix + "Declaration");
        IncrementalValuesProvider<FunctionSqlModel?> sqlModels = analysis.Select(static (value, _) => value.Declaration is null ? null :
            new FunctionSqlModel(value.Declaration.TemplateName, value.Declaration.Replace, value.Declaration.Options, new([]), false,
                new([new FunctionSqlModel.Column(null, new SqlTypeTemplate(value.Model!.EventTrigger ? "event_trigger" : "trigger", false, null))]),
                value.Model.Callback.Replace("ankus_managed_", "ankus_fn_"))).WithTrackingName(prefix + "SqlModel");
        IncrementalValuesProvider<FunctionSqlEmission?> sql = sqlModels.Select(static (value, _) => value is null ? null :
            FunctionSqlEmission.Create(value)).WithTrackingName(prefix + "SqlEmission");
        return analysis.Collect().Combine(emission.Collect()).Combine(declarations.Collect()).Combine(sql.Collect()).Select(static (value, _) =>
            new EquatableArray<TriggerOutput>(value.Left.Left.Left.Select((item, index) =>
                new TriggerOutput(item, value.Left.Left.Right[index], value.Left.Right[index], value.Right[index]))));
    }

    /// <summary>
    /// Validates the existing signature and shared SQL rules while compiler objects remain confined to analysis.
    /// </summary>
    private static TriggerAnalysis? Analyze(GeneratorAttributeSyntaxContext attribute, bool eventTrigger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var method = (IMethodSymbol)attribute.TargetSymbol;
        if (!eventTrigger && EventTriggerDeclaration.IsEventTrigger(method))
        {
            return null;
        }

        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, attribute.SemanticModel.Compilation), new(arguments))), cancellationToken);
        bool valid = eventTrigger ? EventTriggerDeclaration.Validate(method, diagnostics) : TriggerDeclaration.Validate(method, diagnostics);
        string name = PgFunctionGenerator.GetSqlName(method);
        FunctionDeclaration? declaration = valid ? FunctionDeclaration.Create(method, name, diagnostics, contextParameter: true) : null;
        AttributeData? function = method.GetAttributes().FirstOrDefault(static value => value.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute");
        return new(DeclarationIdentity.Create(method), declaration is null ? null :
            new(eventTrigger, MethodInvocation.Create(method).Target, PgFunctionGenerator.GetCallbackName(method, name)), declaration,
            SqlDeclarationOptions.Read(function, attribute.SemanticModel.Compilation, cancellationToken), new(problems), GeneratorLocation.Create(method.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
    }

    /// <summary>
    /// Contains only invocation identities needed to render a trigger boundary.
    /// </summary>
    /// <param name="EventTrigger">Whether the callback handles database events; otherwise it handles rows.</param>
    /// <param name="Target">The fully qualified managed invocation target.</param>
    /// <param name="Callback">The assembly-specific callback identity.</param>
    internal sealed record TriggerModel(bool EventTrigger, string Target, string Callback);

    /// <summary>
    /// Separates callback and SQL values from current diagnostic coordinates and graph policies.
    /// </summary>
    /// <param name="Identity">The exact managed declaration identity.</param>
    /// <param name="Model">The validated invocation, or null after a validation error.</param>
    /// <param name="Declaration">The shared SQL contract, or null after a validation error.</param>
    /// <param name="Options">The optional detached SQL generation and dependency policy.</param>
    /// <param name="Problems">The original diagnostics with detached coordinates.</param>
    /// <param name="Location">The declaration coordinates distinguishing invalid duplicate signatures.</param>
    internal sealed record TriggerAnalysis(DeclarationIdentity Identity, TriggerModel? Model, FunctionDeclaration? Declaration,
        SqlDeclarationOptions? Options, EquatableArray<GeneratorProblem> Problems, GeneratorLocation? Location);

    /// <summary>
    /// Supplies one trigger analysis and its independently cached boundary and SQL fragments.
    /// </summary>
    /// <param name="Analysis">The detached semantic values and diagnostic coordinates.</param>
    /// <param name="Emission">The immutable boundary, or null after validation failed.</param>
    /// <param name="Declaration">The cached SQL declaration, or null after validation failed.</param>
    /// <param name="Sql">The independently cached SQL fragments, or null after validation failed.</param>
    internal sealed record TriggerOutput(TriggerAnalysis Analysis, FunctionEmission? Emission, FunctionDeclaration? Declaration, FunctionSqlEmission? Sql);
}
