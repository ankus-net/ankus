using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// A provider with every capability.
    /// </summary>
    private const string CustomScanProviderSource = """
        [Ankus.PgCustomScan]
        public sealed class NumberScan : Ankus.IPgRescannableCustomScan<NumberScan.State>, Ankus.IPgExplainedCustomScan<NumberScan.State>,
            Ankus.IPgScrollableCustomScan<NumberScan.State>, Ankus.IPgMarkableCustomScan<NumberScan.State>
        {
            public sealed class State
            {
                public int Read { get; set; }

                public int Marked { get; set; }
            }

            public static void Plan(Ankus.PgCustomScanPlanner planner)
            {
                if (planner.RelationName == "numbers")
                {
                    planner.AddPath(new(0, 1));
                }
            }

            public static State Begin(Ankus.PgCustomScanContext context) => new();

            public static bool Read(State state, Ankus.PgHeapTuple row)
            {
                if (state.Read == 3)
                {
                    return false;
                }

                row.Set(0, ++state.Read);
                return true;
            }

            public static bool ReadBackward(State state, Ankus.PgHeapTuple row)
            {
                if (state.Read == 0 || --state.Read == 0)
                {
                    return false;
                }

                row.Set(0, state.Read);
                return true;
            }

            public static void Rescan(State state) => state.Read = 0;

            public static void Mark(State state) => state.Marked = state.Read;

            public static void Restore(State state) => state.Read = state.Marked;

            public static void Explain(State state, Ankus.PgExplainWriter explain) => explain.Add("Rows Read", state.Read);
        }
        """;

    /// <summary>
    /// A provider registers its method tables during library load, chains the relation path hook, and dispatches every
    /// capability through constrained calls that also reach explicit interface implementations.
    /// </summary>
    /// <param name="source">The provider declaration.</param>
    /// <param name="name">The registered name.</param>
    /// <param name="rescan">Whether the dispatcher passes a rescan call.</param>
    /// <param name="explain">Whether the dispatcher passes an EXPLAIN call.</param>
    /// <param name="scroll">Whether the dispatcher passes a backward read.</param>
    /// <param name="mark">Whether the dispatcher passes mark and restore calls.</param>
    /// <param name="shared">The parallel provider's shared memory layout, or null.</param>
    [TestMethod]
    [DataRow(CustomScanProviderSource, "number_scan", true, true, true, true, null)]
    [DataRow("""
        internal sealed class Outer
        {
            [Ankus.PgCustomScan(Name = "nested")]
            internal sealed class Plain : Ankus.IPgCustomScan<string>
            {
                static void Ankus.IPgCustomScan<string>.Plan(Ankus.PgCustomScanPlanner planner) { }
                static string Ankus.IPgCustomScan<string>.Begin(Ankus.PgCustomScanContext context) => "state";
                static bool Ankus.IPgCustomScan<string>.Read(string state, Ankus.PgHeapTuple row) => false;
            }
        }
        """, "nested", false, false, false, false, null)]
    [DataRow("""
        [Ankus.PgCustomScan(Name = "scroll_only")]
        public sealed class Scroll : Ankus.IPgScrollableCustomScan<string>
        {
            public static void Plan(Ankus.PgCustomScanPlanner planner) { }
            public static string Begin(Ankus.PgCustomScanContext context) => "state";
            public static bool Read(string state, Ankus.PgHeapTuple row) => false;
            public static bool ReadBackward(string state, Ankus.PgHeapTuple row) => false;
        }
        """, "scroll_only", false, false, true, false, null)]
    [DataRow("""
        [Ankus.PgCustomScan]
        public sealed class Split : Ankus.IPgParallelCustomScan<Split.State, Split.Counter>
        {
            public sealed class State
            {
                public Ankus.PgCustomScanSharedMemory<Counter>? Memory { get; set; }
            }

            public readonly struct Counter
            {
                public readonly Ankus.PgAtomicValue<long> Claimed;
            }

            public static void Plan(Ankus.PgCustomScanPlanner planner) => planner.AddParallelPath(new(0, 1), 2);
            public static State Begin(Ankus.PgCustomScanContext context) => new();
            public static bool Read(State state, Ankus.PgHeapTuple row) => state.Memory is { } memory && Ankus.PgAtomicValue.Increment(in memory.Value.Claimed) <= 3;
            public static void InitializeShared(State state, ref Counter memory) { }
            public static void AttachShared(State state, Ankus.PgCustomScanSharedMemory<Counter> memory) => state.Memory = memory;
        }
        """, "split", false, false, false, false, "global::Split.Counter")]
    public void CustomScanProvidersRegisterAndDispatch(string source, string name, bool rescan, bool explain, bool scroll, bool mark,
        string? shared)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        IMethodSymbol dispatcher = Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers()
            .OfType<IMethodSymbol>().Where(static method => method.Name.EndsWith("_custom_scan", StringComparison.Ordinal)));
        Assert.AreEqual(dispatcher.Name, Assert.ContainsSingle(dispatcher.GetAttributes()).NamedArguments
            .Single(static argument => argument.Key == "EntryPoint").Value.Value);
        string managed = string.Concat(compilation.SyntaxTrees.Where(static tree => tree.FilePath.EndsWith("ExtensionDispatchers.g.cs", StringComparison.Ordinal))
            .Select(tree => tree.GetText(context.CancellationToken).ToString())).ReplaceLineEndings("\n");
        Assert.AreEqual(rescan, managed.Contains("&" + dispatcher.Name + "_rescan<", StringComparison.Ordinal));
        Assert.AreEqual(explain, managed.Contains("&" + dispatcher.Name + "_explain<", StringComparison.Ordinal));
        Assert.AreEqual(scroll, managed.Contains("&" + dispatcher.Name + "_read_backward<", StringComparison.Ordinal));
        Assert.AreEqual(mark, managed.Contains("&" + dispatcher.Name + "_mark<", StringComparison.Ordinal));
        Assert.AreEqual(mark, managed.Contains("&" + dispatcher.Name + "_restore<", StringComparison.Ordinal));
        Assert.AreEqual(shared is not null, managed.Contains("&" + dispatcher.Name + "_initialize_shared<", StringComparison.Ordinal));
        Assert.AreEqual(shared is not null, managed.Contains("&" + dispatcher.Name + "_attach_shared<", StringComparison.Ordinal));
        if (shared is not null)
        {
            Assert.Contains("NativeCustomScan.Dispatch<global::Split.State, " + shared + ">(", managed);
        }

        int flags = (scroll ? 1 : 0) | (mark ? 2 : 0);

        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        AssertOrdered(native, ["static void ankus_custom_scan_register(void);", "ankus_custom_scan_register();",
            "ankus_dispatch_native_callback(AnkusManagedNativeCallback callback,", "typedef struct AnkusCustomScanRequest",
            "extern int " + dispatcher.Name + "(int, AnkusCustomScanRequest *, AnkusValue *, AnkusError *, AnkusExecute, AnkusMemoryApi *);",
            "#define ANKUS_CUSTOM_SCAN_COUNT 1", "{ \"" + name + "\", " + dispatcher.Name + ", " + flags.ToString(CultureInfo.InvariantCulture) + ", " +
                (shared is null ? "false" : "true") + " },",
            "RegisterCustomScanMethods(&ankus_custom_scan_plan_methods[provider]);", "set_rel_pathlist_hook = ankus_custom_scan_paths;"]);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(compilation).ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Each provider mistake has its own diagnostic at the declaration that makes it, and leaves other declarations
    /// generating.
    /// </summary>
    /// <param name="source">The provider declaration.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="fragment">The marked source text.</param>
    /// <param name="detail">A fragment of the message.</param>
    [TestMethod]
    [DataRow("[Ankus.PgCustomScan] public sealed class Scan { }", "ANKUS535", "Scan", "it implements none")]
    [DataRow("[Ankus.PgCustomScan] public sealed class Scan : Ankus.IPgCustomScan<string>, Ankus.IPgCustomScan<object> " +
        "{ public static void Plan(Ankus.PgCustomScanPlanner planner) { } static string Ankus.IPgCustomScan<string>.Begin(Ankus.PgCustomScanContext context) => \"\"; " +
        "static object Ankus.IPgCustomScan<object>.Begin(Ankus.PgCustomScanContext context) => new(); " +
        "public static bool Read(string state, Ankus.PgHeapTuple row) => false; public static bool Read(object state, Ankus.PgHeapTuple row) => false; }",
        "ANKUS535", "Scan", "it implements 2 state types")]
    [DataRow("[Ankus.PgCustomScan(Name = \"bad-name\")] public sealed class Scan : Ankus.IPgCustomScan<string> { " +
        "public static void Plan(Ankus.PgCustomScanPlanner planner) { } public static string Begin(Ankus.PgCustomScanContext context) => \"\"; " +
        "public static bool Read(string state, Ankus.PgHeapTuple row) => false; }", "ANKUS536", "\"bad-name\"", "'bad-name'")]
    [DataRow("[Ankus.PgCustomScan(Name = \"a1234567890123456789012345678901234567890123456789012345678901234\")] public sealed class Scan : Ankus.IPgCustomScan<string> { " +
        "public static void Plan(Ankus.PgCustomScanPlanner planner) { } public static string Begin(Ankus.PgCustomScanContext context) => \"\"; " +
        "public static bool Read(string state, Ankus.PgHeapTuple row) => false; }", "ANKUS536",
        "\"a1234567890123456789012345678901234567890123456789012345678901234\"", "custom scan name")]
    [DataRow("public static class Outer { [Ankus.PgCustomScan] private sealed class Scan : Ankus.IPgCustomScan<string> { " +
        "public static void Plan(Ankus.PgCustomScanPlanner planner) { } public static string Begin(Ankus.PgCustomScanContext context) => \"\"; " +
        "public static bool Read(string state, Ankus.PgHeapTuple row) => false; } }", "ANKUS538", "Scan", "'Scan'")]
    [DataRow("public static class Outer<T> { [Ankus.PgCustomScan] public sealed class Scan : Ankus.IPgCustomScan<string> { " +
        "public static void Plan(Ankus.PgCustomScanPlanner planner) { } public static string Begin(Ankus.PgCustomScanContext context) => \"\"; " +
        "public static bool Read(string state, Ankus.PgHeapTuple row) => false; } }", "ANKUS538", "Outer", "'Outer'")]
    [DataRow("[Ankus.PgCustomScan] public sealed class Scan : Ankus.IPgCustomScan<string> { " +
        "public static async void Plan(Ankus.PgCustomScanPlanner planner) { await System.Threading.Tasks.Task.Yield(); } " +
        "public static string Begin(Ankus.PgCustomScanContext context) => \"\"; public static bool Read(string state, Ankus.PgHeapTuple row) => false; }",
        "ANKUS539", "async", "'Plan'")]
    [DataRow("[Ankus.PgCustomScan] public sealed class Scan : Ankus.IPgMarkableCustomScan<string> { " +
        "public static void Plan(Ankus.PgCustomScanPlanner planner) { } public static string Begin(Ankus.PgCustomScanContext context) => \"\"; " +
        "public static bool Read(string state, Ankus.PgHeapTuple row) => false; " +
        "public static async void Mark(string state) { await System.Threading.Tasks.Task.Yield(); } public static void Restore(string state) { } }",
        "ANKUS539", "async", "'Mark'")]
    [DataRow("[Ankus.PgCustomScan] public sealed class Scan : Ankus.IPgParallelCustomScan<string, int>, Ankus.IPgParallelCustomScan<string, long> { " +
        "public static void Plan(Ankus.PgCustomScanPlanner planner) { } public static string Begin(Ankus.PgCustomScanContext context) => \"\"; " +
        "public static bool Read(string state, Ankus.PgHeapTuple row) => false; " +
        "public static void InitializeShared(string state, ref int memory) { } public static void InitializeShared(string state, ref long memory) { } " +
        "public static void AttachShared(string state, Ankus.PgCustomScanSharedMemory<int> memory) { } " +
        "public static void AttachShared(string state, Ankus.PgCustomScanSharedMemory<long> memory) { } }",
        "ANKUS540", "Scan", "2 shared memory types")]
    public void CustomScanProviderFailuresHaveSpecificDiagnostics(string source, string id, string fragment, string detail)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source +
            " public static class Other { [Ankus.PgFunction] public static int Value() => 7; }");
        Diagnostic error = Assert.ContainsSingle(diagnostics.Where(static diagnostic => diagnostic.Id is ['A', 'N', 'K', 'U', 'S', '5', '3' or '4', _]));
        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(fragment, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains(detail, error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://willibrandon.github.io/ankus/custom-scans/#managed-providers", error.Descriptor.HelpLinkUri);
        Assert.DoesNotContain("ankus_custom_scan_register", ManifestValue(compilation, "Ankus.NativeSource"));
        Assert.Contains("value", InstallationBody(compilation));
    }

    /// <summary>
    /// Two providers cannot register the same name; the second is reported and the first still registers.
    /// </summary>
    [TestMethod]
    public void CustomScanNamesAreUnique()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(CustomScanProviderSource + """
            [Ankus.PgCustomScan(Name = "number_scan")]
            public sealed class Other : Ankus.IPgCustomScan<string>
            {
                public static void Plan(Ankus.PgCustomScanPlanner planner) { }
                public static string Begin(Ankus.PgCustomScanContext context) => "";
                public static bool Read(string state, Ankus.PgHeapTuple row) => false;
            }
            """);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS537", error.Id);
        Assert.AreEqual("Other", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains("'NumberScan'", error.GetMessage(CultureInfo.InvariantCulture));
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains("#define ANKUS_CUSTOM_SCAN_COUNT 1", native);
    }
}
