using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Build.Tests;

/// <summary>
/// Shares one set of platform metadata references among every test compilation in the process.
/// </summary>
/// <remarks>
/// Roslyn copies each referenced file's whole image into native memory, which is released only when the
/// reference is finalized. Creating the references for each compilation therefore held gigabytes outside
/// the managed heap during parallel runs.
/// </remarks>
internal static class CompilationReferences
{
    /// <summary>
    /// Gets the test runtime's platform assemblies and the Ankus runtime assembly.
    /// </summary>
    internal static ImmutableArray<MetadataReference> Platform { get; } = Create();

    private static ImmutableArray<MetadataReference> Create()
    {
        string assemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The test runtime did not provide its assembly reference list.");
        return [.. assemblies.Split(Path.PathSeparator).Append(typeof(IPgNativeType).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(static path => MetadataReference.CreateFromFile(path))];
    }
}
