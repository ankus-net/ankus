using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies callback diagnostic identity, exact authored location and unaffected callable siblings.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Each independently correctable row or event callback failure points at its own authored cause.
    /// </summary>
    /// <param name="source">The invalid callback declaration.</param>
    /// <param name="expected">Its dedicated diagnostic ID.</param>
    /// <param name="fragment">The exact highlighted source text.</param>
    [TestMethod]
    [DataRow("public class Functions { [Ankus.PgTrigger] public Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS208", "Audit")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static async System.Threading.Tasks.Task Audit(Ankus.PgTriggerContext context) { await System.Threading.Tasks.Task.Yield(); } }", "ANKUS209", "async")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit\u003CT\u003E(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS210", "\u003CT\u003E")]
    [DataRow("public interface Functions { [Ankus.PgTrigger] static abstract Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context); }", "ANKUS211", "abstract")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static ref int Audit(Ankus.PgTriggerContext context) =\u003E throw new System.InvalidOperationException(); }", "ANKUS212", "ref int")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static ref readonly int Audit(Ankus.PgTriggerContext context) =\u003E throw new System.InvalidOperationException(); }", "ANKUS212", "ref readonly int")]
    [DataRow("public class Functions { [Ankus.PgTrigger] private static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS213", "private")]
    [DataRow("public class Functions { [Ankus.PgTrigger] protected static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS213", "protected")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static int Audit(Ankus.PgTriggerContext context) =\u003E 1; }", "ANKUS214", "int")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit() =\u003E null; }", "ANKUS215", "()")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context, int extra) =\u003E null; }", "ANKUS215", "(Ankus.PgTriggerContext context, int extra)")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(ref Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS216", "ref")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(in Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS216", "in")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(out Ankus.PgTriggerContext context) { context = null!; return null; } }", "ANKUS216", "out")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(params Ankus.PgTriggerContext[] context) =\u003E null; }", "ANKUS217", "params")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context = null!) =\u003E null; }", "ANKUS218", "= null!")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit([System.Runtime.InteropServices.Optional] Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS218", "System.Runtime.InteropServices.Optional")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext? context) =\u003E null; }", "ANKUS219", "Ankus.PgTriggerContext?")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(object context) =\u003E null; }", "ANKUS220", "object")]
    [DataRow("public class Functions\u003CT\u003E { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS221", "Functions")]
    [DataRow("file class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS222", "Functions")]
    [DataRow("public class Outer { private class Inner { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; } }", "ANKUS223", "Inner")]
    [DataRow("public class Functions { [Ankus.PgTrigger, Ankus.PgOperator(\u0022\u002B\u0022)] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS224", "Ankus.PgOperator(\u0022\u002B\u0022)")]
    [DataRow("public class Functions { [Ankus.PgTrigger] [return: Ankus.PgNumericPrecision(5, 2)] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS225", "Ankus.PgNumericPrecision(5, 2)")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit([Ankus.PgParameter(Name = \u0022arg\u0022)] Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS225", "Ankus.PgParameter(Name = \u0022arg\u0022)")]
    [DataRow("public class Functions { [Ankus.PgTrigger, Ankus.PgFunction(Rows = 1000)] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS226", "1000")]
    [DataRow("public class Functions { [Ankus.PgTrigger, Ankus.PgFunction(SetMode = Ankus.PgSetMode.Auto)] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) =\u003E null; }", "ANKUS227", "Ankus.PgSetMode.Auto")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static System.Threading.Tasks.Task Audit(Ankus.PgTriggerContext context) =\u003E System.Threading.Tasks.Task.CompletedTask; }", "ANKUS228", "System.Threading.Tasks.Task")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static System.Threading.Tasks.ValueTask Audit(Ankus.PgTriggerContext context) =\u003E default; }", "ANKUS228", "System.Threading.Tasks.ValueTask")]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static System.Collections.Generic.IAsyncEnumerable\u003Cint\u003E Audit(Ankus.PgTriggerContext context) =\u003E null!; }", "ANKUS229", "System.Collections.Generic.IAsyncEnumerable\u003Cint\u003E")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS208", "Audit")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static async System.Threading.Tasks.Task Audit(Ankus.PgEventTriggerContext context) { await System.Threading.Tasks.Task.Yield(); } }", "ANKUS209", "async")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit\u003CT\u003E(Ankus.PgEventTriggerContext context) { } }", "ANKUS210", "\u003CT\u003E")]
    [DataRow("public interface Functions { [Ankus.PgEventTrigger] static abstract void Audit(Ankus.PgEventTriggerContext context); }", "ANKUS211", "abstract")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static ref int Audit(Ankus.PgEventTriggerContext context) =\u003E throw new System.InvalidOperationException(); }", "ANKUS212", "ref int")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static ref readonly int Audit(Ankus.PgEventTriggerContext context) =\u003E throw new System.InvalidOperationException(); }", "ANKUS212", "ref readonly int")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] private static void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS213", "private")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] protected static void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS213", "protected")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static int Audit(Ankus.PgEventTriggerContext context) =\u003E 1; }", "ANKUS214", "int")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit() { } }", "ANKUS215", "()")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context, int extra) { } }", "ANKUS215", "(Ankus.PgEventTriggerContext context, int extra)")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(ref Ankus.PgEventTriggerContext context) { } }", "ANKUS216", "ref")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(in Ankus.PgEventTriggerContext context) { } }", "ANKUS216", "in")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(out Ankus.PgEventTriggerContext context) { context = null!;  } }", "ANKUS216", "out")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(params Ankus.PgEventTriggerContext[] context) { } }", "ANKUS217", "params")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context = null!) { } }", "ANKUS218", "= null!")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit([System.Runtime.InteropServices.Optional] Ankus.PgEventTriggerContext context) { } }", "ANKUS218", "System.Runtime.InteropServices.Optional")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext? context) { } }", "ANKUS219", "Ankus.PgEventTriggerContext?")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(object context) { } }", "ANKUS220", "object")]
    [DataRow("public class Functions\u003CT\u003E { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS221", "Functions")]
    [DataRow("file class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS222", "Functions")]
    [DataRow("public class Outer { private class Inner { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } } }", "ANKUS223", "Inner")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger, Ankus.PgOperator(\u0022\u002B\u0022)] public static void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS224", "Ankus.PgOperator(\u0022\u002B\u0022)")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] [return: Ankus.PgNumericPrecision(5, 2)] public static void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS225", "Ankus.PgNumericPrecision(5, 2)")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit([Ankus.PgParameter(Name = \u0022arg\u0022)] Ankus.PgEventTriggerContext context) { } }", "ANKUS225", "Ankus.PgParameter(Name = \u0022arg\u0022)")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger, Ankus.PgFunction(Rows = 1000)] public static void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS226", "1000")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger, Ankus.PgFunction(SetMode = Ankus.PgSetMode.Auto)] public static void Audit(Ankus.PgEventTriggerContext context) { } }", "ANKUS227", "Ankus.PgSetMode.Auto")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static System.Threading.Tasks.Task Audit(Ankus.PgEventTriggerContext context) =\u003E System.Threading.Tasks.Task.CompletedTask; }", "ANKUS228", "System.Threading.Tasks.Task")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static System.Threading.Tasks.ValueTask Audit(Ankus.PgEventTriggerContext context) =\u003E default; }", "ANKUS228", "System.Threading.Tasks.ValueTask")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static System.Collections.Generic.IAsyncEnumerable\u003Cint\u003E Audit(Ankus.PgEventTriggerContext context) =\u003E null!; }", "ANKUS229", "System.Collections.Generic.IAsyncEnumerable\u003Cint\u003E")]
    public void CallbackFailuresHaveSpecificDiagnostics(string source, string expected, string fragment)
    {
        const string Sibling = "public static class Other { [Ankus.PgFunction] public static int Value() => 7; }";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source + Sibling);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(expected, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(fragment, diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/triggers/#declaration-diagnostics", diagnostic.Descriptor.HelpLinkUri);
        Assert.Contains("'Audit'", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
    }

    /// <summary>
    /// Nullable row results and accessible concrete callback declarations remain callable.
    /// </summary>
    /// <param name="declaration">The valid callback.</param>
    [TestMethod]
    [DataRow("public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }")]
    [DataRow("public class Functions { [Ankus.PgTrigger] internal static Ankus.PgHeapTuple Audit(Ankus.PgTriggerContext context) => null!; }")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } }")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] protected internal static void Audit(Ankus.PgEventTriggerContext context) { } }")]
    public void CallbackDiagnosticControlsRemainCallable(string declaration)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(declaration);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
    }
}
