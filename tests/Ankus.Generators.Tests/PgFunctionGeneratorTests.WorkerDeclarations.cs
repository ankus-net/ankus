using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies independently correctable worker failures without losing unrelated SQL output.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Covers separate signature, container, metadata and export partitions at their authored source.
    /// </summary>
    /// <returns>The declaration, precise diagnostic and exact highlighted source text.</returns>
    public static IEnumerable<(string Source, string Diagnostic, string Fragment)> WorkerDiagnosticCases()
    {
        (string Source, string Diagnostic, string Fragment)[] cases =
        [
            ("public class Functions { public void Other() { [Ankus.PgBackgroundWorker] static void Run(nuint value) { } Run(0); } }", "ANKUS250", "Ankus.PgBackgroundWorker"),
            ("public class Functions { public void Other() { System.Action<nuint> worker = [Ankus.PgBackgroundWorker] (nuint value) => { }; worker(0); } }", "ANKUS250", "Ankus.PgBackgroundWorker"),
            ("public interface IWorker { static abstract void Run(nuint value); } public class Functions : IWorker { [Ankus.PgBackgroundWorker] static void IWorker.Run(nuint value) { } }", "ANKUS250", "Ankus.PgBackgroundWorker"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public void Run(nuint value) { } }", "ANKUS251", "Run"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static async void Run(nuint value) { await System.Threading.Tasks.Task.Yield(); } }", "ANKUS252", "async"),
            ("public partial class Functions { [Ankus.PgBackgroundWorker] public static partial void Run(nuint value); public static async partial void Run(nuint value) { await System.Threading.Tasks.Task.Yield(); } }", "ANKUS252", "async"),
            ("public partial class Functions { public static partial void Run(nuint value); [Ankus.PgBackgroundWorker] public static async partial void Run(nuint value) { await System.Threading.Tasks.Task.Yield(); } }", "ANKUS252", "async"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run<T>(nuint value) { } }", "ANKUS253", "<T>"),
            ("public interface Functions { [Ankus.PgBackgroundWorker] static abstract void Run(nuint value); }", "ANKUS254", "abstract"),
            ("public interface Functions { [Ankus.PgBackgroundWorker] static virtual void Run(nuint value) { } }", "ANKUS255", "virtual"),
            ("public class Functions { [Ankus.PgBackgroundWorker, System.Runtime.InteropServices.DllImport(\"native\")] public static extern void Run(nuint value); }", "ANKUS256", "extern"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static int Run(nuint value) => 1; }", "ANKUS257", "int"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static System.Threading.Tasks.Task Run(nuint value) => System.Threading.Tasks.Task.CompletedTask; }", "ANKUS257", "System.Threading.Tasks.Task"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run() { } }", "ANKUS258", "()"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint one, nuint two) { } }", "ANKUS258", "(nuint one, nuint two)"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run(ref nuint value) { } }", "ANKUS259", "ref nuint value"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run(in nuint value) { } }", "ANKUS259", "in nuint value"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run(out nuint value) { value = 0; } }", "ANKUS259", "out nuint value"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run(ref readonly nuint value) { } }", "ANKUS259", "ref readonly nuint value"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run(long value) { } }", "ANKUS260", "long"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run(nint value) { } }", "ANKUS260", "nint"),
            ("public class Functions { [Ankus.PgBackgroundWorker] private static void Run(nuint value) { } }", "ANKUS261", "private"),
            ("public class Functions { [Ankus.PgBackgroundWorker] protected static void Run(nuint value) { } }", "ANKUS261", "protected"),
            ("public class Functions { [Ankus.PgBackgroundWorker] private protected static void Run(nuint value) { } }", "ANKUS261", "private"),
            ("public partial class Functions { [Ankus.PgBackgroundWorker] static partial void Run(nuint value); }", "ANKUS262", "Run"),
            ("public class Functions<T> { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } }", "ANKUS263", "Functions"),
            ("public class Outer<T> { public class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } } }", "ANKUS263", "Functions"),
            ("file class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } }", "ANKUS264", "Functions"),
            ("public class Outer { private class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } } }", "ANKUS265", "Functions"),
            ("public class Outer { private protected class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } } }", "ANKUS265", "Functions"),
            ("public class Functions { [Ankus.PgBackgroundWorker, System.Diagnostics.Conditional(\"DEBUG\")] public static void Run(nuint value) { } }", "ANKUS266", "System.Diagnostics.Conditional(\"DEBUG\")"),
            ("public class Functions { [Ankus.PgBackgroundWorker, System.Runtime.InteropServices.UnmanagedCallersOnly] public static void Run(nuint value) { } }", "ANKUS267", "System.Runtime.InteropServices.UnmanagedCallersOnly"),
            ("public class Functions { [Ankus.PgBackgroundWorker, Ankus.PgInitialize] public static void Run(nuint value) { } }", "ANKUS268", "Ankus.PgInitialize"),
            ("public class Functions { [Ankus.PgBackgroundWorker, Ankus.PgModuleLoad] public static void Run(nuint value) { } }", "ANKUS268", "Ankus.PgModuleLoad"),
            ("public class Functions { [Ankus.PgBackgroundWorker, Ankus.PgFunction] public static void Run(nuint value) { } }", "ANKUS269", "Ankus.PgFunction"),
            ("public class Functions { [Ankus.PgBackgroundWorker, Ankus.PgTrigger] public static void Run(nuint value) { } }", "ANKUS269", "Ankus.PgTrigger"),
            ("public class Functions { [Ankus.PgBackgroundWorker, Ankus.PgEventTrigger] public static void Run(nuint value) { } }", "ANKUS269", "Ankus.PgEventTrigger"),
            ("public class Functions { [Ankus.PgBackgroundWorker, Ankus.PgOperator(\"+\")] public static void Run(nuint value) { } }", "ANKUS269", "Ankus.PgOperator(\"+\")"),
            ("public class Functions { [Ankus.PgBackgroundWorker, Ankus.PgCast] public static void Run(nuint value) { } }", "ANKUS269", "Ankus.PgCast"),
            ("public class Functions { [Ankus.PgBackgroundWorker] [return: Ankus.PgNumericPrecision(5)] public static void Run(nuint value) { } }", "ANKUS270", "Ankus.PgNumericPrecision(5)"),
            ("public class Functions { [Ankus.PgBackgroundWorker] [return: Ankus.PgCompositeType(\"item\")] public static void Run(nuint value) { } }", "ANKUS270", "Ankus.PgCompositeType(\"item\")"),
            ("public class Functions { [Ankus.PgBackgroundWorker] [return: Ankus.PgColumnNames(\"item\")] public static void Run(nuint value) { } }", "ANKUS270", "Ankus.PgColumnNames(\"item\")"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run([Ankus.PgParameter(Name = \"item\")] nuint value) { } }", "ANKUS271", "Ankus.PgParameter(Name = \"item\")"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run([Ankus.PgNumericPrecision(5)] nuint value) { } }", "ANKUS271", "Ankus.PgNumericPrecision(5)"),
            ("public class Functions { [Ankus.PgBackgroundWorker] public static void Run([Ankus.PgCompositeType(\"item\")] nuint value) { } }", "ANKUS271", "Ankus.PgCompositeType(\"item\")"),
        ];
        foreach ((string source, string diagnostic, string fragment) in cases)
        {
            yield return (source, diagnostic, fragment);
        }

        foreach (string symbol in new[] { "", "1entry", "_entry", "é", "a-b", "a.b" })
        {
            yield return (Export(symbol), "ANKUS272", "\"" + symbol + "\"");
        }

        string longSymbol = new('a', 96);
        yield return (Export(longSymbol), "ANKUS273", "\"" + longSymbol + "\"");
        foreach (string symbol in new[] { "for", "Pg_magic_func", "_PG_init", "_PG_fini", "_PG_output_plugin_init", "ankus_worker", "pg_finfo_worker" })
        {
            yield return (Export(symbol), "ANKUS274", "\"" + symbol + "\"");
        }

        static string Export(string symbol) => "public class Functions { [Ankus.PgBackgroundWorker(EntryPoint = \"" + symbol +
            "\")] public static void Run(nuint value) { } }";
    }

    /// <summary>
    /// Invalid worker entries point at their exact cause while the unrelated SQL function still compiles.
    /// </summary>
    /// <param name="source">The invalid authored worker declaration.</param>
    /// <param name="expected">The independent diagnostic identity.</param>
    /// <param name="fragment">The exact expected highlighted text.</param>
    [TestMethod]
    [DynamicData(nameof(WorkerDiagnosticCases))]
    public void WorkerFailuresHaveSpecificDiagnostics(string source, string expected, string fragment)
    {
        const string Sibling = "public static class Other { [Ankus.PgFunction] public static int Value() => 7; }";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source + Sibling);
        Diagnostic error = Assert.ContainsSingle(diagnostics.Where(static diagnostic => diagnostic.Id is
            "ANKUS250" or "ANKUS251" or "ANKUS252" or "ANKUS253" or "ANKUS254" or "ANKUS255" or "ANKUS256" or
            "ANKUS257" or "ANKUS258" or "ANKUS259" or "ANKUS260" or "ANKUS261" or "ANKUS262" or "ANKUS263" or
            "ANKUS264" or "ANKUS265" or "ANKUS266" or "ANKUS267" or "ANKUS268" or "ANKUS269" or "ANKUS270" or
            "ANKUS271" or "ANKUS272" or "ANKUS273" or "ANKUS274" or "ANKUS275"));
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(fragment, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/background-workers/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.DoesNotContain("CS8785", diagnostics.Select(static diagnostic => diagnostic.Id));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
        Assert.DoesNotContain("Run", ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("value", InstallationBody(compilation));
    }

    /// <summary>
    /// Valid native unsigned arguments, accessibility, partial implementations and symbol bounds remain callable.
    /// </summary>
    /// <param name="source">The supported authored worker declaration.</param>
    /// <param name="entry">The expected native symbol.</param>
    [TestMethod]
    [DataRow("public class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } }", "Run")]
    [DataRow("public class Functions { [Ankus.PgBackgroundWorker] internal static void Run(nuint value) { } }", "Run")]
    [DataRow("public class Functions { [Ankus.PgBackgroundWorker] protected internal static void Run(System.UIntPtr value) { } }", "Run")]
    [DataRow("public partial class Functions { [Ankus.PgBackgroundWorker] public static partial void Run(nuint value); public static partial void Run(nuint value) { } }", "Run")]
    [DataRow("public class Functions { [Ankus.PgBackgroundWorker(EntryPoint = null)] public static void Run(nuint value) { } }", "Run")]
    [DataRow("public class Functions { [Ankus.PgBackgroundWorker(EntryPoint = \"A_0\")] public static void Run(nuint value) { } }", "A_0")]
    public void WorkerDiagnosticControlsRemainCallable(string source, string entry)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertInitializationCompilationSucceeds(compilation, diagnostics);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", entry], ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("PGDLLEXPORT void " + entry + "(Datum argument)", ManifestValue(compilation, "Ankus.NativeSource"));
    }

    /// <summary>
    /// Shared native identities reject the second entry while preserving the first worker and an unrelated SQL function.
    /// </summary>
    /// <param name="explicitEntry">Whether the collision uses explicit symbols or default method names.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WorkerDuplicatesHaveSpecificDiagnostics(bool explicitEntry)
    {
        string marker = explicitEntry ? "[Ankus.PgBackgroundWorker(EntryPoint = \"shared_entry\")]" : "[Ankus.PgBackgroundWorker]";
        string firstName = explicitEntry ? "Start" : "Run";
        string source = "public class First { " + marker + " public static void " + firstName + "(nuint value) { } } " +
            "public class Second { " + marker + " public static void Run(nuint value) { } } " +
            "public static class Other { [Ankus.PgFunction] public static int Value() => 7; }";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS275", error.Id);
        Assert.AreEqual("Run", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains(explicitEntry ? "'shared_entry'" : "'Run'", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/background-workers/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.ContainsSingle(ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(name => name == (explicitEntry ? "shared_entry" : "Run")));
        Assert.Contains("value", InstallationBody(compilation));
    }
}
