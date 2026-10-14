using System.Globalization;
using System.Text.Json;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Deep consumer outputs retain independently owned native objects usable by the actual MSVC linker after consumer cleanup.
    /// </summary>
    /// <param name="consumerLength">The complete consumer output path length.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow(256)]
    [DataRow(260)]
    [DataRow(261)]
    [DataRow(300)]
    public async Task NativeLinkPublicationSupportsLongWindowsConsumerPaths(int consumerLength)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-link-path-").FullName;
        try
        {
            string parent = Path.Join(directory, new string('p', 80));
            string output = Path.Join(parent, new string('q', consumerLength - parent.Length - 1));
            string cache = Path.Join(directory, "cache");
            Assert.AreEqual(consumerLength, output.Length);
            await NativeBindingLinkCommand.PublishAsync(output, cache, "int native_published(void) { return 42; }\n",
                (file, artifact, token) => NativeBindingLayoutCommand.RunProcessAsync("cl.exe",
                    ["/nologo", "/WX", "/c", "/O2", "/Fo" + artifact, file], directory, token), context.CancellationToken);
            string stored = Assert.ContainsSingle(Directory.GetFiles(cache, "native-calls.obj", SearchOption.AllDirectories));
            string selected = (await File.ReadAllTextAsync(Path.Join(output, "native-call-libraries.txt"), context.CancellationToken)).Trim();
            Assert.AreEqual(stored, selected);
            Directory.Delete(output, recursive: true);
            string main = Path.Join(directory, "main.c");
            string caller = Path.Join(directory, "main.obj");
            string executable = Path.Join(directory, "main.exe");
            await File.WriteAllTextAsync(main,
                "#include <stdio.h>\nextern int native_published(void);\nint main(void) { printf(\"%d\\n\", native_published()); return 0; }\n",
                context.CancellationToken);
            await RunAsync("cl.exe", ["/nologo", "/WX", "/c", "/O2", "/Fo" + caller, main], directory);
            string response = Path.Join(directory, "link.rsp");
            await File.WriteAllTextAsync(response, $"/NOLOGO\n/OUT:\"{executable}\"\n\"{caller}\"\n\"{selected}\"\n", context.CancellationToken);
            await RunAsync("link.exe", ["@" + response], directory);
            Assert.AreEqual("42\n", (await RunAsync(executable, [], directory)).ReplaceLineEndings("\n"));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Compiler failure, missing output and late cancellation retain the old manifest and executable object until recovery.
    /// </summary>
    /// <param name="failure">The failed boundary before publishing a corrected native body.</param>
    [TestMethod]
    [DataRow("compiler")]
    [DataRow("missing-object")]
    [DataRow("cancellation")]
    public async Task NativeLinkPublicationPreservesExecutableOutputAndRecovers(string failure)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-link-publication-").FullName;
        var stages = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            string output = Path.Join(directory, "output");
            string cache = Path.Join(directory, "cache");
            string manifest = Path.Join(output, "native-call-libraries.txt");
            const string Original = "int native_published(void) { return 41; }\n";
            const string Recovered = "int native_published(void) { return 42; }\n";
            await NativeBindingLinkCommand.PublishAsync(output, cache, Original, CompileAsync, context.CancellationToken);
            string previous = await File.ReadAllTextAsync(manifest, context.CancellationToken);
            string artifact = previous.Trim();
            byte[] bytes = await File.ReadAllBytesAsync(artifact, context.CancellationToken);
            Assert.AreEqual("41\n", await ExecutePublishedBodyAsync(artifact, directory));
            if (failure == "compiler")
            {
                InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    NativeBindingLinkCommand.PublishAsync(output, cache, "#error rejected_native_body\n", CompileAsync, context.CancellationToken));
                Assert.Contains("rejected_native_body", error.Message);
            }
            else if (failure == "missing-object")
            {
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => NativeBindingLinkCommand.PublishAsync(output, cache, Recovered,
                    async (file, _, token) =>
                    {
                        stages.Add(Path.GetDirectoryName(file)!);
                        string compiler = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
                        string[] arguments = OperatingSystem.IsWindows() ? ["/nologo", "/WX", "/Zs", file]
                            : ["-Werror", "-fsyntax-only", file];
                        await NativeBindingLayoutCommand.RunProcessAsync(compiler, arguments, Path.GetDirectoryName(file)!, token);
                    }, context.CancellationToken));
            }
            else
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingLinkCommand.PublishAsync(output, cache, Recovered,
                    async (file, candidate, token) =>
                    {
                        await CompileAsync(file, candidate, token);
                        await cancellation.CancelAsync();
                    }, cancellation.Token));
            }

            Assert.AreEqual(previous, await File.ReadAllTextAsync(manifest, context.CancellationToken));
            Assert.AreSequenceEqual(bytes, await File.ReadAllBytesAsync(artifact, context.CancellationToken));
            Assert.AreEqual(Original, await File.ReadAllTextAsync(Path.Join(Path.GetDirectoryName(artifact)!, "native-calls.c"), context.CancellationToken));
            Assert.IsEmpty(Directory.GetDirectories(output, "native-call-link-*"));
            Assert.DoesNotContain(Directory.Exists, stages);
            Assert.IsEmpty(Directory.GetFiles(output, "native-call-manifest-*"));
            Assert.AreEqual("41\n", await ExecutePublishedBodyAsync(artifact, directory));
            await NativeBindingLinkCommand.PublishAsync(output, cache, Recovered, CompileAsync, context.CancellationToken);
            string replacement = (await File.ReadAllTextAsync(manifest, context.CancellationToken)).Trim();
            Assert.AreNotEqual(artifact, replacement);
            Assert.AreEqual("42\n", await ExecutePublishedBodyAsync(replacement, directory));
            Assert.AreEqual(Recovered, await File.ReadAllTextAsync(Path.Join(Path.GetDirectoryName(replacement)!, "native-calls.c"), context.CancellationToken));
            Assert.AreSequenceEqual(bytes, await File.ReadAllBytesAsync(artifact, context.CancellationToken));
            Assert.IsEmpty(Directory.GetDirectories(output, "native-call-link-*"));
            await NativeBindingLinkCommand.PublishEmptyAsync(output, context.CancellationToken);
            Assert.AreEqual("", await File.ReadAllTextAsync(manifest, context.CancellationToken));
            Assert.AreEqual("42\n", await ExecutePublishedBodyAsync(replacement, directory));
            Assert.IsEmpty(Directory.GetDirectories(output, "native-call-link-*"));
            Assert.DoesNotContain(Directory.Exists, stages);
            Assert.IsEmpty(Directory.GetFiles(output, "native-call-manifest-*"));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }

        async Task CompileAsync(string file, string artifact, CancellationToken token)
        {
            stages.Add(Path.GetDirectoryName(file)!);
            string compiler = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
            string[] arguments = OperatingSystem.IsWindows() ? ["/nologo", "/WX", "/c", "/O2", "/Fo" + artifact, file]
                : ["-Werror", "-c", "-O2", file, "-o", artifact];
            await NativeBindingLayoutCommand.RunProcessAsync(compiler, arguments, Path.GetDirectoryName(file)!, token);
        }
    }

    /// <summary>
    /// No imports require no installation or compiler, while unknown accessors fail before replacing an existing manifest.
    /// </summary>
    [TestMethod]
    public async Task NativeLinkCommandValidatesImportsBeforeToolDiscovery()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-link-command-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync("extern int known(int value); extern int current; typedef int (*Operation)(int); extern Operation callback;",
                [new("known", "known", true), new("current", "current", false), new("callback", "callback", false)], directory);
            string contract = Path.Join(directory, "records.json");
            string consumer = Path.Join(directory, "consumer.obj");
            string output = Path.Join(directory, "output");
            Directory.CreateDirectory(output);
            string manifest = Path.Join(output, "native-call-libraries.txt");
            await File.WriteAllTextAsync(contract, JsonSerializer.Serialize(records, NativeBindingRecordWorker.JsonOptions), context.CancellationToken);
            await File.WriteAllTextAsync(manifest, "previous-object\n", context.CancellationToken);
            string[] arguments = [contract, "missing-pg-config", output, consumer, "missing-compiler", "", ""];
            byte[] invalid = await CompileNativeObjectAsync("extern void *ankus_native_body_missing(void); void *entry(void) { return ankus_native_body_missing(); }");
            await File.WriteAllBytesAsync(consumer, invalid, context.CancellationToken);
            FormatException error = await Assert.ThrowsExactlyAsync<FormatException>(() => NativeBindingLinkCommand.RunAsync(arguments, context.CancellationToken));
            Assert.Contains("Unknown or duplicate native call selection 'missing'", error.Message);
            Assert.AreEqual("previous-object\n", await File.ReadAllTextAsync(manifest, context.CancellationToken));
            Assert.IsEmpty(Directory.GetDirectories(output));
            byte[] global = await CompileNativeObjectAsync("extern void *ankus_native_global_body_read_current(void); void *entry(void) { return ankus_native_global_body_read_current(); }");
            await File.WriteAllBytesAsync(consumer, global, context.CancellationToken);
            FileNotFoundException missing = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => NativeBindingLinkCommand.RunAsync(arguments, context.CancellationToken));
            Assert.AreEqual("missing-pg-config", missing.FileName);
            Assert.AreEqual("previous-object\n", await File.ReadAllTextAsync(manifest, context.CancellationToken));
            int signature = NativeBindingIndirectModel.Describe(records.Graph).Single().FunctionType;
            string accessor = NativeBindingIndirectImports.Prefix + signature.ToString(CultureInfo.InvariantCulture);
            byte[] indirect = await CompileNativeObjectAsync("extern void *" + accessor + "(void); void *entry(void) { return " + accessor + "(); }");
            await File.WriteAllBytesAsync(consumer, indirect, context.CancellationToken);
            FileNotFoundException indirectMissing = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => NativeBindingLinkCommand.RunAsync(arguments, context.CancellationToken));
            Assert.AreEqual("missing-pg-config", indirectMissing.FileName);
            Assert.AreEqual("previous-object\n", await File.ReadAllTextAsync(manifest, context.CancellationToken));
            string callbackAccessor = NativeBindingCallbackImports.Prefix + signature.ToString(CultureInfo.InvariantCulture) + "_" + new string('1', 32);
            byte[] callback = await CompileNativeObjectAsync("extern void *" + callbackAccessor + "(void *); void *entry(void) { return " + callbackAccessor + "((void *)0); }");
            await File.WriteAllBytesAsync(consumer, callback, context.CancellationToken);
            FileNotFoundException callbackMissing = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => NativeBindingLinkCommand.RunAsync(arguments, context.CancellationToken));
            Assert.AreEqual("missing-pg-config", callbackMissing.FileName);
            Assert.AreEqual("previous-object\n", await File.ReadAllTextAsync(manifest, context.CancellationToken));
            byte[] empty = await CompileNativeObjectAsync("int no_imports(void) { return 42; }");
            await File.WriteAllBytesAsync(consumer, empty, context.CancellationToken);
            await NativeBindingLinkCommand.RunAsync(arguments, context.CancellationToken);
            Assert.AreEqual("", await File.ReadAllTextAsync(manifest, context.CancellationToken));
            Assert.AreSequenceEqual<string>([manifest], Directory.GetFiles(output));
            Assert.IsEmpty(Directory.GetDirectories(output));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Invalid command arity and cancellation fail before opening contracts or changing output.
    /// </summary>
    /// <param name="count">An unsupported command argument count.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
    [DataRow(9)]
    public async Task NativeLinkCommandRejectsInvalidArguments(int count)
    {
        ArgumentException failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            NativeBindingLinkCommand.RunAsync([.. Enumerable.Repeat("", count)], context.CancellationToken));
        Assert.AreEqual("arguments", failure.ParamName);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            NativeBindingLinkCommand.RunAsync([.. Enumerable.Repeat("missing", 7)], cancellation.Token));
    }

    private async Task<string> ExecutePublishedBodyAsync(string artifact, string directory)
    {
        string file = Path.Join(directory, "consumer-" + Guid.NewGuid().ToString("N") + ".c");
        string executable = Path.ChangeExtension(file, OperatingSystem.IsWindows() ? ".exe" : ".out");
        await File.WriteAllTextAsync(file, "#include <stdio.h>\nextern int native_published(void);\nint main(void) { printf(\"%d\\n\", native_published()); return 0; }\n",
            context.CancellationToken);
        string compiler = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
        string[] arguments = OperatingSystem.IsWindows() ? ["/nologo", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(file, ".obj"), file, artifact]
            : ["-Werror", file, artifact, "-o", executable];
        await RunAsync(compiler, arguments, directory);
        return (await RunAsync(executable, [], directory)).ReplaceLineEndings("\n");
    }
}
