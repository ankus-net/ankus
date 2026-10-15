using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Resolves module identity into immutable values before rendering header-selected native metadata.
/// </summary>
internal static class NativeModuleMagic
{
    /// <summary>
    /// Identifies the independently correctable module name C-string boundary contract.
    /// </summary>
    private static readonly DiagnosticDescriptor s_nameZero = new(
        "ANKUS350", "Module name cannot contain zero characters",
        "Remove embedded zero characters from PgModule.Name or its project default; PostgreSQL requires the complete null-terminated value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/reference/build-settings/#module-identity-diagnostics");

    /// <summary>
    /// Identifies the independently correctable module name Unicode encoding contract.
    /// </summary>
    private static readonly DiagnosticDescriptor s_nameUnicode = new(
        "ANKUS351", "Module name requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in PgModule.Name or its project default; Ankus preserves exact UTF-8 text without replacement",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/reference/build-settings/#module-identity-diagnostics");

    /// <summary>
    /// Identifies the independently correctable module version C-string boundary contract.
    /// </summary>
    private static readonly DiagnosticDescriptor s_versionZero = new(
        "ANKUS352", "Module version cannot contain zero characters",
        "Remove embedded zero characters from PgModule.Version or its project default; PostgreSQL requires the complete null-terminated value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/reference/build-settings/#module-identity-diagnostics");

    /// <summary>
    /// Identifies the independently correctable module version Unicode encoding contract.
    /// </summary>
    private static readonly DiagnosticDescriptor s_versionUnicode = new(
        "ANKUS353", "Module version requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in PgModule.Version or its project default; Ankus preserves exact UTF-8 text without replacement",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/reference/build-settings/#module-identity-diagnostics");

    /// <summary>
    /// Registers semantic identity discovery, separately cached native emission and current-tree diagnostics.
    /// </summary>
    /// <param name="context">The generator's pipeline registration context.</param>
    /// <param name="projectVersion">The independently evaluated project version.</param>
    /// <returns>The explicit declaration flag and validated native metadata.</returns>
    internal static IncrementalValueProvider<ModuleOutput> Register(
        IncrementalGeneratorInitializationContext context, IncrementalValueProvider<string?> projectVersion)
    {
        IncrementalValueProvider<ModuleInput> input = context.CompilationProvider
            .Select(static (compilation, token) => Read(compilation, token))
            .WithTrackingName("ModuleInput");
        IncrementalValueProvider<ModuleAnalysis> analysis = input.Combine(projectVersion)
            .Select(static (value, _) => Analyze(value.Left, value.Right))
            .WithTrackingName("ModuleAnalysis");
        IncrementalValueProvider<ModuleIdentity?> identity = analysis
            .Select(static (value, _) => value.Identity).WithTrackingName("ModuleIdentity");
        IncrementalValueProvider<string?> source = identity
            .Select(static (value, _) => Emit(value)).WithTrackingName("ModuleMagic");
        return input.Select(static (value, _) => value.Declared).Combine(source).Combine(analysis)
            .Select(static (value, _) => new ModuleOutput(value.Left.Left, value.Left.Right, value.Right.NameError, value.Right.VersionError));
    }

    /// <summary>
    /// Reports module diagnostics only when the containing assembly declares an extension.
    /// </summary>
    /// <param name="module">The detached module metadata and validation results.</param>
    /// <param name="compilation">The current compilation receiving source diagnostics.</param>
    /// <param name="context">The generator's diagnostic destination.</param>
    internal static void Report(ModuleOutput module, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        Report(module.NameError, compilation, context);
        Report(module.VersionError, compilation, context);
    }

    /// <summary>
    /// Extracts only metadata and diagnostic coordinates from the compiler's assembly model.
    /// </summary>
    private static ModuleInput Read(Compilation compilation, CancellationToken cancellationToken)
    {
        AttributeData? attribute = compilation.Assembly.GetAttributes().FirstOrDefault(static value =>
            value.AttributeClass?.ToDisplayString() == "Ankus.PgModuleAttribute");
        AttributeSyntax? syntax = attribute?.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        return new(compilation.AssemblyName!, compilation.Assembly.Identity.Version.ToString(), attribute is not null,
            attribute?.NamedArguments.FirstOrDefault(static value => value.Key == "Name").Value.Value as string,
            attribute?.NamedArguments.FirstOrDefault(static value => value.Key == "Version").Value.Value as string,
            ReadLocation("Name"), ReadLocation("Version"));

        GeneratorLocation? ReadLocation(string property)
            => GeneratorLocation.Create(syntax?.ArgumentList?.Arguments
                .FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == property)?.Expression.GetLocation(), compilation);
    }

    /// <summary>
    /// Resolves defaults and rejects lossy native string values without emitting source.
    /// </summary>
    private static ModuleAnalysis Analyze(ModuleInput input, string? projectVersion)
    {
        string name = input.Name ?? input.AssemblyName;
        string version = input.Version ?? projectVersion ?? input.AssemblyVersion;
        ModuleProblem? nameError = SqlText.IsText(name) ? null : new("Name", name.Contains('\0'), input.NameLocation);
        ModuleProblem? versionError = SqlText.IsText(version) ? null : new("Version", version.Contains('\0'), input.VersionLocation);
        return new(nameError is null && versionError is null ? new(name, version) : null, nameError, versionError);
    }

    /// <summary>
    /// Uses extended identity on PostgreSQL 18 and later while preserving earlier ABI declarations.
    /// </summary>
    private static string? Emit(ModuleIdentity? identity)
        => identity is null ? null : "#if PG_VERSION_NUM >= 180000\nPG_MODULE_MAGIC_EXT(\n    .name = " + Literal(identity.Name) +
            ",\n    .version = " + Literal(identity.Version) + ");\n#else\nPG_MODULE_MAGIC;\n#endif\n\n";

    /// <summary>
    /// Reports an invalid value on the current source tree without retaining compiler state in cached data.
    /// </summary>
    private static void Report(ModuleProblem? problem, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        if (problem is not null)
        {
            DiagnosticDescriptor descriptor = problem.Property == "Name"
                ? problem.HasZero ? s_nameZero : s_nameUnicode
                : problem.HasZero ? s_versionZero : s_versionUnicode;
            context.Report(descriptor, problem.Location?.Resolve(compilation) ?? Location.None);
        }
    }

    /// <summary>
    /// Encodes every byte so C quoting, escapes and source-file encoding cannot alter identity.
    /// </summary>
    private static string Literal(string value)
        => "\"" + string.Concat(Encoding.UTF8.GetBytes(value)
            .Select(static item => "\\x" + item.ToString("x2", CultureInfo.InvariantCulture))) + "\"";

    /// <summary>
    /// Contains the complete assembly metadata inputs without symbols, syntax nodes or compilation references.
    /// </summary>
    private sealed record ModuleInput(string AssemblyName, string AssemblyVersion, bool Declared, string? Name, string? Version,
        GeneratorLocation? NameLocation, GeneratorLocation? VersionLocation);

    /// <summary>
    /// Contains only the resolved values that can change emitted module metadata.
    /// </summary>
    private sealed record ModuleIdentity(string Name, string Version);

    /// <summary>
    /// Identifies one invalid authored property and its detached source coordinates.
    /// </summary>
    internal sealed record ModuleProblem(string Property, bool HasZero, GeneratorLocation? Location);

    /// <summary>
    /// Keeps validated identity and reporting data independently of the compiler's object graph.
    /// </summary>
    private sealed record ModuleAnalysis(ModuleIdentity? Identity, ModuleProblem? NameError, ModuleProblem? VersionError);

    /// <summary>
    /// Carries cached native metadata and detached diagnostics into extension composition.
    /// </summary>
    /// <param name="Declared">Whether the assembly explicitly declares a PostgreSQL module.</param>
    /// <param name="Source">The rendered metadata, or null when invalid.</param>
    /// <param name="NameError">The optional invalid name diagnostic.</param>
    /// <param name="VersionError">The optional invalid version diagnostic.</param>
    internal sealed record ModuleOutput(bool Declared, string? Source, ModuleProblem? NameError, ModuleProblem? VersionError);
}
