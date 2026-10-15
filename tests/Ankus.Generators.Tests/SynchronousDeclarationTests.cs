using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies synchronous callback validation, diagnostic locations and recovery against compiled consumers.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Distinct asynchronous SQL contracts report actionable errors before exposing native entry points.
    /// </summary>
    /// <param name="signature">The authored return type and optional async modifier.</param>
    /// <param name="body">A valid C# implementation of that contract.</param>
    /// <param name="expected">The dedicated PostgreSQL diagnostic.</param>
    [TestMethod]
    [DataRow("async void", "{ await Task.Yield(); }", "ANKUS030")]
    [DataRow("Task", "=> Task.CompletedTask;", "ANKUS031")]
    [DataRow("Task<int>", "=> Task.FromResult(42);", "ANKUS031")]
    [DataRow("async Task<int>", "{ await Task.Yield(); return 42; }", "ANKUS031")]
    [DataRow("ValueTask", "=> ValueTask.CompletedTask;", "ANKUS031")]
    [DataRow("ValueTask<int>", "=> new(42);", "ANKUS031")]
    [DataRow("IAsyncEnumerable<int>", "=> null!;", "ANKUS032")]
    [DataRow("async IAsyncEnumerable<int>", "{ await Task.Yield(); yield return 42; }", "ANKUS032")]
    [DataRow("DetachedTask", "=> new();", "ANKUS031")]
    [DataRow("DetachedRows", "=> new();", "ANKUS032")]
    public void AsynchronousFunctionContractsHaveActionableDiagnostics(string signature, string body, string expected)
    {
        CSharpCompilation initial = ModuleCompilation($$"""
            using System.Threading.Tasks;
            using System.Collections.Generic;
            public static class Functions { [Ankus.PgFunction] public static {{signature}} Run() {{body}} }
            public sealed class DetachedTask() : Task(() => { });
            public sealed class DetachedRows : IAsyncEnumerable<int>
            {
                public IAsyncEnumerator<int> GetAsyncEnumerator(System.Threading.CancellationToken cancellationToken = default)
                    => throw new System.InvalidOperationException();
            }
            """);
        Assert.IsEmpty(initial.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreSame(initial.SyntaxTrees.Single(), error.Location.SourceTree);
        Assert.AreEqual(expected == "ANKUS030" ? "async" : signature.Replace("async ", string.Empty, StringComparison.Ordinal),
            error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/reference/execution/#backend-threads-and-tasks", error.Descriptor.HelpLinkUri);
        Assert.Contains(expected == "ANKUS030" ? "keep the entry method synchronous" : expected == "ANKUS031" ?
            "return the completed SQL value" : "use IEnumerable<T>", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.AreSequenceEqual(["Pg_magic_func"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(output));
    }

    /// <summary>
    /// Cached rejection follows current mapped source trees and disappears when the function becomes synchronous.
    /// </summary>
    /// <param name="kind">The asynchronous contract to replace.</param>
    [TestMethod]
    [DataRow("async")]
    [DataRow("task")]
    [DataRow("sequence")]
    public void AsynchronousFunctionDiagnosticsFollowCurrentTreesAndRecover(string kind)
    {
        string declaration = kind switch
        {
            "async" => "async void Answer() { await System.Threading.Tasks.Task.Yield(); }",
            "task" => "System.Threading.Tasks.Task<int> Answer() => System.Threading.Tasks.Task.FromResult(42);",
            _ => "System.Collections.Generic.IAsyncEnumerable<int> Answer() => null!;",
        };
        string source = "public static class Functions { [Ankus.PgFunction] public static " + declaration + " }";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> previous,
            context.CancellationToken);
        Diagnostic original = Assert.ContainsSingle(previous);
        SyntaxTree current = CSharpSyntaxTree.ParseText(source + "\n// independent editor change", path: "Module.cs",
            cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(original.Id, error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "FunctionAnalysis"));
        SyntaxTree mapped = CSharpSyntaxTree.ParseText("#line 76 \"Authored.cs\"\n" + source, path: "Moved.cs",
            cancellationToken: context.CancellationToken);
        edited = edited.ReplaceSyntaxTree(current, mapped);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out diagnostics, context.CancellationToken);
        error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(original.Id, error.Id);
        Assert.AreSame(mapped, error.Location.SourceTree);
        Assert.AreEqual("Authored.cs", error.Location.GetMappedLineSpan().Path);
        Assert.AreEqual(75, error.Location.GetMappedLineSpan().StartLinePosition.Line);
        _ = RunModule(driver, edited.ReplaceSyntaxTree(mapped, CSharpSyntaxTree.ParseText(source.Replace(declaration, "int Answer() => 42;",
            StringComparison.Ordinal), path: "Moved.cs", cancellationToken: context.CancellationToken)), out Compilation repaired);
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(repaired));
        Assert.Contains("RETURNS integer", InstallationBody(repaired));
    }

    /// <summary>
    /// User-defined SQL types with familiar asynchronous names retain their synchronous semantics.
    /// </summary>
    /// <param name="name">The user type name that must not be identified by spelling alone.</param>
    [TestMethod]
    [DataRow("Task")]
    [DataRow("ValueTask")]
    [DataRow("IAsyncEnumerable")]
    public void SynchronousUserTypesAreNotClassifiedAsTasks(string name)
    {
        CSharpCompilation initial = ModuleCompilation($$"""
            [Ankus.PgType(Name="detached_value")] public readonly record struct {{name}}(int Number);
            public static class Functions
            {
                [Ankus.PgFunction] public static {{name}} Echo({{name}} value) => value;
                [Ankus.PgFunction] public static int Answer() => 42;
            }
            """);
        RunModule(ModuleDriver(), initial, out Compilation output);

        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
        Assert.Contains("CREATE TYPE \"detached_value\"", InstallationBody(output));
        Assert.Contains("CREATE FUNCTION \"echo\"", InstallationBody(output));
    }

    /// <summary>
    /// A source-owned type is not a framework awaitable even when its full metadata name matches Task.
    /// </summary>
    [TestMethod]
    public void SourceAssemblyTaskContractIsNotFrameworkAwaitable()
    {
        CSharpCompilation initial = ModuleCompilation("""
            namespace System.Threading.Tasks
            {
                public readonly record struct Task(int Number);
            }
            public static class Functions
            {
                [Ankus.PgFunction] public static System.Threading.Tasks.Task Echo(System.Threading.Tasks.Task value) => value;
                [Ankus.PgFunction] public static int Answer() => 42;
            }
            """);
        Assert.AreSame(initial.Assembly, initial.GetTypeByMetadataName("System.Threading.Tasks.Task")!.ContainingAssembly);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> diagnostics,
            context.CancellationToken);
        Assert.AreEqual("ANKUS039", Assert.ContainsSingle(diagnostics).Id);
        _ = RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            initial.SyntaxTrees.Single().GetText(context.CancellationToken).ToString().Replace(
                "System.Threading.Tasks.Task Echo(System.Threading.Tasks.Task value)", "int Echo(int value)", StringComparison.Ordinal),
            path: "Module.cs", cancellationToken: context.CancellationToken)), out Compilation output);

        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
        Assert.Contains("CREATE FUNCTION \"echo\"", InstallationBody(output));
    }

    /// <summary>
    /// An explicitly aliased framework Task retains its diagnostic when a source type shadows the metadata name.
    /// </summary>
    [TestMethod]
    public void AliasedFrameworkTaskIsRecognizedDespiteSourceShadowing()
    {
        CSharpCompilation initial = ModuleCompilation("""
            extern alias framework;
            namespace System.Threading.Tasks { public sealed class Task { } }
            public static class Functions
            {
                [Ankus.PgFunction] public static framework::System.Threading.Tasks.Task Run()
                    => framework::System.Threading.Tasks.Task.CompletedTask;
            }
            """).WithReferences(s_references.Select(reference => reference.Display == typeof(object).Assembly.Location ?
                reference.WithProperties(reference.Properties.WithAliases(["global", "framework"])) : reference));
        Assert.IsEmpty(initial.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.AreSame(initial.Assembly, initial.GetTypeByMetadataName("System.Threading.Tasks.Task")!.ContainingAssembly);
        IMethodSymbol method = Assert.ContainsSingle(initial.GetTypeByMetadataName("Functions")!.GetMembers("Run").OfType<IMethodSymbol>());
        Assert.AreSame(initial.GetSpecialType(SpecialType.System_Object).ContainingAssembly, method.ReturnType.ContainingAssembly);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> diagnostics,
            context.CancellationToken);

        Assert.AreEqual("ANKUS031", Assert.ContainsSingle(diagnostics).Id);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
    }

    /// <summary>
    /// Operator- and cast-only backing functions receive the same asynchronous rejection as ordinary functions.
    /// </summary>
    /// <param name="marker">The alias-only declaration marker.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"===\")]")]
    [DataRow("[Ankus.PgCast]")]
    public void AsynchronousAliasFunctionsAreRejected(string marker)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { " + marker +
            "public static System.Threading.Tasks.Task<int> Convert(int value) => System.Threading.Tasks.Task.FromResult(value); }");

        Assert.AreEqual("ANKUS031", Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// An async partial implementation cannot bypass validation through its synchronous definition.
    /// </summary>
    /// <param name="role">The PostgreSQL callback family.</param>
    /// <param name="implementationMarker">Whether the attribute is declared on the implementation.</param>
    [TestMethod]
    [DataRow("function", false)]
    [DataRow("function", true)]
    [DataRow("event", false)]
    [DataRow("event", true)]
    [DataRow("test", false)]
    [DataRow("test", true)]
    public void PartialAsyncCallbacksAreRejectedAndRecover(string role, bool implementationMarker)
    {
        string marker = role switch
        {
            "event" => "[Ankus.PgEventTrigger] ",
            "test" => "[Ankus.PgTest] ",
            _ => "[Ankus.PgFunction] ",
        };
        string parameters = role == "event" ? "Ankus.PgEventTriggerContext value" : string.Empty;
        string definition = "public static partial class Functions { [Ankus.PgFunction] public static int Answer() => 42; " +
            (implementationMarker ? string.Empty : marker) + "public static partial void Run(" + parameters + "); }";
        string implementation = "public static partial class Functions { " + (implementationMarker ? marker : string.Empty) +
            "public static async partial void Run(" + parameters + ") { await System.Threading.Tasks.Task.Yield(); } }";
        CSharpCompilation initial = ModuleCompilation(definition).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            implementation, path: "Implementation.cs", cancellationToken: context.CancellationToken));
        Assert.IsEmpty(initial.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        IMethodSymbol method = Assert.ContainsSingle(initial.GetTypeByMetadataName("Functions")!.GetMembers("Run").OfType<IMethodSymbol>());
        Assert.IsTrue(method.IsAsync);
        Assert.IsTrue(method.PartialImplementationPart!.IsAsync);
        GeneratorDriver driver = PgTestDriver(true).RunGeneratorsAndUpdateCompilation(initial, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        string expected = role switch
        {
            "event" => "ANKUS209",
            "test" => "ANKUS292",
            _ => "ANKUS030",
        };
        Assert.AreEqual(expected, error.Id);
        if (role is "event" or "test")
        {
            Assert.AreEqual("Implementation.cs", error.Location.SourceTree!.FilePath);
            Assert.AreEqual("async", error.Location.SourceTree.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        }

        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.IsNull(output.GetTypeByMetadataName("Functions+PostgresTests"));
        _ = RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            implementation.Replace("async partial", "partial", StringComparison.Ordinal)
                .Replace("await System.Threading.Tasks.Task.Yield();", "System.GC.KeepAlive(null);", StringComparison.Ordinal),
            path: "Implementation.cs", cancellationToken: context.CancellationToken)), out Compilation repaired);

        Assert.AreEqual(42, InvokeSqlReferenceAnswer(repaired));
        Assert.AreEqual(role == "test", repaired.GetTypeByMetadataName("Functions+PostgresTests") is not null);
    }
}
