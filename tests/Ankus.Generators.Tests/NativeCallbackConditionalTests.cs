using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators.Tests;

/// <summary>
/// Prevents compile-time call omission at statically declared native callback boundaries.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Handler metadata edits invalidate validation, retain sibling caches and clear the diagnostic after repair.
    /// </summary>
    /// <param name="defined">Whether the conditional symbol is currently defined.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeCallbackConditionalMetadataTracksEditsAndRepairs(bool defined)
    {
        const string Attribute = "System.Diagnostics.Conditional(\"ANKUS_CALLBACK_ENABLED\")";
        string source = (defined ? "#define ANKUS_CALLBACK_ENABLED\n" : string.Empty) +
            CallbackCacheSource().Replace("long Invoke(int first, long second)", "void Invoke()", StringComparison.Ordinal)
                .Replace("long Handle(int first, long second) => second + first;", "void Handle() { }", StringComparison.Ordinal)
                .Replace("long OtherHandle(int first, long second) => second;", "void OtherHandle() { }", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string original = CallbackEmission(driver, "Callback").Source;
        string sibling = CallbackEmission(driver, "OtherCallback").Source;
        AssertCallbackCompiles(first);

        string invalid = source.Replace("private static void Handle()", "[" + Attribute + "] private static void Handle()", StringComparison.Ordinal);
        CSharpCompilation rejected = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            invalid, path: "Invalid.cs", cancellationToken: context.CancellationToken));
        driver = driver.RunGeneratorsAndUpdateCompilation(rejected, out _, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS276", error.Id);
        Assert.AreEqual(Attribute, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.DoesNotContain(" @Callback\n", CallbackProperties(driver)!.ReplaceLineEndings("\n"));
        Assert.AreEqual(sibling, CallbackEmission(driver, "OtherCallback").Source);
        Assert.AreEqual(IncrementalStepRunReason.Cached, CallbackEmission(driver, "OtherCallback").Reason);

        CSharpCompilation moved = rejected.ReplaceSyntaxTree(rejected.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            "\n\n" + invalid, path: "Moved.cs", cancellationToken: context.CancellationToken));
        driver = driver.RunGeneratorsAndUpdateCompilation(moved, out _, out diagnostics, context.CancellationToken);
        Diagnostic current = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS276", current.Id);
        Assert.AreSame(moved.SyntaxTrees.Single(), current.Location.SourceTree);
        Assert.AreEqual(error.Location.GetLineSpan().StartLinePosition.Line + 2, current.Location.GetLineSpan().StartLinePosition.Line);
        Assert.AreEqual(Attribute, current.Location.SourceTree!.GetText(context.CancellationToken).ToString(current.Location.SourceSpan));
        Assert.AreEqual(IncrementalStepRunReason.Cached, CallbackEmission(driver, "OtherCallback").Reason);

        driver = RunModule(driver, initial, out Compilation repaired);
        Assert.AreEqual(original, CallbackEmission(driver, "Callback").Source);
        Assert.AreEqual(sibling, CallbackEmission(driver, "OtherCallback").Source);
        Assert.AreEqual(IncrementalStepRunReason.Cached, CallbackEmission(driver, "OtherCallback").Reason);
        AssertCallbackCompiles(repaired);
    }

    /// <summary>
    /// Conditional handlers fail at their authored attribute independently of symbols or partial placement.
    /// </summary>
    /// <param name="placement">An ordinary method, partial definition, or partial implementation.</param>
    /// <param name="defined">Whether the conditional symbol is currently defined.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public void NativeCallbackDeclarationsRejectConditionalHandlers(int placement, bool defined)
    {
        const string Attribute = "global::System.Diagnostics.Conditional(\"ANKUS_CALLBACK_ENABLED\")";
        string definition = placement == 0 ? string.Empty :
            (placement == 1 ? "[" + Attribute + "] " : string.Empty) + "private static partial void Handle();";
        string implementation = (placement != 1 ? "[" + Attribute + "] " : string.Empty) +
            "private static " + (placement == 0 ? string.Empty : "partial ") + "void Handle() { }";
        string types = CallbackTypeSource.Replace("public long Invoke(int first, long second)", "public void Invoke()", StringComparison.Ordinal);
        string source = (defined ? "#define ANKUS_CALLBACK_ENABLED\n" : string.Empty) + types + $$"""
            public static partial class Functions
            {
                [Ankus.PgNativeCallback(nameof(Handle))]
                public static partial Hook Callback { get; }
                {{definition}}
                {{implementation}}
            }
            public static class Other
            {
                [Ankus.PgFunction]
                public static int Value() => 7;
            }
            """;
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source, path: "Callbacks.cs");
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS276", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(Attribute, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/raw-values/#managed-native-callbacks-and-hooks", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(compilation.GetTypeByMetadataName("Functions")!.GetTypeMembers());
        Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
        Assert.Contains("value", InstallationBody(compilation));

        SyntaxTree authored = compilation.SyntaxTrees.Single(static tree => tree.FilePath == "Callbacks.cs");
        SyntaxNode syntax = authored.GetRoot(context.CancellationToken);
        PropertyDeclarationSyntax property = Assert.ContainsSingle(syntax.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(static item => item.Identifier.ValueText == "Callback"));
        SyntaxTree withoutInvalid = authored.WithRootAndOptions(syntax.RemoveNode(property, SyntaxRemoveOptions.KeepNoTrivia)!, authored.Options);
        AssertCallbackCompiles(compilation.ReplaceSyntaxTree(authored, withoutInvalid));
    }

    /// <summary>
    /// Ordinary and debugger-annotated void handlers still execute once through the real generated managed dispatcher.
    /// </summary>
    /// <param name="debugger">Whether an unrelated debugger annotation is present.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeCallbackNonconditionalVoidHandlersExecute(bool debugger)
    {
        string types = CallbackTypeSource.Replace("public long Invoke(int first, long second)", "public void Invoke()", StringComparison.Ordinal);
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(types + $$"""
            public static partial class Functions
            {
                public static int Effects;
                [Ankus.PgNativeCallback(nameof(Handle))]
                public static partial Hook Callback { get; }
                {{(debugger ? "[System.Diagnostics.DebuggerStepThrough]" : string.Empty)}}
                private static void Handle() => Effects += 73;
            }
            public static unsafe class ConditionalControl
            {
                [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
                private static int Resolve(nint api, void* request, nint* result, Ankus.CompilerServices.NativeCallError* error) => 0;

                public static object[] Run()
                {
                    nint* api = stackalloc nint[] { 999, 77, (nint)(delegate* unmanaged[Cdecl]<nint, void*, nint*, Ankus.CompilerServices.NativeCallError*, int>)&Resolve, 0 };
                    nint previous = Ankus.CompilerServices.NativeMemoryContext.Enter((nint)api);
                    try
                    {
                        nint address = Functions.Callback.Address;
                        Ankus.CompilerServices.NativeCallError error = default;
                        Ankus.CompilerServices.NativeCallbackContext native = new((nint)(&error), 0, (nint)api, 0, 0);
                        var invoke = (delegate* unmanaged[Cdecl]<Ankus.CompilerServices.NativeCallArgument*, nuint, nint, nuint, Ankus.CompilerServices.NativeCallbackContext*, int>)address;
                        int status = invoke(null, 0, 0, 0, &native);
                        return [status, Functions.Effects, error];
                    }
                    finally
                    {
                        Ankus.CompilerServices.NativeMemoryContext.Exit(previous);
                    }
                }
            }
            """);
        Assert.IsEmpty(diagnostics);
        compilation = ImplementCallbackAccessors(compilation);
        using var stream = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = compilation.Emit(stream, cancellationToken: context.CancellationToken);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var load = new AssemblyLoadContext("NativeCallbackConditionalControl", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(stream);
            MethodInfo run = assembly.GetType("ConditionalControl", throwOnError: true)!.GetMethod("Run")!;
            object[] values = Assert.IsInstanceOfType<object[]>(run.Invoke(null, null));
            try
            {
                Assert.AreEqual(0, Assert.IsInstanceOfType<int>(values[0]));
                Assert.AreEqual(73, Assert.IsInstanceOfType<int>(values[1]));
            }
            finally
            {
                typeof(NativeCallError).GetMethod("Release", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(values[2], null);
            }
        }
        finally
        {
            load.Unload();
        }
    }
}
