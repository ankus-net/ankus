using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// A scalar function's text result uses the thread's reusable buffer, which native code copies as soon as the call
    /// returns; the rows of a set, which wait for PostgreSQL to read them, keep owned strings.
    /// </summary>
    [TestMethod]
    public void ScalarTextResultsReuseOneBuffer()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgFunction]
                public static string Echo(string value) => value;

                [Ankus.PgFunction]
                public static System.Collections.Generic.IEnumerable<string> Words(string value) => value.Split(' ');
            }
            """);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        string generated = DatumMappingManaged(compilation);
        Assert.Contains("*result = global::Ankus.CompilerServices.NativeValue.FromResultString(value);", generated);
        Assert.Contains("NativeValue.FromString(", generated);
    }
}
