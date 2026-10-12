using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Separates per-provider custom scan validation from the extension's native provider table.
/// </summary>
internal static class CustomScanPipeline
{
    /// <summary>
    /// Registers detached provider analysis.
    /// </summary>
    /// <param name="context">The generator registration context.</param>
    /// <returns>The detached diagnostics and invocation contracts.</returns>
    internal static IncrementalValueProvider<EquatableArray<Analysis>> Register(IncrementalGeneratorInitializationContext context)
        => context.SyntaxProvider.ForAttributeWithMetadataName("Ankus.PgCustomScanAttribute", static (node, _) => node is ClassDeclarationSyntax,
            static (attribute, token) => Analyze(attribute, token)).WithTrackingName("CustomScanAnalysis").Collect()
            .Select(static (values, _) => new EquatableArray<Analysis>(values.Distinct()));

    /// <summary>
    /// Reports current diagnostics, rejects duplicate names and renders the selected providers.
    /// </summary>
    /// <param name="analyses">The independently analyzed providers.</param>
    /// <param name="compilation">The current diagnostic source trees.</param>
    /// <param name="context">The production diagnostic receiver.</param>
    /// <returns>The managed dispatchers and native provider table, or null when no valid provider is declared.</returns>
    internal static Emission? Select(EquatableArray<Analysis> analyses, GeneratorSourceResolver compilation, GeneratorDiagnostics context)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var selected = new List<CustomScanDeclaration>();
        foreach (Analysis analysis in analyses.OrderBy(static value => value.SortName, StringComparer.Ordinal))
        {
            foreach (GeneratorProblem problem in analysis.Problems)
            {
                problem.Report(compilation, context);
            }

            if (analysis.Model is not CustomScanDeclaration model)
            {
                continue;
            }

            if (names.TryGetValue(model.Name, out string? owner))
            {
                context.Report(CustomScanDiagnostics.Duplicate, analysis.Location?.Resolve(compilation), model.Name, owner);
                continue;
            }

            names.Add(model.Name, analysis.SortName);
            selected.Add(model);
        }

        if (selected.Count == 0)
        {
            return null;
        }

        var managed = new StringBuilder();
        var native = new StringBuilder();
        foreach (CustomScanDeclaration model in selected)
        {
            managed.Append(Managed(model));
            native.AppendLine($"extern int {model.Callback}(int, AnkusCustomScanRequest *, AnkusValue *, AnkusError *, AnkusExecute, AnkusMemoryApi *);");
        }

        native.AppendLine($"#define ANKUS_CUSTOM_SCAN_COUNT {selected.Count.ToString(CultureInfo.InvariantCulture)}");
        native.AppendLine("static const AnkusCustomScanProvider ankus_custom_scan_providers[ANKUS_CUSTOM_SCAN_COUNT] = {");
        foreach (CustomScanDeclaration model in selected)
        {
            // The capability bits are PostgreSQL's CUSTOMPATH_SUPPORT_BACKWARD_SCAN and CUSTOMPATH_SUPPORT_MARK_RESTORE.
            int flags = (model.Scroll ? 1 : 0) | (model.Mark ? 2 : 0);
            native.AppendLine($"    {{ \"{model.Name}\", {model.Callback}, {flags.ToString(CultureInfo.InvariantCulture)}, {(model.Shared is null ? "false" : "true")} }},");
        }

