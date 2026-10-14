using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// A measured companion identity reaches the native capability independently of the callback's SQL behavior.
    /// </summary>
    [TestMethod]
    public void NativeNodeCapabilityUsesTheCompilationsMeasuredBindingIdentity()
    {
        const string identity = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            namespace Ankus.Postgres
            {
                public static class NativeBinding
                {
                    public const string Identity = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
                    public const string NodeLayouts = "7:8:4;11:16:8";
                }
            }

            public static class Functions
            {
                [Ankus.PgFunction]
                public static int Value() => 1;
            }
            """);
        AssertMemoryCompilationSucceeds(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains("static const char ankus_binding_identity[] = \"" + identity + "\";", native);
        Assert.Contains("request->value != PG_VERSION_NUM / 10000", native);
        Assert.Contains("memcmp((const void *) request->data, ankus_binding_identity", native);
        Assert.Contains("[7] = {8, 4},", native);
        Assert.Contains("[11] = {16, 8},", native);
    }

    /// <summary>
    /// A lookalike declaration cannot inject C source or supply a malformed ABI identity to the native capability.
    /// </summary>
    /// <param name="identity">An invalid declaration identity.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("short")]
    [DataRow("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [DataRow("\"; bad_native_source(); //")]
    public void NativeNodeCapabilityRejectsMalformedBindingConstants(string identity)
    {
        string literal = SymbolDisplay.FormatLiteral(identity, quote: true);
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            namespace Ankus.Postgres
            {
                public static class NativeBinding
                {
                    public const string Identity = {{literal}};
                }
            }

            public static class Functions
            {
                [Ankus.PgFunction]
                public static int Value() => 1;
            }
            """);
        AssertMemoryCompilationSucceeds(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains("static const char ankus_binding_identity[] = \"\";", native);
        Assert.DoesNotContain("bad_native_source();", native);
    }

    /// <summary>
    /// Missing, malformed, duplicate and invalid layouts never become native switch arms or partial tables.
    /// </summary>
    /// <param name="layouts">The malformed concrete layout metadata.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("7:8")]
    [DataRow("7:8:4;7:16:8")]
    [DataRow("7:3:1")]
    [DataRow("7:8:0")]
    [DataRow("7:8:3")]
    [DataRow("7:8:16")]
    [DataRow("4294967296:8:4")]
    [DataRow("7:2147483648:4")]
    [DataRow("7:8:4;bad_native_source()")]
    public void NativeNodeCapabilityRejectsMalformedLayoutMetadata(string layouts)
    {
        string literal = SymbolDisplay.FormatLiteral(layouts, quote: true);
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            namespace Ankus.Postgres
            {
                public static class NativeBinding
                {
                    public const string Identity = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
                    public const string NodeLayouts = {{literal}};
                }
            }
            public static class Functions
            {
                [Ankus.PgFunction]
                public static int Value() => 1;
            }
            """);
        AssertMemoryCompilationSucceeds(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains("this extension has no measured node layout contract", native);
        Assert.DoesNotContain("ankus_node_layouts", native);
        Assert.DoesNotContain("bad_native_source()", native);
    }
}
