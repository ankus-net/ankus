using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies separately correctable initialization failures and valid phase controls.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Covers every invalid declaration partition independently for both initialization phases.
    /// </summary>
    /// <returns>The authored declaration, precise diagnostic and exact highlighted source text.</returns>
    public static IEnumerable<(string Source, string Diagnostic, string Fragment)> InitializerDiagnosticCases()
    {
        (string Source, string Diagnostic, string Fragment)[] cases =
        [
            ("public class Functions { public void Other() { [Ankus.__MARKER__] static void Start() { } Start(); } }", "ANKUS231", "Ankus.__MARKER__"),
            ("public class Functions { [Ankus.__MARKER__] public void Start() { } }", "ANKUS232", "Start"),
            ("public class Functions { [Ankus.__MARKER__] public static async void Start() { await System.Threading.Tasks.Task.Yield(); } }", "ANKUS233", "async"),
            ("public class Functions { [Ankus.__MARKER__] public static void Start<T>() { } }", "ANKUS234", "<T>"),
            ("public interface Functions { [Ankus.__MARKER__] static abstract void Start(); }", "ANKUS235", "abstract"),
            ("public interface Functions { [Ankus.__MARKER__] static virtual void Start() { } }", "ANKUS236", "virtual"),
            ("public class Functions { [Ankus.__MARKER__, System.Runtime.InteropServices.DllImport(\"native\")] public static extern void Start(); }", "ANKUS237", "extern"),
            ("public class Functions { [Ankus.__MARKER__] public static int Start() => 1; }", "ANKUS238", "int"),
            ("public class Functions { [Ankus.__MARKER__] public static void Start(int value) { } }", "ANKUS239", "(int value)"),
            ("public class Functions { [Ankus.__MARKER__] private static void Start() { } }", "ANKUS240", "private"),
            ("public class Functions { [Ankus.__MARKER__] protected static void Start() { } }", "ANKUS240", "protected"),
            ("public partial class Functions { [Ankus.__MARKER__] static partial void Start(); }", "ANKUS241", "Start"),
            ("public class Functions<T> { [Ankus.__MARKER__] public static void Start() { } }", "ANKUS242", "Functions"),
            ("file class Functions { [Ankus.__MARKER__] public static void Start() { } }", "ANKUS243", "Functions"),
            ("public class Outer { private class Functions { [Ankus.__MARKER__] public static void Start() { } } }", "ANKUS244", "Functions"),
            ("public class Functions { [Ankus.__MARKER__, System.Diagnostics.Conditional(\"DEBUG\")] public static void Start() { } }", "ANKUS245", "System.Diagnostics.Conditional(\"DEBUG\")"),
            ("public class Functions { [Ankus.__MARKER__, System.Runtime.InteropServices.UnmanagedCallersOnly] public static void Start() { } }", "ANKUS246", "System.Runtime.InteropServices.UnmanagedCallersOnly"),
            ("public class Functions { [Ankus.__MARKER__, Ankus.PgFunction] public static void Start() { } }", "ANKUS247", "Ankus.PgFunction"),
            ("public class Functions { [Ankus.__MARKER__] [return: Ankus.PgNumericPrecision(5)] public static void Start() { } }", "ANKUS248", "Ankus.PgNumericPrecision(5)"),
            ("public class Functions { [Ankus.__MARKER__, Ankus.__OTHER__] public static void Start() { } }", "ANKUS230", "Ankus.PgModuleLoad"),
        ];
        foreach (string marker in new[] { "PgInitialize", "PgModuleLoad" })
        {
            foreach ((string source, string diagnostic, string fragment) in cases)
            {
                yield return (source.Replace("__MARKER__", marker, StringComparison.Ordinal)
                    .Replace("__OTHER__", marker == "PgInitialize" ? "PgModuleLoad" : "PgInitialize", StringComparison.Ordinal),
                    diagnostic, fragment.Replace("__MARKER__", marker, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// Invalid callbacks identify their authored cause while an unrelated valid function remains callable.
    /// </summary>
    /// <param name="source">The invalid initialization declaration.</param>
    /// <param name="expected">The independently expected diagnostic.</param>
    /// <param name="fragment">The exact highlighted source.</param>
    [TestMethod]
    [DynamicData(nameof(InitializerDiagnosticCases))]
    public void InitializerFailuresHaveSpecificDiagnostics(string source, string expected, string fragment)
    {
        const string Sibling = "public static class Other { [Ankus.PgFunction] public static int Value() => 7; }";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source + Sibling);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(fragment, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/initialization/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.Contains("'Start'", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
        Assert.DoesNotContain("_PG_init", ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Accessible implemented callbacks retain the loader export for both phase markers.
    /// </summary>
    /// <param name="marker">The initialization phase.</param>
    /// <param name="access">The callable accessibility.</param>
    [TestMethod]
    [DataRow("PgInitialize", "public")]
    [DataRow("PgInitialize", "internal")]
    [DataRow("PgModuleLoad", "public")]
    [DataRow("PgModuleLoad", "internal")]
    public void InitializerDiagnosticControlsRemainCallable(string marker, string access)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { [Ankus." + marker + "] " +
            access + " static void Start() { } }");
        AssertInitializationCompilationSucceeds(compilation, diagnostics);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
    }

    /// <summary>
    /// A second callback diagnoses its phase without removing an unrelated valid SQL dispatcher.
    /// </summary>
    /// <param name="marker">The duplicated initialization phase.</param>
    [TestMethod]
    [DataRow("PgInitialize")]
    [DataRow("PgModuleLoad")]
    public void InitializerDuplicatesHaveSpecificDiagnostics(string marker)
    {
        string first = "public static class First { [Ankus." + marker + "] public static void Start() { } }";
        string second = "public static class Second { [Ankus." + marker + "] public static void Start() { } }";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(first + second +
            "public static class Other { [Ankus.PgFunction] public static int Value() => 7; }");
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS249", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual("An assembly can declare only one " + marker + " callback; 'Start' is an additional declaration", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("Start", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
        Assert.DoesNotContain("_PG_init", ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
