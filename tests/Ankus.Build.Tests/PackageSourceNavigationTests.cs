using Ankus.PgConfig;
using Ankus.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies that Go to Definition opens the packaged libraries' declarations from the sources in their PDBs.
/// </summary>
[TestClass]
public sealed class PackageSourceNavigationTests
{
    /// <summary>
    /// Opens every public declaration of a packaged library as Go to Definition does and requires each to open at its
    /// own location, with every name in the opened documents resolved so Go to Definition also works from there.
    /// </summary>
    /// <param name="library">A type from the packaged library.</param>
    [TestMethod]
    [DataRow(typeof(PgFunctions))]
    [DataRow(typeof(PostgresInstallation))]
    [DataRow(typeof(PostgresExtensionTest))]
    public void PublicDeclarationsOpenAtTheirOwnLocation(Type library)
    {
        string location = library.Assembly.Location;
        PortableExecutableReference reference = CompilationReferences.Platform.OfType<PortableExecutableReference>()
            .Single(reference => reference.FilePath == location);
        var assembly = CSharpCompilation.Create("Consumer", references: CompilationReferences.Platform)
            .GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
        Assert.IsNotNull(assembly);

        using var navigation = new PdbNavigation(location);
        PdbNavigationResult result = navigation.Open(assembly, CompilationReferences.Platform);
        Assert.IsGreaterThan(50, result.Declarations.Count);
        Assert.IsEmpty(result.Failures, string.Join(Environment.NewLine, result.Failures.Take(20)));
    }
}
