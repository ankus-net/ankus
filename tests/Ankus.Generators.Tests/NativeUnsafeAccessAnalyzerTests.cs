using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies raw native contracts require explicit unsafe source context while checked APIs remain ordinary C#.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed partial class NativeUnsafeAccessAnalyzerTests(TestContext context)
{
    private const string Declarations = """
        public static class Raw
        {
            [Ankus.CompilerServices.NativeUnsafeAccess]
            public static int Read() => 42;
            [Ankus.CompilerServices.NativeUnsafeAccess]
            public static int Current { get; set; }
            public static int Checked() => 42;
        }
        public readonly struct Callback
        {
            [Ankus.CompilerServices.NativeUnsafeAccess]
            public int Invoke() => 42;
        }
        """;

    /// <summary>
    /// Scalar methods, both global accessors and method groups retain the same native obligations.
    /// </summary>
    /// <param name="body">The consumer body.</param>
    /// <param name="expression">The expression whose source location must be diagnosed.</param>
    [TestMethod]
    [DataRow("_ = Raw.Read();", "Raw.Read()")]
    [DataRow("_ = Raw.Current;", "Raw.Current")]
    [DataRow("Raw.Current = 1;", "Raw.Current")]
    [DataRow("Raw.Current++;", "Raw.Current")]
    [DataRow("System.Func<int> invoke = Raw.Read; _ = invoke;", "Raw.Read")]
    [DataRow("_ = new Callback().Invoke();", "new Callback().Invoke()")]
    [DataRow("System.Func<int> invoke = new Callback().Invoke; _ = invoke;", "new Callback().Invoke")]
    [DataRow("System.Func<int> invoke = () => Raw.Read(); _ = invoke;", "Raw.Read()")]
    [DataRow("int Local() => Raw.Read(); _ = Local();", "Raw.Read()")]
    public async Task RawAccessRequiresExplicitUnsafeContext(string body, string expression)
    {
        Diagnostic error = Assert.ContainsSingle(await Analyze("public static void Run() { " + body + " }"));
        Assert.AreEqual("ANKUS129", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains("unsafe block", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/raw-values/#fixed-native-functions", error.Descriptor.HelpLinkUri);
    }

    /// <summary>
    /// Unsafe contexts, metadata-only references and checked runtime APIs remain valid.
    /// </summary>
    /// <param name="member">The complete legal consumer member.</param>
    [TestMethod]
    [DataRow("public static void Run() { unsafe { _ = Raw.Read(); Raw.Current = Raw.Current + 1; } }")]
    [DataRow("public static unsafe void Run() { _ = Raw.Read(); }")]
    [DataRow("public static unsafe int Value => Raw.Current;")]
    [DataRow("public static unsafe class Nested { public static int Read() => Raw.Read(); }")]
    [DataRow("public static void Run() { unsafe int Local() => Raw.Read(); _ = Local(); }")]
    [DataRow("public static void Run() { unsafe { System.Func<int> invoke = Raw.Read; _ = invoke; } }")]
    [DataRow("public static string Name => nameof(Raw.Current);")]
    [DataRow("public static string Name => nameof(Raw.Read);")]
    [DataRow("public static int Value => Raw.Checked();")]
    [DataRow("public static void Run() => Ankus.PgInterrupts.Check();")]
    [DataRow("public static Ankus.PgMemoryContext Create() => Ankus.PgMemoryContext.Create(\"checked\");")]
    public async Task ExplicitContextsAndCheckedApisRemainValid(string member)
        => Assert.IsEmpty(await Analyze(member));

    /// <summary>
    /// An unsafe sibling declaration does not grant access to another method or property.
    /// </summary>
    [TestMethod]
    public async Task UnsafeContextDoesNotEscapeItsLexicalScope()
    {
        ImmutableArray<Diagnostic> diagnostics = await Analyze("""
            public static void Run()
            {
                unsafe { _ = Raw.Read(); }
                _ = Raw.Current;
            }
            """);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS129", error.Id);
        Assert.AreEqual("Raw.Current", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// Arbitrary-address runtime entry points retain their native obligations even when no pointer appears in the signature.
    /// </summary>
    /// <param name="body">The actual runtime caller.</param>
    /// <param name="expression">The exact obligated expression.</param>
    [TestMethod]
    [DataRow("_ = Ankus.PgFunctions.DangerousCall<int>(1, 0);", "Ankus.PgFunctions.DangerousCall<int>(1, 0)")]
    [DataRow("_ = Ankus.PgFunctions.DangerousCallRaw(1, 23, Ankus.PgMemoryContext.Current, 0);", "Ankus.PgFunctions.DangerousCallRaw(1, 23, Ankus.PgMemoryContext.Current, 0)")]
    [DataRow("_ = Ankus.PgInternal.DangerousCreate(1, Ankus.PgMemoryContext.Current);", "Ankus.PgInternal.DangerousCreate(1, Ankus.PgMemoryContext.Current)")]
    [DataRow("_ = Ankus.PgDatum.DangerousCreate(1, 23, Ankus.PgMemoryContext.Current);", "Ankus.PgDatum.DangerousCreate(1, 23, Ankus.PgMemoryContext.Current)")]
    [DataRow("Ankus.PgInternal value; unsafe { value = Ankus.PgInternal.DangerousCreate(1, Ankus.PgMemoryContext.Current); } _ = value.DangerousBorrow<int>();", "value.DangerousBorrow<int>()")]
    [DataRow("Ankus.CompilerServices.NativeRawCall.Invoke(1, [], 0, 0);", "Ankus.CompilerServices.NativeRawCall.Invoke(1, [], 0, 0)")]
    [DataRow("System.Func<nuint, Ankus.PgMemoryContext, Ankus.PgInternal> create = Ankus.PgInternal.DangerousCreate; _ = create;", "Ankus.PgInternal.DangerousCreate")]
    public async Task ArbitraryAddressApisRequireExplicitContexts(string body, string expression)
    {
        Diagnostic error = Assert.ContainsSingle(await Analyze("public static void Run() { " + body + " }"));
        Assert.AreEqual("ANKUS129", error.Id);
        Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.IsEmpty(await Analyze("public static void Run() { unsafe { " + body + " } }"));
    }

    /// <summary>
    /// Generated source has the same memory-safety obligation as authored source.
    /// </summary>
    /// <param name="path">The generated-file spelling.</param>
    /// <param name="header">The optional generated-code marker.</param>
    [TestMethod]
    [DataRow("Consumer.g.cs", "")]
    [DataRow("Consumer.cs", "// <auto-generated />\n")]
    [DataRow("Consumer.g.cs", "// <auto-generated />\n")]
    public async Task GeneratedSourceRetainsTheUnsafeRequirement(string path, string header)
    {
        Diagnostic error = Assert.ContainsSingle(await Analyze("public static int Run() => Raw.Read();", path, header));
        Assert.AreEqual("ANKUS129", error.Id);
        Assert.AreEqual("Raw.Read()", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.IsEmpty(await Analyze("public static int Run() { unsafe { return Raw.Read(); } }", path, header));
    }

    /// <summary>
    /// Executes the shipped analyzer against valid C# and the real runtime-owned contract attribute.
    /// </summary>
    private async Task<ImmutableArray<Diagnostic>> Analyze(string member, string path = "Consumer.cs", string header = "")
    {
        CSharpCompilation compilation = CSharpCompilation.Create("RawAnalyzerConsumer",
            [CSharpSyntaxTree.ParseText(header + Declarations + "public static class Consumer { " + member + " }",
                new CSharpParseOptions(LanguageVersion.CSharp14), path: path, cancellationToken: context.CancellationToken)],
            CompilationReferences.Platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken));
        var analysis = new CompilationWithAnalyzers(compilation, [new NativeUnsafeAccessAnalyzer()],
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: false));
        return await analysis.GetAnalyzerDiagnosticsAsync(context.CancellationToken);
    }
}
