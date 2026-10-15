using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies the logical decoding output plugin export, its native callback boundary and its declaration diagnostics.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Stands in for the generated selected-header callback table and one callback type.
    /// </summary>
    private const string OutputPluginTypesSource = """
        namespace Ankus.Postgres
        {
            public struct OutputPluginCallbacks : Ankus.IPgNativeType
            {
                static int Ankus.IPgNativeType.PostgresMajor => 18;
                static string Ankus.IPgNativeType.AbiIdentity => new string('A', 64);
                static string Ankus.IPgNativeType.RuntimeIdentifier => System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
                static int Ankus.IPgNativeType.NativeSize => 16;
                static int Ankus.IPgNativeType.NativeAlignment => 8;
                public StartupCallback startup_cb;
                public nint shutdown_cb;
            }

            [Ankus.CompilerServices.NativeFunctionPointer(7)]
            public readonly unsafe struct StartupCallback(void* address) : Ankus.IPgNativeType
            {
                public nint Address => (nint)address;
                static int Ankus.IPgNativeType.PostgresMajor => 18;
                static string Ankus.IPgNativeType.AbiIdentity => new string('A', 64);
                static string Ankus.IPgNativeType.RuntimeIdentifier => System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
                static int Ankus.IPgNativeType.NativeSize => System.IntPtr.Size;
                static int Ankus.IPgNativeType.NativeAlignment => System.IntPtr.Size;
                public void Invoke(nint context, bool isInit) => throw new System.NotSupportedException();
            }
        }

        """;

    /// <summary>
    /// The diagnostics that report output plugin declaration failures.
    /// </summary>
    private static readonly string[] s_outputPluginDiagnostics =
        ["ANKUS515", "ANKUS516", "ANKUS517", "ANKUS518", "ANKUS519", "ANKUS520", "ANKUS521", "ANKUS522", "ANKUS523",
            "ANKUS524", "ANKUS525", "ANKUS526", "ANKUS277"];

    /// <summary>
    /// Supported initializers export PostgreSQL's loader symbol through the native callback boundary without SQL objects.
    /// </summary>
    /// <param name="declaration">The supported declaration form.</param>
    /// <param name="target">The expected statically bound managed call target.</param>
    [TestMethod]
    [DataRow("public static unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }",
        "global::Plugins.@Init")]
    [DataRow("internal unsafe class Outer { internal struct Plugins { [Ankus.PgOutputPlugin] internal static void @event(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } } }",
        "global::Outer.Plugins.@event")]
    [DataRow("public static unsafe partial class Plugins { [Ankus.PgOutputPlugin] public static partial void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks); public static partial void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }",
        "global::Plugins.@Init")]
    [DataRow("public unsafe interface Plugins { [Ankus.PgOutputPlugin] protected internal static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }",
        "global::Plugins.@Init")]
    public void OutputPluginInitializersExportTheLoaderSymbol(string declaration, string target)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(OutputPluginTypesSource + declaration);
        AssertInitializationCompilationSucceeds(compilation, diagnostics);
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init", "_PG_output_plugin_init"],
            ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(compilation).ReplaceLineEndings("\n"));

        IMethodSymbol dispatcher = Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers()
            .OfType<IMethodSymbol>().Where(static method => method.Name.EndsWith("_output_plugin", StringComparison.Ordinal)));
        AttributeData entry = Assert.ContainsSingle(dispatcher.GetAttributes());
        Assert.AreEqual("UnmanagedCallersOnlyAttribute", entry.AttributeClass!.Name);
        Assert.AreEqual(dispatcher.Name, entry.NamedArguments.Single(static argument => argument.Key == "EntryPoint").Value.Value);
        Assert.AreEqual(SpecialType.System_Int32, dispatcher.ReturnType.SpecialType);
        Assert.HasCount(5, dispatcher.Parameters);
        string managed = string.Concat(compilation.SyntaxTrees.Where(static tree => tree.FilePath.EndsWith("ExtensionDispatchers.g.cs", StringComparison.Ordinal))
            .Select(tree => tree.GetText(context.CancellationToken).ToString())).ReplaceLineEndings("\n");
        string body = managed[managed.IndexOf("private static int " + dispatcher.Name, StringComparison.Ordinal)..];
        AssertOrdered(body, ["if (context is null || context->Error == 0)", "NativeBackend.Enter(context->Execute)", "NativeMemoryContext.Enter(context->Memory)",
            "NativeRawCallback.ValidateFrame(arguments, count, 1, result, resultSize, -1)",
            target + "(global::Ankus.CompilerServices.NativeRawCallback.ReadRecordPointer<global::Ankus.Postgres.OutputPluginCallbacks>(arguments[0]))",
            "catch (global::System.Exception exception)", "NativeError.Write(exception,", "NativeMemoryContext.Exit(previousMemory)",
            "NativeBackend.Exit(previousBackend)"]);

        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        AssertOrdered(native, ["ankus_dispatch_native_callback(AnkusManagedNativeCallback callback,", "#include \"replication/output_plugin.h\"",
            "extern int " + dispatcher.Name + "(const AnkusNativeCallArgument *, size_t, void *, size_t, void *);",
            "PGDLLEXPORT void _PG_output_plugin_init(OutputPluginCallbacks *callbacks);",
            "PGDLLEXPORT void\n_PG_output_plugin_init(OutputPluginCallbacks *callbacks)\n{",
            "AnkusNativeCallArgument arguments[] = { { &callbacks, sizeof(callbacks) } };",
            "ankus_dispatch_native_callback(" + dispatcher.Name + ", arguments, 1, NULL, 0);"]);
        Assert.DoesNotContain("PG_FUNCTION_INFO_V1", native);
    }

    /// <summary>
    /// An initializer that assigns generated callback properties composes with SQL functions and other native entries.
    /// </summary>
    [TestMethod]
    public void OutputPluginInitializersComposeWithCallbacksAndSql()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(OutputPluginTypesSource + """
            public static unsafe partial class Decoder
            {
                [Ankus.PgOutputPlugin]
                public static void Initialize(Ankus.Postgres.OutputPluginCallbacks* callbacks) => callbacks->startup_cb = Startup;

                [Ankus.PgNativeCallback(nameof(OnStartup))]
                private static partial Ankus.Postgres.StartupCallback Startup { get; }

                private static void OnStartup(nint context, bool isInit) { }

                [Ankus.PgBackgroundWorker]
                public static void Worker(nuint argument) { }

                [Ankus.PgFunction]
                public static int Answer() => 42;
            }
            """);
        Assert.IsEmpty(diagnostics);
        AssertCallbackCompiles(compilation);
        string[] exports = ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.ContainsSingle(exports.Where(static name => name == "_PG_output_plugin_init"));
        Assert.ContainsSingle(exports.Where(static name => name == "_PG_init"));
        Assert.Contains("Worker", exports);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.AreEqual(1, Occurrences(native, "ankus_dispatch_native_callback(AnkusManagedNativeCallback callback,"));
        Assert.AreEqual(1, Occurrences(native, "_PG_output_plugin_init(OutputPluginCallbacks *callbacks)\n{"));
        Assert.Contains("answer", InstallationBody(compilation));
        Assert.DoesNotContain("initialize", InstallationBody(compilation));

        static int Occurrences(string text, string value)
        {
            int count = 0;
            for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }

    /// <summary>
    /// Covers separately correctable signature, container and metadata failures at their authored source.
    /// </summary>
    /// <returns>The declaration, precise diagnostic and exact highlighted source text.</returns>
    public static IEnumerable<(string Source, string Diagnostic, string Fragment)> OutputPluginDiagnosticCases()
    {
        const string Table = "Ankus.Postgres.OutputPluginCallbacks* callbacks";
        return
        [
            ($"public unsafe class Plugins {{ public void Other() {{ [Ankus.PgOutputPlugin] static void Init({Table}) {{ }} Init(null); }} }}", "ANKUS515", "Ankus.PgOutputPlugin"),
            ($"public unsafe interface IPlugin {{ static abstract void Init({Table}); }} public unsafe class Plugins : IPlugin {{ [Ankus.PgOutputPlugin] static void IPlugin.Init({Table}) {{ }} }}",
                "ANKUS515", "Ankus.PgOutputPlugin"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] public void Init({Table}) {{ }} }}", "ANKUS516", "Init"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] public static async void Init(nint callbacks) {{ await System.Threading.Tasks.Task.Yield(); }} }}",
                "ANKUS517", "async"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] public static void Init<T>({Table}) {{ }} }}", "ANKUS518", "<T>"),
            ($"public unsafe interface Plugins {{ [Ankus.PgOutputPlugin] static abstract void Init({Table}); }}", "ANKUS519", "abstract"),
            ($"public unsafe interface Plugins {{ [Ankus.PgOutputPlugin] static virtual void Init({Table}) {{ }} }}", "ANKUS519", "virtual"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin, System.Runtime.InteropServices.DllImport(\"native\")] public static extern void Init({Table}); }}",
                "ANKUS519", "extern"),
            ($"public unsafe partial class Plugins {{ [Ankus.PgOutputPlugin] static partial void Init({Table}); }}", "ANKUS519", "Init"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] public static int Init({Table}) => 0; }}", "ANKUS520", "int"),
            ("public unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init() { } }", "ANKUS521", "()"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] public static void Init({Table}, int other) {{ }} }}", "ANKUS521",
                $"({Table}, int other)"),
            ("public unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(nint callbacks) { } }", "ANKUS521", "nint callbacks"),
            ("public unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(void* callbacks) { } }", "ANKUS521", "void* callbacks"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] public static void Init(ref {Table}) {{ }} }}", "ANKUS521", $"ref {Table}"),
            ("namespace Other { public struct OutputPluginCallbacks { } } public unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(Other.OutputPluginCallbacks* callbacks) { } }",
                "ANKUS521", "Other.OutputPluginCallbacks* callbacks"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] private static void Init({Table}) {{ }} }}", "ANKUS522", "private"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] protected static void Init({Table}) {{ }} }}", "ANKUS522", "protected"),
            ($"public unsafe class Plugins<T> {{ [Ankus.PgOutputPlugin] public static void Init({Table}) {{ }} }}", "ANKUS523", "Plugins"),
            ($"file unsafe class Plugins {{ [Ankus.PgOutputPlugin] public static void Init({Table}) {{ }} }}", "ANKUS523", "Plugins"),
            ($"public class Outer {{ private unsafe class Plugins {{ [Ankus.PgOutputPlugin] public static void Init({Table}) {{ }} }} }}", "ANKUS523", "Plugins"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin, System.Diagnostics.Conditional(\"DEBUG\")] public static void Init({Table}) {{ }} }}",
                "ANKUS277", "System.Diagnostics.Conditional(\"DEBUG\")"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin, System.Runtime.InteropServices.UnmanagedCallersOnly] public static void Init({Table}) {{ }} }}",
                "ANKUS524", "System.Runtime.InteropServices.UnmanagedCallersOnly"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin, Ankus.PgFunction] public static void Init({Table}) {{ }} }}", "ANKUS525", "Ankus.PgFunction"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin, Ankus.PgTrigger] public static void Init({Table}) {{ }} }}", "ANKUS525", "Ankus.PgTrigger"),
            ($"public unsafe class Plugins {{ [Ankus.PgOutputPlugin] public static void Init([Ankus.PgParameter(Name = \"table\")] {Table}) {{ }} }}",
                "ANKUS525", "Ankus.PgParameter(Name = \"table\")"),
        ];
    }

    /// <summary>
    /// Invalid initializers point at their exact cause, export nothing and leave an unrelated SQL function intact.
    /// </summary>
    /// <param name="source">The invalid authored initializer.</param>
    /// <param name="expected">The independent diagnostic identity.</param>
    /// <param name="fragment">The exact expected highlighted text.</param>
    [TestMethod]
    [DynamicData(nameof(OutputPluginDiagnosticCases))]
    public void OutputPluginFailuresHaveSpecificDiagnostics(string source, string expected, string fragment)
    {
        const string Sibling = " public static class Other { [Ankus.PgFunction] public static int Value() => 7; }";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(OutputPluginTypesSource + source + Sibling);
        Diagnostic error = Assert.ContainsSingle(diagnostics.Where(static diagnostic => s_outputPluginDiagnostics.Contains(diagnostic.Id)));
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(fragment, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(expected == "ANKUS277" ? "https://ankus.net/reference/execution/#conditional-entry-methods"
            : "https://ankus.net/logical-decoding/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.DoesNotContain("CS8785", diagnostics.Select(static diagnostic => diagnostic.Id));
        Assert.DoesNotContain("_PG_output_plugin_init", ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.DoesNotContain("_PG_output_plugin_init", ManifestValue(compilation, "Ankus.NativeSource"));
        Assert.Contains("value", InstallationBody(compilation));
    }

    /// <summary>
    /// A second initializer is rejected because a library exports one loader symbol; neither is exported.
    /// </summary>
    [TestMethod]
    public void OutputPluginDuplicatesAreRejected()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(OutputPluginTypesSource + """
            public static unsafe class First { [Ankus.PgOutputPlugin] public static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }
            public static unsafe class Second { [Ankus.PgOutputPlugin] public static void Start(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }
            public static class Other { [Ankus.PgFunction] public static int Value() => 7; }
            """);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS526", error.Id);
        Assert.AreEqual("Start", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains("'First.Init(Ankus.Postgres.OutputPluginCallbacks*)'", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/logical-decoding/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.DoesNotContain("_PG_output_plugin_init", ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("value", InstallationBody(compilation));
    }
}
