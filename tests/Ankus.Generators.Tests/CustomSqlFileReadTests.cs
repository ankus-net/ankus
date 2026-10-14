using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// SQL declarations read only the additional file they actually select, including after an unrelated input changes.
    /// </summary>
    /// <param name="file">Whether SQL comes from a tracked file rather than an inline declaration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomSqlReadsOnlySelectedAdditionalFiles(bool file)
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-read-project");
        var used = new CountedSqlInput(Path.Join(project, "seed.sql"), "SELECT 42;");
        var unused = new CountedSqlInput(Path.Join(project, "unused.data"), "Unrelated content");
        CSharpCompilation compilation = ModuleCompilation(file
            ? "[assembly: Ankus.PgSqlFile(\"seed\", \"seed.sql\")]"
            : "[assembly: Ankus.PgSql(\"seed\", \"SELECT 42;\")]");
        GeneratorDriver driver = RunModule(CustomSqlDriver(project, [used, unused]), compilation, out Compilation first);

        Assert.AreEqual(file ? 1 : 0, used.Reads);
        Assert.AreEqual(0, unused.Reads);
        Assert.AreEqual("SELECT 42;\n", InstallationBody(first));
        var replacement = new CountedSqlInput(unused.Path, "Changed unrelated content");
        driver = RunModule(driver.ReplaceAdditionalText(unused, replacement), compilation, out Compilation second);
        Assert.AreEqual(file ? 1 : 0, used.Reads);
        Assert.AreEqual(0, replacement.Reads);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "seed").Reason);
        Assert.AreEqual("SELECT 42;\n", InstallationBody(second));
    }

    /// <summary>
    /// A changed declaration reads its newly selected file without rereading the old file.
    /// </summary>
    [TestMethod]
    public void CustomSqlReadsNewSelectionAfterDeclarationChanges()
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-read-project");
        var firstFile = new CountedSqlInput(Path.Join(project, "first.sql"), "SELECT 1;");
        var secondFile = new CountedSqlInput(Path.Join(project, "second.sql"), "SELECT 2;");
        const string Source = "[assembly: Ankus.PgSqlFile(\"seed\", \"first.sql\")]";
        CSharpCompilation compilation = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(CustomSqlDriver(project, [firstFile, secondFile]), compilation, out Compilation first);
        Assert.AreEqual(1, firstFile.Reads);
        Assert.AreEqual(0, secondFile.Reads);
        Assert.AreEqual("SELECT 1;\n", InstallationBody(first));

        SyntaxTree changed = CSharpSyntaxTree.ParseText(Source.Replace("first.sql", "second.sql", StringComparison.Ordinal),
            path: "Module.cs", cancellationToken: context.CancellationToken);
        _ = RunModule(driver, compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.Single(), changed), out Compilation second);
        Assert.AreEqual(1, firstFile.Reads);
        Assert.AreEqual(1, secondFile.Reads);
        Assert.AreEqual("SELECT 2;\n", InstallationBody(second));
    }

    /// <summary>
    /// Filtering contents must not hide a suffix ambiguity when a different block uniquely selects one candidate.
    /// </summary>
    [TestMethod]
    public void CustomSqlReadsPreserveTheCompletePathCatalog()
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-read-project");
        var firstFile = new CountedSqlInput(Path.Join(project, "one", "seed.sql"), "SELECT 1;");
        var secondFile = new CountedSqlInput(Path.Join(project, "two", "seed.sql"), "SELECT 2;");
        CSharpCompilation compilation = ModuleCompilation("""
            [assembly: Ankus.PgSqlFile("ambiguous", "seed.sql")]
            [assembly: Ankus.PgSqlFile("exact", "one/seed.sql")]
            """);
        GeneratorDriver driver = CustomSqlDriver(project, [firstFile, secondFile])
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);

        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS368", error.Id);
        Assert.Contains("exactly one tracked input", error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(1, firstFile.Reads);
        Assert.AreEqual(0, secondFile.Reads);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
    }

    /// <summary>
    /// Records real compiler input reads without performing filesystem access.
    /// </summary>
    /// <param name="path">The tracked path.</param>
    /// <param name="content">The exact tracked contents.</param>
    private sealed class CountedSqlInput(string path, string content) : AdditionalText
    {
        private int _reads;

        /// <inheritdoc />
        public override string Path => path;

        /// <summary>
        /// Gets the number of actual content reads.
        /// </summary>
        internal int Reads => Volatile.Read(ref _reads);

        /// <inheritdoc />
        public override SourceText GetText(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _reads);
            return SourceText.From(content);
        }
    }
}
