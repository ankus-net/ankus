using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Native callback property and handler failures identify the independently correctable authored cause.
    /// </summary>
    /// <param name="property">The complete callback property declaration.</param>
    /// <param name="handler">The named handler declaration.</param>
    /// <param name="expected">The independent precise contract error.</param>
    /// <param name="highlight">The exact authored token or expression requiring correction.</param>
    [TestMethod]
    [DataRow("public partial Hook Callback { get; }", "private static long Handle(int first, long second) => second;", "ANKUS312", "Callback")]
    [DataRow("public static Hook Callback => default;", "private static long Handle(int first, long second) => second;", "ANKUS317", "Callback")]
    [DataRow("public static partial Hook Callback { get; set; }", "private static long Handle(int first, long second) => second;", "ANKUS316", "set")]
    [DataRow("public static partial int Callback { get; }", "private static long Handle(int first, long second) => second;", "ANKUS323", "int")]
    [DataRow("public static partial Hook Callback { get; }", "private long Handle(int first, long second) => second;", "ANKUS332", "Handle")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle<T>(int first, long second) => second;", "ANKUS337", "Handle")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(ref int first, long second) => second;", "ANKUS341", "first")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(in int first, long second) => second;", "ANKUS341", "first")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(out int first, long second) { first = 0; return second; }", "ANKUS341", "first")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(long first, long second) => second;", "ANKUS346", "first")]
    [DataRow("public static partial Hook Callback { get; }", "private static int Handle(int first, long second) => first;", "ANKUS344", "int")]
    [DataRow("public static partial Hook Callback { get; }", "private static async System.Threading.Tasks.Task<long> Handle(int first, long second) { await System.Threading.Tasks.Task.Yield(); return second; }", "ANKUS335", "async")]
    [DataRow("public static partial Hook Callback { get; }", "private static partial long Handle(int first, long second);", "ANKUS336", "Handle")]
    [DataRow("public static partial Hook Callback { get; }", "[System.Runtime.InteropServices.UnmanagedCallersOnly] private static long Handle(int first, long second) => second;", "ANKUS343", "System.Runtime.InteropServices.UnmanagedCallersOnly")]
    [DataRow("public static partial Hook Callback { get; }", "private static extern long Handle(int first, long second);", "ANKUS334", "extern")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(int first, long second, __arglist) => second;", "ANKUS338", "Handle")]
    [DataRow("public static partial Hook Callback { get; }", "private static decimal Handle(int first, long second) => second;", "ANKUS340", "decimal")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(string first, long second) => second;", "ANKUS342", "first")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(int first) => first;", "ANKUS345", "Handle")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(int first, long second, int third) => second;", "ANKUS345", "Handle")]
    [DataRow("[Ankus.PgNativeCallback(\"Handle\")] public static partial Hook Callback { get; }", "private static long Handle(int first, long second) => second;", "ANKUS310", "Callback")]
    [DataRow("[Ankus.PgGucInt(\"callback.limit\", 1, \"limit\")] public static partial Hook Callback { get; }", "private static long Handle(int first, long second) => second;", "ANKUS311", "Ankus.PgGucInt(\"callback.limit\", 1, \"limit\")")]
    [DataRow("public static partial ref Hook Callback { get; }", "private static long Handle(int first, long second) => second;", "ANKUS313", "ref Hook")]
    [DataRow("public static partial Hook Callback { set; }", "private static long Handle(int first, long second) => second;", "ANKUS315", "Callback")]
    [DataRow("public static partial Hook Callback { get; init; }", "private static long Handle(int first, long second) => second;", "ANKUS316", "init")]
    [DataRow("public static partial Hook Callback { get; } public static partial Hook Callback => default;", "private static long Handle(int first, long second) => second;", "ANKUS318", "Callback")]
    [DataRow("public static partial Hook Callback { get; }", "private static ref long Handle(int first, long second) => throw new System.NotSupportedException();", "ANKUS339", "ref long")]
    [DataRow("public static partial Hook Callback { get; }", "private static ref readonly long Handle(int first, long second) => throw new System.NotSupportedException();", "ANKUS339", "ref readonly long")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(int first, long second) => second; private static long Handle(int first, long second) => second;", "ANKUS329", "\"Handle\"")]
    [DataRow("public static partial Hook Callback { get; }", "private static long Handle(long first, long second) => second; private static int Handle(int first, long second) => first;", "ANKUS330", "\"Handle\"")]
    public void NativeCallbackFailuresHaveSpecificDiagnostics(string property, string handler, string expected, string highlight)
    {
        string source = CallbackTypeSource + "public partial class Functions { [Ankus.PgNativeCallback(\"Handle\")] " + property + " " + handler + " }";
        AssertNativeCallbackFailure(ModuleCompilation(source), expected, highlight);
    }

    /// <summary>
    /// Invalid callback ownership points to the actual incomplete, generic or inaccessible-from-other-files container.
    /// </summary>
    /// <param name="container">The complete containing declaration.</param>
    /// <param name="expected">The precise ownership contract.</param>
    /// <param name="highlight">The containing type needing correction.</param>
    [TestMethod]
    [DataRow("public class Functions", "ANKUS322", "Functions")]
    [DataRow("public partial class Functions<T>", "ANKUS320", "Functions")]
    [DataRow("file partial class Functions", "ANKUS321", "Functions")]
    [DataRow("public partial interface Functions", "ANKUS319", "Functions")]
    public void NativeCallbackContainerFailuresHaveSpecificDiagnostics(string container, string expected, string highlight)
    {
        string source = CallbackTypeSource + container + " { [Ankus.PgNativeCallback(\"Handle\")] public static partial Hook Callback { get; } private static long Handle(int first, long second) => second; }";
        AssertNativeCallbackFailure(ModuleCompilation(source), expected, highlight);
    }

    /// <summary>
    /// Pointer metadata distinguishes its prototype, fixed invocation and native-address construction requirements.
    /// </summary>
    /// <param name="original">The valid source fragment.</param>
    /// <param name="replacement">Its invalid replacement.</param>
    /// <param name="expected">The independently required pointer diagnostic.</param>
    [TestMethod]
    [DataRow("[Ankus.CompilerServices.NativeFunctionPointer(7)]", "", "ANKUS324")]
    [DataRow("[Ankus.CompilerServices.NativeFunctionPointer(7)]", "[Ankus.CompilerServices.NativeFunctionPointer(-1)]", "ANKUS324")]
    [DataRow("[Ankus.CompilerServices.NativeFunctionPointer(7)]", "[Ankus.CompilerServices.NativeFunctionPointer(7), Ankus.CompilerServices.NativeFunctionPointer(8)]", "ANKUS324")]
    [DataRow("Hook(void* address)", "Hook(nint address)", "ANKUS326")]
    [DataRow("Hook(void* address)", "Hook(int* address)", "ANKUS326")]
    [DataRow("public long Invoke(int first, long second)", "private long Invoke(int first, long second)", "ANKUS325")]
    [DataRow("public long Invoke(int first, long second)", "public static long Invoke(int first, long second)", "ANKUS325")]
    [DataRow("public long Invoke(int first, long second)", "public long Invoke<T>(int first, long second)", "ANKUS325")]
    [DataRow("public long Invoke(int first, long second)", "public long Invoke(ref int first, long second)", "ANKUS325")]
    [DataRow("public long Invoke(int first, long second)", "public long Invoke(int first, long second, __arglist)", "ANKUS325")]
    [DataRow("public long Invoke(int first, long second)", "public long Invoke() => 0; public long Invoke(int first, long second)", "ANKUS325")]
    [DataRow("public long Invoke(int first, long second)", "public decimal Invoke(int first, long second)", "ANKUS325")]
    public void NativeCallbackPointerFailuresHaveSpecificDiagnostics(string original, string replacement, string expected)
    {
        string source = CallbackTypeSource.Replace(original, replacement, StringComparison.Ordinal) +
            "public static partial class Functions { [Ankus.PgNativeCallback(\"Handle\")] public static partial Hook Callback { get; } private static long Handle(int first, long second) => second; }";
        AssertNativeCallbackFailure(ModuleCompilation(source), expected, "Hook");
    }

    /// <summary>
    /// Invalid and absent names point at the explicit attribute expression rather than the callback property.
    /// </summary>
    /// <param name="expression">The authored name expression.</param>
    /// <param name="expected">The name or absent-method contract.</param>
    [TestMethod]
    [DataRow("null", "ANKUS327")]
    [DataRow("\"\"", "ANKUS327")]
    [DataRow("\" \"", "ANKUS327")]
    [DataRow("\"Absent\"", "ANKUS328")]
    public void NativeCallbackHandlerNameFailuresHaveSpecificDiagnostics(string expression, string expected)
    {
        string source = CallbackTypeSource + "public static partial class Functions { [Ankus.PgNativeCallback(" + expression + ")] public static partial Hook Callback { get; } }";
        AssertNativeCallbackFailure(ModuleCompilation(source), expected, expression);
    }

    /// <summary>
    /// A native callback cannot target a constructor or an indexed property, even when its pointer prototype is complete.
    /// </summary>
    /// <param name="declaration">The complete attributed property and related member declaration.</param>
    /// <param name="expected">The independently required callable-shape diagnostic.</param>
    /// <param name="highlight">The authored member needing correction.</param>
    [TestMethod]
    [DataRow("[Ankus.PgNativeCallback(\".ctor\")] public static partial Hook Callback { get; } public Functions() { }", "ANKUS331", "Functions")]
    [DataRow("[Ankus.PgNativeCallback(\"Handle\")] public partial Hook this[int index] { get; } private static long Handle(int first, long second) => second;", "ANKUS314", "this")]
    [DataRow("public static partial Hook Callback { get; } private static long Handle(int first, long second) => second;", "ANKUS310", "Callback")]
    public void NativeCallbackMemberFailuresHaveSpecificDiagnostics(string declaration, string expected, string highlight)
    {
        AssertNativeCallbackFailure(ModuleCompilation(CallbackTypeSource + "public partial class Functions { " + declaration + " }"), expected, highlight);
    }

    /// <summary>
    /// An existing partial implementation is attributed to the actual authored implementation rather than its definition.
    /// </summary>
    [TestMethod]
    public void NativeCallbackImplementationDiagnosticIdentifiesAuthoredPart()
    {
        const string implementation = "public static partial Hook Callback => default;";
        string source = CallbackTypeSource + "public static partial class Functions { [Ankus.PgNativeCallback(\"Handle\")] public static partial Hook Callback { get; } " +
            implementation + " private static long Handle(int first, long second) => second; }";
        Diagnostic error = AssertNativeCallbackFailure(ModuleCompilation(source), "ANKUS318", "Callback");
        Assert.AreEqual(source.IndexOf(implementation, StringComparison.Ordinal) + implementation.IndexOf("Callback", StringComparison.Ordinal), error.Location.SourceSpan.Start);
    }

    /// <summary>
    /// Reads the actual property symbol, invokes its real validator and checks the exact independently expected contract.
    /// </summary>
    /// <param name="compilation">The complete callback source compilation.</param>
    /// <param name="expected">The required diagnostic identity.</param>
    /// <param name="highlight">The exact authored cause.</param>
    /// <returns>The independently verified diagnostic for further source-boundary checks.</returns>
    private Diagnostic AssertNativeCallbackFailure(CSharpCompilation compilation, string expected, string highlight)
    {
        INamedTypeSymbol owner = Assert.ContainsSingle(compilation.Assembly.GlobalNamespace.GetTypeMembers("Functions"));
        IPropertySymbol property = Assert.ContainsSingle(owner.GetMembers().OfType<IPropertySymbol>());
        var errors = new List<Diagnostic>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            errors.Add(Diagnostic.Create(descriptor, location, arguments)), context.CancellationToken);
        Assert.IsNull(NativeCallbackDeclaration.Create(property, diagnostics));
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(highlight, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains(property.Name, error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/raw-values/#native-callback-declaration-diagnostics", error.Descriptor.HelpLinkUri);
        return error;
    }
}