        native.AppendLine("};");
        native.AppendLine();
        return new(managed.ToString(), NativeCustomScanBridge.Declarations + native + NativeCustomScanBridge.Functions);
    }

    /// <summary>
    /// Renders a provider's exported dispatcher and the constrained calls that reach its static interface members.
    /// </summary>
    /// <param name="model">The provider contract.</param>
    /// <returns>Members of the extension's dispatcher class.</returns>
    private static string Managed(CustomScanDeclaration model)
    {
        string callback = model.Callback;
        string state = model.State;
        string rescan = model.Rescan ? $"&{callback}_rescan<{model.Provider}>" : "null";
        string explain = model.Explain ? $"&{callback}_explain<{model.Provider}>" : "null";
        string backward = model.Scroll ? $"&{callback}_read_backward<{model.Provider}>" : "null";
        string mark = model.Mark ? $"&{callback}_mark<{model.Provider}>" : "null";
        string restore = model.Mark ? $"&{callback}_restore<{model.Provider}>" : "null";
        string dispatch = model.Shared is null
            ? $"Dispatch<{state}>"
            : $"Dispatch<{state}, {model.Shared}>";
        string parallel = model.Shared is null
            ? "false"
            : $"&{callback}_initialize_shared<{model.Provider}>, &{callback}_attach_shared<{model.Provider}>";
        var source = new StringBuilder($$"""
                [global::System.Runtime.InteropServices.UnmanagedCallersOnly(
                    EntryPoint = "{{callback}}", CallConvs = [typeof(global::System.Runtime.CompilerServices.CallConvCdecl)])]
                private static int {{callback}}(int operation, global::Ankus.CompilerServices.NativeCustomScanRequest* request,
                    global::Ankus.CompilerServices.NativeValue* row, global::Ankus.CompilerServices.NativeCallError* error, nint execute, nint memory)
                    => global::Ankus.CompilerServices.NativeCustomScan.{{dispatch}}(operation, request, row, error, execute, memory,
                        &{{callback}}_plan<{{model.Provider}}>, &{{callback}}_begin<{{model.Provider}}>, &{{callback}}_read<{{model.Provider}}>,
                        {{rescan}}, {{explain}}, {{backward}}, {{mark}}, {{restore}}, {{parallel}});

                private static void {{callback}}_plan<T>(global::Ankus.PgCustomScanPlanner planner)
                    where T : global::Ankus.IPgCustomScan<{{state}}>
                    => T.Plan(planner);

                private static {{state}} {{callback}}_begin<T>(global::Ankus.PgCustomScanContext context)
                    where T : global::Ankus.IPgCustomScan<{{state}}>
                    => T.Begin(context);

                private static bool {{callback}}_read<T>({{state}} state, global::Ankus.PgHeapTuple row)
                    where T : global::Ankus.IPgCustomScan<{{state}}>
                    => T.Read(state, row);


            """);
        if (model.Rescan)
        {
            source.Append($$"""
                    private static void {{callback}}_rescan<T>({{state}} state)
                        where T : global::Ankus.IPgRescannableCustomScan<{{state}}>
                        => T.Rescan(state);


                """);
        }

        if (model.Explain)
        {
            source.Append($$"""
                    private static void {{callback}}_explain<T>({{state}} state, global::Ankus.PgExplainWriter explain)
                        where T : global::Ankus.IPgExplainedCustomScan<{{state}}>
                        => T.Explain(state, explain);


                """);
        }

        if (model.Scroll)
        {
            source.Append($$"""
                    private static bool {{callback}}_read_backward<T>({{state}} state, global::Ankus.PgHeapTuple row)
                        where T : global::Ankus.IPgScrollableCustomScan<{{state}}>
                        => T.ReadBackward(state, row);


                """);
        }

        if (model.Mark)
        {
            source.Append($$"""
                    private static void {{callback}}_mark<T>({{state}} state)
                        where T : global::Ankus.IPgMarkableCustomScan<{{state}}>
                        => T.Mark(state);

                    private static void {{callback}}_restore<T>({{state}} state)
                        where T : global::Ankus.IPgMarkableCustomScan<{{state}}>
                        => T.Restore(state);


                """);
        }

        if (model.Shared is string shared)
        {
            source.Append($$"""
                    private static void {{callback}}_initialize_shared<T>({{state}} state, ref {{shared}} memory)
                        where T : global::Ankus.IPgParallelCustomScan<{{state}}, {{shared}}>
                        => T.InitializeShared(state, ref memory);

                    private static void {{callback}}_attach_shared<T>({{state}} state, global::Ankus.PgCustomScanSharedMemory<{{shared}}> memory)
                        where T : global::Ankus.IPgParallelCustomScan<{{state}}, {{shared}}>
                        => T.AttachShared(state, memory);


                """);
        }

        return source.ToString();
    }

    /// <summary>
    /// Validates one provider and detaches current diagnostic coordinates before returning.
    /// </summary>
    private static Analysis Analyze(GeneratorAttributeSyntaxContext attribute, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var type = (INamedTypeSymbol)attribute.TargetSymbol;
        var problems = new List<GeneratorProblem>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            problems.Add(new(descriptor, GeneratorLocation.Create(location, attribute.SemanticModel.Compilation), new(arguments))), cancellationToken);
        return new(type.ToDisplayString(), CustomScanDeclaration.Create(type, attribute.Attributes[0], diagnostics), new(problems),
            GeneratorLocation.Create(type.Locations.FirstOrDefault(), attribute.SemanticModel.Compilation));
    }

    /// <summary>
    /// Retains a provider's contract separately from ordering and current diagnostic coordinates.
    /// </summary>
    /// <param name="SortName">The deterministic ordering key and display name.</param>
    /// <param name="Model">The detached invocation contract, or null after a validation error.</param>
    /// <param name="Problems">The original diagnostic contracts with detached source coordinates.</param>
    /// <param name="Location">The provider's current source coordinates.</param>
    internal sealed record Analysis(string SortName, CustomScanDeclaration? Model, EquatableArray<GeneratorProblem> Problems,
        GeneratorLocation? Location);

    /// <summary>
    /// Holds the selected providers' rendered sources.
    /// </summary>
    /// <param name="Managed">The exported dispatchers, as members of the extension's dispatcher class.</param>
    /// <param name="Native">The bridge declarations, provider table and functions.</param>
    internal sealed record Emission(string Managed, string Native);
}
