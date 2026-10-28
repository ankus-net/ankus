using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Ankus.Build.Tests;

/// <summary>
/// Compiles and executes generated native binding declarations inside a collectible test assembly.
/// </summary>
internal static class GeneratedBindingCompilation
{
    /// <summary>
    /// Executes a test harness compiled together with the emitted declarations, requiring clean compiler diagnostics.
    /// </summary>
    /// <param name="binding">The complete generated declarations.</param>
    /// <param name="harness">A BindingAssertions.Run method returning observed values.</param>
    /// <param name="cancellationToken">Cancels parsing and compilation.</param>
    /// <param name="checkOverflow">Whether consumer arithmetic is checked by default.</param>
    /// <returns>The actual values read from the compiled unmanaged representations.</returns>
    internal static long[] Run(NativeBindingSource binding, string harness, CancellationToken cancellationToken, bool checkOverflow = false)
    {
        CSharpCompilation compilation = Create(binding, harness, cancellationToken);
        compilation = compilation.WithOptions(compilation.Options.WithOverflowChecks(checkOverflow));
        using var output = new MemoryStream();
        EmitResult result = compilation.Emit(output, cancellationToken: cancellationToken);
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        output.Position = 0;
        var loader = new AssemblyLoadContext(binding.AssemblyName, isCollectible: true);
        try
        {
            Assembly assembly = loader.LoadFromStream(output);
            Type? assertions = assembly.GetType("BindingAssertions");
            Assert.IsNotNull(assertions);
            MethodInfo? method = assertions.GetMethod("Run", BindingFlags.Static | BindingFlags.Public);
            Assert.IsNotNull(method);
            return method.CreateDelegate<Func<long[]>>()();
        }
        finally
        {
            loader.Unload();
        }
    }

    /// <summary>
    /// Gets actual compiler diagnostics for consumers that intentionally violate the emitted type contract.
    /// </summary>
    internal static ImmutableArray<Diagnostic> Diagnostics(NativeBindingSource binding, string harness, CancellationToken cancellationToken)
        => Create(binding, harness, cancellationToken).GetDiagnostics(cancellationToken);

    /// <summary>
    /// Builds both positive and negative consumers against the same compiler and runtime references.
    /// </summary>
    private static CSharpCompilation Create(NativeBindingSource binding, string harness, CancellationToken cancellationToken)
        => CSharpCompilation.Create(binding.AssemblyName,
            [CSharpSyntaxTree.ParseText(binding.Source, cancellationToken: cancellationToken),
                CSharpSyntaxTree.ParseText(harness, path: "Consumer.cs", cancellationToken: cancellationToken)],
            CompilationReferences.Platform, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
}
