using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// A run-time definition call emits the native registry and binds it, through every ordinary call form.
    /// </summary>
    /// <param name="usings">Source imports.</param>
    /// <param name="call">The definition call.</param>
    [TestMethod]
    [DataRow("", "Ankus.PgGucRegistry.DefineInt(\"demo.\" + \"limit\", 3, \"Limit\")")]
    [DataRow("using Ankus;", "PgGucRegistry.DefineBool(\"demo.enabled\", true, \"Enabled\")")]
    [DataRow("using static Ankus.PgGucRegistry;", "DefineString(\"demo.label\", null, \"Label\")")]
    [DataRow("using Registry = Ankus.PgGucRegistry;", "Registry.DefineEnum(\"demo.mode\", 1, \"Mode\", [new Ankus.PgGucEnumOption(\"on\", 1)])")]
    [DataRow("", "global::Ankus.PgGucRegistry.DefineReal(\"demo.ratio\", 0.5, \"Ratio\", minimum: 0, maximum: 1)")]
    public void RuntimeDefinitionsEmitTheNativeRegistry(string usings, string call)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(usings + """

            public static class Lifecycle
            {
                [Ankus.PgModuleLoad]
                public static void Load() => _ =
            """ + call + """
            ;
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains("#define ANKUS_GUC_RUNTIME 1", native);
        Assert.Contains("static AnkusGuc *ankus_guc_definitions[1] = { NULL };", native);
        Assert.Contains("static const int ankus_guc_count = 0;", native);
        Assert.Contains("ankus_read_guc = ankus_guc_read;", native);
        Assert.Contains("return ankus_guc_define_runtime(name, (intptr_t) value, error);", native);
        // The guarded entry follows the read source's error capture, which it calls.
        int capture = native.IndexOf("ankus_guc_capture_error(ErrorData *data, AnkusError *error)", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, capture);
        Assert.IsGreaterThan(capture, native.IndexOf("ankus_guc_define_runtime(const char *name, intptr_t request, AnkusError *error)", StringComparison.Ordinal));
        // Hooks on run-time definitions dispatch through prebuilt slots, emitted after the shared hook path they call.
        int hookPath = native.IndexOf("ankus_guc_show(AnkusGuc *definition)", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, native.IndexOf("ankus_guc_check(AnkusGuc *definition, void *proposed", StringComparison.Ordinal));
        Assert.IsGreaterThan(hookPath, native.IndexOf("ANKUS_GUC_RUNTIME_SLOT(1)\n", StringComparison.Ordinal));
        Assert.Contains("ANKUS_GUC_RUNTIME_SLOT(64)\n", native);
        Assert.DoesNotContain("ANKUS_GUC_RUNTIME_SLOT(65)", native);
        Assert.Contains("ankus_guc_runtime_bind(AnkusGuc *definition, int hooks, int slot)", native);
        Assert.Contains("extern int ankus_guc_runtime_hook(", native);
        Assert.ContainsSingle(compilation.SyntaxTrees.Where(tree =>
            tree.GetText(context.CancellationToken).ToString().Contains("EntryPoint = \"ankus_guc_runtime_hook\"", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Extensions without run-time definition calls, including calls to a same-named type elsewhere, keep their native source unchanged.
    /// </summary>
    /// <param name="source">Extension source that never calls Ankus.PgGucRegistry.</param>
    [TestMethod]
    [DataRow("public static class Lifecycle { [Ankus.PgModuleLoad] public static void Load() { } }")]
    [DataRow("""
        namespace Other { public static class PgGucRegistry { public static int DefineInt(string name) => name.Length; } }
        public static class Lifecycle { [Ankus.PgModuleLoad] public static void Load() => _ = Other.PgGucRegistry.DefineInt("demo.limit"); }
        """)]
    public void ExtensionsWithoutRuntimeDefinitionsOmitTheRegistry(string source)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.DoesNotContain("ANKUS_GUC_RUNTIME", native);
        Assert.DoesNotContain("ankus_guc_define_runtime", native);
        Assert.DoesNotContain("ankus_guc_runtime_hook", native);
    }

    /// <summary>
    /// Run-time definitions extend, rather than replace, the generated registry of attribute-declared settings.
    /// </summary>
    [TestMethod]
    public void RuntimeDefinitionsCoexistWithDeclaredSettings()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static partial class Settings
            {
                [Ankus.PgGucInt("demo.count", 3, "Count")]
                public static partial int Count { get; }
            }

            public static class Lifecycle
            {
                [Ankus.PgModuleLoad]
                public static void Load() => _ = Ankus.PgGucRegistry.DefineInt("demo.extra", 4, "Extra");
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains("static const int ankus_guc_count = 1;", native);
        Assert.DoesNotContain("ankus_guc_definitions[1] = { NULL }", native);
        Assert.Contains("#define ANKUS_GUC_RUNTIME 1", native);
        Assert.Contains("return ankus_guc_define_runtime(name, (intptr_t) value, error);", native);
        // The declared setting reads its located variable; run-time definitions are only read by name.
        Assert.Contains("if (definition == NULL && !locate)", native);
        Assert.Contains("global::Ankus.CompilerServices.NativeGuc.ReadInt32(\"demo.count\", ref global::Ankus.Generated.GucVariables.",
            string.Concat(compilation.SyntaxTrees.Select(static tree => tree.ToString())));
    }
}
