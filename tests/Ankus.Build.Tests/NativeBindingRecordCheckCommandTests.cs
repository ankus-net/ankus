namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Compiler failure, physical-layout failure and late cancellation preserve the prior output and clean owned files.
    /// </summary>
    /// <param name="failure">The failed verification boundary before a subsequent successful publication.</param>
    [TestMethod]
    [DataRow("compiler")]
    [DataRow("executable")]
    [DataRow("cancellation")]
    public async Task NativeRecordChecksPublishOnlyVerifiedContracts(string failure)
    {
        const string Headers = "typedef struct Entry { int value; unsigned int flags : 3; } Entry; extern Entry current;";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-record-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, [new("current", "current", false)], directory);
            string output = Path.Join(directory, "published");
            Directory.CreateDirectory(output);
            string destination = Path.Join(output, "native-record-checks.c");
            const string Prior = "prior verified source\n";
            await File.WriteAllTextAsync(destination, Prior, context.CancellationToken);
            string actual = failure switch
            {
                "compiler" => Headers.Replace("int value;", "float value;", StringComparison.Ordinal),
                "executable" => Headers.Replace("flags : 3", "flags : 4", StringComparison.Ordinal),
                _ => Headers,
            };
            string candidate = Source(actual);
            if (failure == "cancellation")
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingRecordCheckCommand.PublishAsync(output, candidate,
                    async (file, stage, token) =>
                    {
                        await VerifyAsync(file, stage, token);
                        await cancellation.CancelAsync();
                    }, cancellation.Token));
            }
            else
            {
                InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    NativeBindingRecordCheckCommand.PublishAsync(output, candidate, VerifyAsync, context.CancellationToken));
                Assert.Contains(failure == "compiler" ? "Native record contract changed: member type Entry.value" : "Native record bitfield changed: Entry.flags", error.Message);
            }

            Assert.AreEqual(Prior, await File.ReadAllTextAsync(destination, context.CancellationToken));
            Assert.IsEmpty(Directory.GetDirectories(output));
            Assert.AreSequenceEqual<string>([destination], Directory.GetFiles(output));
            string recovered = Source(Headers);
            await NativeBindingRecordCheckCommand.PublishAsync(output, recovered, VerifyAsync, context.CancellationToken);
            Assert.AreEqual(recovered, await File.ReadAllTextAsync(destination, context.CancellationToken));
            Assert.IsEmpty(Directory.GetDirectories(output));
            Assert.AreSequenceEqual<string>([destination], Directory.GetFiles(output));

            string Source(string headers) => NativeBindingRecordChecks.Generate(records, "#define PG_VERSION_NUM 180006\n" + headers) +
                NativeBindingRecordChecks.ExecutableEntryPoint;
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }

        static async Task VerifyAsync(string file, string stage, CancellationToken token)
        {
            string executable = Path.Join(stage, OperatingSystem.IsWindows() ? "checks.exe" : "checks");
            string compiler = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", file, "-o", executable];
            string log = Path.Join(stage, "compiler.txt");
            await NativeBindingHeaderCommand.CompileAsync(compiler, arguments, log, stage, token, inspectBodies: true);
            await NativeBindingHeaderCommand.CompileAsync(executable, [], log, stage, token, inspectBodies: true);
        }
    }

    /// <summary>
    /// Invalid arguments and initial cancellation fail before accessing input or creating publication directories.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksValidateCommandBoundaries()
    {
        ArgumentException arguments = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            NativeBindingRecordCheckCommand.RunAsync([], context.CancellationToken));
        Assert.Contains("binding-record-checks", arguments.Message);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            NativeBindingRecordCheckCommand.RunAsync(["missing", "", "unused"], cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingRecordCheckCommand.PublishAsync("unused", "",
            static (_, _, _) =>
            {
                Assert.Fail("Cancelled verification must not run.");
                return Task.CompletedTask;
            }, cancellation.Token));
    }
}
