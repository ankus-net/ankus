namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Clang treats PostgreSQL and platform headers as external inputs while MSVC retains its native include form.
    /// </summary>
    /// <param name="compiler">The selected Windows C frontend.</param>
    /// <param name="expected">The exact expected argument sequence.</param>
    [TestMethod]
    [DataRow("clang-cl", new[] { "/imsvc", "first", "/imsvc", "second" })]
    [DataRow("clang-cl.exe", new[] { "/imsvc", "first", "/imsvc", "second" })]
    [DataRow("C:\\LLVM\\bin\\CLANG-CL.EXE", new[] { "/imsvc", "first", "/imsvc", "second" })]
    [DataRow("cl.exe", new[] { "/Ifirst", "/Isecond" })]
    public void WindowsHeaderIncludesPreserveTheSelectedCompilerContract(string compiler, string[] expected)
    {
        var options = new List<string>();
        NativeBindingHeaderCommand.AddWindowsIncludes(options, compiler, ["first", "second"]);
        Assert.AreSequenceEqual(expected, options);
    }

    /// <summary>
    /// Actual preprocessing observes same-timestamp edits, unused macros, include precedence and target options.
    /// </summary>
    [TestMethod]
    public async Task NativePreprocessingTracksContentAndIncludeResolution()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-preprocessing-").FullName;
        try
        {
            string earlier = Directory.CreateDirectory(Path.Join(directory, "earlier")).FullName;
            string later = Directory.CreateDirectory(Path.Join(directory, "later")).FullName;
            string relocated = Directory.CreateDirectory(Path.Join(directory, "relocated")).FullName;
            string file = Path.Join(later, "binding-input.h");
            const string Header = "#define WIDTH 3\n#define UNUSED 37\ntypedef struct Entry { int cells[WIDTH]; } Entry;\n";
            const string Source = "#include <binding-input.h>\n#ifdef EXTRA\nextern Entry additional;\n#endif\n";
            string[] options = OperatingSystem.IsWindows() ? ["/nologo", "/WX", "/I" + earlier, "/I" + later]
                : ["-Werror", "-I", earlier, "-I", later];
            await File.WriteAllTextAsync(file, Header, context.CancellationToken);
            DateTime timestamp = File.GetLastWriteTimeUtc(file);
            NativeBindingPreprocessed original = await ObserveAsync(directory, options);
            Assert.IsGreaterThanOrEqualTo(20, original.ClangMajor);
            Assert.AreEqual(original, await ObserveAsync(relocated, options));
            if (!OperatingSystem.IsWindows())
            {
                string alias = Path.Join(directory, "linked staging");
                Directory.CreateSymbolicLink(alias, relocated);
                Assert.AreEqual(original, await ObserveAsync(alias, options));
            }

            await File.WriteAllTextAsync(file, Header.Replace("WIDTH 3", "WIDTH 4", StringComparison.Ordinal), context.CancellationToken);
            File.SetLastWriteTimeUtc(file, timestamp);
            NativeBindingPreprocessed resized = await ObserveAsync(directory, options);
            Assert.AreNotEqual(original.Hash, resized.Hash);
            await File.WriteAllTextAsync(file, Header.Replace("UNUSED 37", "UNUSED 38", StringComparison.Ordinal), context.CancellationToken);
            File.SetLastWriteTimeUtc(file, timestamp);
            NativeBindingPreprocessed macro = await ObserveAsync(directory, options);
            Assert.AreNotEqual(original.Hash, macro.Hash);
            Assert.AreNotEqual(resized.Hash, macro.Hash);

            string shadow = Path.Join(earlier, "binding-input.h");
            await File.WriteAllTextAsync(shadow, Header, context.CancellationToken);
            Assert.AreEqual(original, await ObserveAsync(directory, options));
            await File.WriteAllTextAsync(shadow, Header.Replace("WIDTH 3", "WIDTH 9", StringComparison.Ordinal), context.CancellationToken);
            NativeBindingPreprocessed selected = await ObserveAsync(directory, options);
            Assert.AreNotEqual(original.Hash, selected.Hash);
            Assert.AreNotEqual(macro.Hash, selected.Hash);
            NativeBindingPreprocessed defined = await ObserveAsync(directory, [.. options, OperatingSystem.IsWindows() ? "/DEXTRA" : "-DEXTRA"]);
            Assert.AreNotEqual(selected.Hash, defined.Hash);
            File.Delete(shadow);
            Assert.AreEqual(macro, await ObserveAsync(directory, options));

            async Task<NativeBindingPreprocessed> ObserveAsync(string working, string[] arguments)
                => await NativeBindingPreprocessor.ObserveAsync(await HostCompilers.ClangAsync(),
                    arguments, Source, working, context.CancellationToken);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Semantic compiler configuration changes invalidate reuse even when preprocessing produces identical tokens.
    /// </summary>
    [TestMethod]
    public async Task NativePreprocessingTracksEffectiveCompilerConfiguration()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-preprocessing-options-").FullName;
        try
        {
            string file = Path.Join(directory, "semantic.cfg");
            string compiler = await HostCompilers.ClangAsync();
            string prefix = OperatingSystem.IsWindows() ? "/clang:" : "";
            string[] options = ["--config=" + file];
            const string Source = "typedef struct Entry { char first; int second; } Entry;\n";
            await File.WriteAllTextAsync(file, prefix + "-fpack-struct=1\n", context.CancellationToken);
            DateTime timestamp = File.GetLastWriteTimeUtc(file);
            NativeBindingPreprocessed original = await NativeBindingPreprocessor.ObserveAsync(compiler, options, Source, directory, context.CancellationToken);
            await File.WriteAllTextAsync(file, prefix + "-fpack-struct=2\n", context.CancellationToken);
            File.SetLastWriteTimeUtc(file, timestamp);
            NativeBindingPreprocessed changed = await NativeBindingPreprocessor.ObserveAsync(compiler, options, Source, directory, context.CancellationToken);
            Assert.AreEqual(original.Hash, changed.Hash);
            Assert.AreNotEqual(original.InvocationHash, changed.InvocationHash);
            await File.WriteAllTextAsync(file, prefix + "-fpack-struct=1\n", context.CancellationToken);
            Assert.AreEqual(original, await NativeBindingPreprocessor.ObserveAsync(compiler, options, Source, directory, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A failed observation's message starts with the compiler's error, the line a build reports, rather than with the
    /// macro definitions the compiler wrote before failing. A missing PostgreSQL header showed only <c>#define __llvm__ 1</c>.
    /// </summary>
    [TestMethod]
    public async Task NativePreprocessingFailureStartsWithTheCompilerError()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-preprocessing-missing-").FullName;
        try
        {
            string compiler = await HostCompilers.ClangAsync();
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingPreprocessor.ObserveAsync(compiler, [], "#include \"postgres.h\"\n", directory, context.CancellationToken));
            string first = failure.Message.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
            Assert.Contains("postgres.h", first);
            Assert.DoesNotContain("#define", first);
            Assert.Contains("#define", failure.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Failed and cancelled observations cannot provide a content identity; a corrected compiler input recovers.
    /// </summary>
    [TestMethod]
    public async Task NativePreprocessingRejectsCompilerFailuresAndRecovers()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-preprocessing-failure-").FullName;
        try
        {
            string compiler = await HostCompilers.ClangAsync();
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingPreprocessor.ObserveAsync(compiler, [], "#error deliberate_preprocessing_failure\n", directory, context.CancellationToken));
            Assert.Contains("deliberate_preprocessing_failure", failure.Message);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                NativeBindingPreprocessor.ObserveAsync(compiler, [], "int value;\n", directory, cancellation.Token));
            NativeBindingPreprocessed recovered = await NativeBindingPreprocessor.ObserveAsync(compiler, [], "int value;\n", directory, context.CancellationToken);
            Assert.AreEqual(64, recovered.Hash.Length);
            Assert.AreEqual(recovered, await NativeBindingPreprocessor.ObserveAsync(compiler, [], "int value;\n", directory, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
