using Ankus.PgConfig;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Compiles and executes native code with the active Apple SDK despite a missing PostgreSQL build-time SDK.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.OSX)]
    public async Task NativeCompilationReplacesMissingHistoricalMacOsSdk()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-native-sdk-").FullName;
        try
        {
            string source = Path.Join(directory, "probe.c");
            string executable = Path.Join(directory, "probe");
            string[] recorded = ["-isysroot", Path.Join(directory, "absent.sdk"), "-DVALUE=42"];
            IReadOnlyList<string> selected = await NativeCompilerArguments.CreateAsync(recorded, context.CancellationToken);
            await File.WriteAllTextAsync(source, """
                #include <stdio.h>
                #include <stdlib.h>
                #include <unistd.h>
                int main(void)
                {
                    printf("%d\n", VALUE);
                    return EXIT_SUCCESS;
                }
                """, context.CancellationToken);
            await RunAsync(await HostCompilers.ClangAsync(), [.. selected, "-Wall", "-Wextra", "-Werror", source, "-o", executable], directory);
            Assert.AreEqual("42", (await RunAsync(executable, [], directory)).Trim());
            Assert.IsFalse(Directory.Exists(recorded[1]));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
