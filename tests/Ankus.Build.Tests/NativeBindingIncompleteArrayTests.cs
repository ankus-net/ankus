namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// GCC verifies incomplete array alignment without inventing storage or discarding an aligned typedef.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task NativeRecordChecksPreserveIncompleteArrayAlignmentInGcc()
    {
        const string Headers = """
            typedef int Open[];
            typedef int Aligned[] __attribute__((aligned(64)));
            typedef const int Matrix[][3];
            extern Open values;
            extern Aligned aligned_values;
            extern Matrix rows;
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-array-checks-").FullName;
        try
        {
            NativeHeaderRequest[] requests = [new("values", "values", false), new("aligned_values", "aligned_values", false), new("rows", "rows", false)];
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, requests, directory);
            NativeRecordType aligned = records.Graph.Types[records.Graph.Roots["aligned_values"]];
            Assert.IsNull(aligned.Size);
            Assert.AreEqual(64L, aligned.Alignment);
            string source = NativeBindingRecordChecks.Generate(records, "#define PG_VERSION_NUM 180006\n" + Headers) + NativeBindingRecordChecks.ExecutableEntryPoint;
            string file = Path.Join(directory, "checks.c");
            string executable = Path.Join(directory, "checks");
            string[] arguments = ["-std=c11", "-Wall", "-Wextra", "-Werror", file, "-o", executable];
            await File.WriteAllTextAsync(file, source, context.CancellationToken);
            await RunAsync("gcc", arguments, directory);
            Assert.AreEqual("", await RunAsync(executable, [], directory));
            string changed = source.Replace("aligned(64)", "aligned(32)", StringComparison.Ordinal);
            Assert.AreNotEqual(source, changed);
            await File.WriteAllTextAsync(file, changed, context.CancellationToken);
            string diagnostic = await RunAsync("gcc", arguments, directory, expectSuccess: false);
            Assert.Contains("Native record contract changed: alignment type", diagnostic);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
