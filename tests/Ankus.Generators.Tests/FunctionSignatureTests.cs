using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies specific signature failures, exact authored locations and concrete repaired invocation contracts.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Invalid calling contracts have distinct actionable errors and cannot expose a native SQL entry.
    /// </summary>
    /// <param name="source">An otherwise valid C# declaration with one invalid SQL contract.</param>
    /// <param name="id">The exact signature diagnostic.</param>
    /// <param name="span">The authored cause highlighted by that diagnostic.</param>
    /// <param name="action">The required correction in the diagnostic message.</param>
    [TestMethod]
    [DataRow("public class Bad { [Ankus.PgFunction] public int Reject() => 42; }", "ANKUS033", "Reject", "declare a static entry method")]
    [DataRow("public class Bad { [Ankus.PgFunction] private static int Reject() => 42; }", "ANKUS034", "Reject", "accessible within the extension assembly")]
    [DataRow("public class Bad { [Ankus.PgFunction] protected static int Reject() => 42; }", "ANKUS034", "Reject", "accessible within the extension assembly")]
    [DataRow("public class Bad { [Ankus.PgFunction] private protected static int Reject() => 42; }", "ANKUS034", "Reject", "accessible within the extension assembly")]
    [DataRow("public class Outer { private class Bad { [Ankus.PgFunction] public static int Reject() => 42; } }", "ANKUS034", "Bad", "accessible within the extension assembly")]
    [DataRow("public class Bad<T> { [Ankus.PgFunction] public static int Reject() => 42; }", "ANKUS035", "<T>", "non-generic containing types")]
    [DataRow("public class Outer<T> { public class Bad { [Ankus.PgFunction] public static int Reject() => 42; } }", "ANKUS035", "<T>", "non-generic containing types")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject<T>() => 42; }", "ANKUS035", "<T>", "non-generic entry method")]
    [DataRow("public interface Bad { [Ankus.PgFunction] static abstract int Reject(); }", "ANKUS036", "abstract", "concrete static implementation")]
    [DataRow("public class Bad { private static int _value; [Ankus.PgFunction] public static ref int Reject() => ref _value; }", "ANKUS037", "ref int", "by value")]
    [DataRow("public class Bad { private static int _value; [Ankus.PgFunction] public static ref readonly int Reject() => ref _value; }", "ANKUS037", "ref readonly int", "by value")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject(ref int value) => value; }", "ANKUS038", "ref int value", "'ref'")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject(in int value) => value; }", "ANKUS038", "in int value", "'in'")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject(out int value) { value = 42; return value; } }", "ANKUS038", "out int value", "'out'")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject(ref readonly int value) => value; }", "ANKUS038", "ref readonly int value", "'ref readonly'")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject(ref Ankus.PgMemoryContext owner) => 42; }", "ANKUS038", "ref Ankus.PgMemoryContext owner", "by value")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static System.Uri Reject() => new(\"https://example.com\"); }", "ANKUS039", "System.Uri", "explicit datum mapping")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject(System.Uri value) => 42; }", "ANKUS040", "System.Uri", "Parameter 'value'")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject(params byte[] values) => 42; }", "ANKUS041", "params", "SQL array")]
    [DataRow("public class Bad { [Ankus.PgFunction] public static int Reject(params System.ReadOnlySpan<int> values) => 42; }", "ANKUS041", "params", "SQL array")]
    [DataRow("file class Bad { [Ankus.PgFunction] public static int Reject() => 42; }", "ANKUS043", "file", "public or internal type")]
    public void FunctionSignaturesHavePreciseDiagnostics(string source, string id, string span, string action)
    {
        CSharpCompilation initial = ModuleCompilation(source);
        Assert.IsEmpty(initial.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreSame(initial.SyntaxTrees.Single(), error.Location.SourceTree);
        Assert.AreEqual(span, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://willibrandon.github.io/ankus/getting-started/functions/#function-signatures", error.Descriptor.HelpLinkUri);
        Assert.Contains(action, error.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.AreSequenceEqual(["Pg_magic_func"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(output));

        RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            "public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }", path: "Module.cs",
            cancellationToken: context.CancellationToken)), out Compilation repaired);
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(repaired));
    }

    /// <summary>
    /// PostgreSQL's argument boundary counts SQL slots while retaining injected context calls.
    /// </summary>
    /// <param name="count">The number of actual SQL arguments.</param>
    /// <param name="injected">Whether native contexts surround those arguments.</param>
    [TestMethod]
    [DataRow(99, false)]
    [DataRow(100, false)]
    [DataRow(101, false)]
    [DataRow(99, true)]
    [DataRow(100, true)]
    [DataRow(101, true)]
    public void FunctionArgumentLimitCountsSqlSlots(int count, bool injected)
    {
        string[] parameters = [.. Enumerable.Range(0, count).Select(static index => "int value" + index.ToString(CultureInfo.InvariantCulture))];
        string[] arguments = [.. Enumerable.Range(0, count).Select(index => index == 0 || index == count - 1 ? "21" : "0")];
        string list = string.Join(", ", parameters);
        string call = string.Join(", ", arguments);
        if (injected)
        {
            list = "Ankus.PgMemoryContext owner, " + list + ", Ankus.PgFunctionContext invocation";
            call = "default!, " + call + ", default!";
        }

        CSharpCompilation initial = ModuleCompilation("public static class Functions { [Ankus.PgFunction] public static int Limit(" + list +
            ") => value0 + value" + (count - 1).ToString(CultureInfo.InvariantCulture) + "; public static int Answer() => Limit(" + call + "); }");
        Assert.IsEmpty(initial.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation output, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        if (count > 100)
        {
            Diagnostic error = Assert.ContainsSingle(diagnostics);
            Assert.AreEqual("ANKUS042", error.Id);
            Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
            Assert.AreEqual("(" + list + ")", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
            Assert.Contains("101 SQL arguments", error.GetMessage(CultureInfo.InvariantCulture));
            Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(output));
            return;
        }

        Assert.IsEmpty(diagnostics);
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
        string sql = InstallationBody(output);
        Assert.Contains("CREATE FUNCTION \"limit\"", sql);
        Assert.Contains("\"value0\" integer", sql);
        Assert.Contains("\"value" + (count - 1).ToString(CultureInfo.InvariantCulture) + "\" integer", sql);
        Assert.DoesNotContain("\"owner\"", sql);
        Assert.DoesNotContain("\"invocation\"", sql);
    }

    /// <summary>
    /// Legal access and variadic contracts still compile and execute concrete managed calls.
    /// </summary>
    /// <param name="declaration">The valid attributed method and its containing type.</param>
    /// <param name="call">The ordinary C# call used to verify that generated source remains callable.</param>
    [TestMethod]
    [DataRow("public static class Bad { [Ankus.PgFunction] public static int Read() => 42; }", "Bad.Read()")]
    [DataRow("internal static class Bad { [Ankus.PgFunction] internal static int Read() => 42; }", "Bad.Read()")]
    [DataRow("public class Outer { internal class Bad { [Ankus.PgFunction] internal static int Read() => 42; } }", "Outer.Bad.Read()")]
    [DataRow("public class Bad { [Ankus.PgFunction] protected internal static int Read() => 42; }", "Bad.Read()")]
    [DataRow("public class Outer { protected internal class Bad { [Ankus.PgFunction] internal static int Read() => 42; } }", "Outer.Bad.Read()")]
    [DataRow("public class Outer { protected internal class Bad { [Ankus.PgFunction] protected internal static int Read() => 42; } }", "Outer.Bad.Read()")]
    [DataRow("public static class Bad { [Ankus.PgFunction] public static int Read(params int[] values) => values[0]; }", "Bad.Read(42)")]
    [DataRow("public static class Bad { [Ankus.PgFunction] public static int Read(params byte[][] values) => values[0][0]; }", "Bad.Read(new byte[] { 42 })")]
    public void CallableSignaturesRetainCompiledBehavior(string declaration, string call)
    {
        CSharpCompilation initial = ModuleCompilation(declaration + " public static class Functions { public static int Answer() => " + call + "; }");
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(initial));
        RunModule(ModuleDriver(), initial, out Compilation output);

        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
        Assert.Contains("CREATE FUNCTION \"read\"", InstallationBody(output));
        AssertSignatureExports(output, "read");
    }

    /// <summary>
    /// Cached signature problems follow current mapped trees and disappear after a valid repair.
    /// </summary>
    /// <param name="method">The invalid calling or SQL conversion contract.</param>
    [TestMethod]
    [DataRow("public int Reject() => 42;")]
    [DataRow("public static System.Uri Reject() => new(\"https://example.com\");")]
    [DataRow("public static int Reject(ref int value) => value;")]
    public void FunctionSignatureDiagnosticsFollowCurrentTreesAndRecover(string method)
    {
        string source = "public class Bad { [Ankus.PgFunction] " + method + " }";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> previous, context.CancellationToken);
        Diagnostic original = Assert.ContainsSingle(previous);
        SyntaxTree current = CSharpSyntaxTree.ParseText(source + "\n// unrelated edit", path: "Module.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(original.Id, error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "FunctionAnalysis"));
        SyntaxTree mapped = CSharpSyntaxTree.ParseText("#line 76 \"Authored.cs\"\n" + source, path: "Moved.cs", cancellationToken: context.CancellationToken);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited.ReplaceSyntaxTree(current, mapped), out _, out diagnostics, context.CancellationToken);
        error = Assert.ContainsSingle(diagnostics);
        Assert.AreSame(mapped, error.Location.SourceTree);
        Assert.AreEqual(original.Id, error.Id);
        Assert.AreEqual("Authored.cs", error.Location.GetMappedLineSpan().Path);
        Assert.AreEqual(75, error.Location.GetMappedLineSpan().StartLinePosition.Line);
        RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            "public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }", path: "Moved.cs",
            cancellationToken: context.CancellationToken)), out Compilation repaired);
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(repaired));
    }

    /// <summary>
    /// One invalid declaration preserves independent valid SQL and reports the first actionable calling restriction.
    /// </summary>
    [TestMethod]
    public void FunctionSignatureFailureDoesNotHideValidDeclaration()
    {
        CSharpCompilation initial = ModuleCompilation("""
            public class Bad { [Ankus.PgFunction] private int Reject<T>(ref int value) => value; }
            public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }
            """);
        ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation output, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Assert.AreEqual("ANKUS033", Assert.ContainsSingle(diagnostics).Id);
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
        Assert.Contains("CREATE FUNCTION \"answer\"", InstallationBody(output));
        Assert.DoesNotContain("CREATE FUNCTION \"reject\"", InstallationBody(output));
        AssertSignatureExports(output, "answer");
    }

    /// <summary>
    /// A new assembly version keeps every SQL-visible native symbol, so an upgraded library still exports the functions an
    /// installed version's catalog entries name; a rename still separates extensions.
    /// </summary>
    [TestMethod]
    public void NativeSymbolsSurviveAssemblyVersionChanges()
    {
        const string Source = "public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }";
        GeneratorDriver driver = ModuleDriver();
        CSharpCompilation first = ModuleCompilation(Source).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")]", path: "Version.cs", cancellationToken: context.CancellationToken));
        RunModule(driver, first, out Compilation before);
        CSharpCompilation second = first.ReplaceSyntaxTree(first.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")]", path: "Version.cs", cancellationToken: context.CancellationToken));
        RunModule(ModuleDriver(), second, out Compilation after);
        RunModule(ModuleDriver(), first.WithAssemblyName("Renamed"), out Compilation renamed);

        Assert.AreEqual(ManifestValue(before, "Ankus.Exports"), ManifestValue(after, "Ankus.Exports"));
        Assert.AreNotEqual(ManifestValue(before, "Ankus.Exports"), ManifestValue(renamed, "Ankus.Exports"));
    }

    /// <summary>
    /// Requires exactly the module, accepted function and matching PostgreSQL information exports.
    /// </summary>
    /// <param name="compilation">The generated consumer carrying the linker manifest.</param>
    /// <param name="name">The only accepted SQL function name.</param>
    private static void AssertSignatureExports(Compilation compilation, string name)
    {
        string[] exports = ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(3, exports);
        Assert.Contains("Pg_magic_func", exports);
        string function = Assert.ContainsSingle(exports.Where(static value => value.StartsWith("ankus_fn_", StringComparison.Ordinal)));
        Assert.EndsWith("_" + name, function);
        Assert.Contains("pg_finfo_" + function, exports);
    }
}
