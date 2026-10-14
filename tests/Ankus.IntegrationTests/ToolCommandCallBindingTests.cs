using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// The installed command produces executable selected-header calls with an actual PostgreSQL by-value result.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolGeneratesExecutableNativeCalls()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "collected native calls");
        string selection = Path.Join(s_root, "call symbols.txt");
        await File.WriteAllTextAsync(selection, "FullTransactionIdFromU64\n", token);
        ProcessResult collected = await RunDotnetAsync(
            [helper, "binding-call-sources", selection, MajorText(), s_installation.PgConfigPath, output], token);
        collected.EnsureSuccess("dotnet", [helper, "binding-call-sources"]);
        string file = Path.Join(output, "execute.c");
        const string Source = """
            #include "native-calls.c"
            #include <stdio.h>
            int main(void)
            {
                uint64 value = UINT64CONST(0xfedcba9876543210);
                FullTransactionId result = {0};
                AnkusNativeCallArgument argument = { &value, sizeof(value) };
                int status = ankus_native_call_FullTransactionIdFromU64(&argument, 1, &result, sizeof(result));
                if (status != ANKUS_CALL_OK || result.value != UINT64CONST(0xfedcba9876543210)) return 1;
                value = 0;
                status = ankus_native_call_FullTransactionIdFromU64(&argument, 1, &result, sizeof(result));
                if (status != ANKUS_CALL_OK || result.value != 0) return 2;
                value = UINT64CONST(0xffffffffffffffff);
                status = ankus_native_call_FullTransactionIdFromU64(&argument, 1, &result, sizeof(result));
                if (status != ANKUS_CALL_OK || result.value != UINT64CONST(0xffffffffffffffff)) return 3;
                puts("PostgreSQL full transaction ID values retained");
                return 0;
            }
            """;
        await File.WriteAllTextAsync(file, Source, token);
        string compiler = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
        string executable = Path.Join(output, OperatingSystem.IsWindows() ? "execute.exe" : "execute");
        string[] options = OperatingSystem.IsWindows()
            ? ["/nologo", "/std:c11", "/WX", "/O2", "/Gy", "/Gw", "/I" + s_installation.ServerIncludeDirectory, "/I" + s_installation.IncludeDirectory,
                "/I" + Path.Join(s_installation.ServerIncludeDirectory, "port", "win32"),
                "/I" + Path.Join(s_installation.ServerIncludeDirectory, "port", "win32_msvc"),
                "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file, "/link", "/OPT:REF"]
            : [.. await s_installation.GetPreprocessorArgumentsAsync(token), "-std=gnu11", "-Wall", "-Wextra", "-Werror", "-O2",
                "-isystem", s_installation.ServerIncludeDirectory, "-isystem", s_installation.IncludeDirectory, file, "-o", executable,
                "-ffunction-sections", "-fdata-sections", OperatingSystem.IsMacOS() ? "-Wl,-dead_strip" : "-Wl,--gc-sections"];
        await PackageProcessRunner.RunCheckedAsync(compiler, options, s_environment, token, workingDirectory: output);
        ProcessResult execution = await PackageProcessRunner.RunCheckedAsync(executable, [], s_environment, token, workingDirectory: output);
        Assert.AreEqual("PostgreSQL full transaction ID values retained\n", execution.StandardOutput.ReplaceLineEndings("\n"));
        Assert.AreEqual("", execution.StandardError);
    }

    /// <summary>
    /// Unsupported call shapes preserve the prior source, then a valid selection replaces it successfully.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolPreservesCallSourcesAndRecovers()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "invalid native calls");
        Directory.CreateDirectory(output);
        string source = Path.Join(output, "native-calls.c");
        byte[] expected = "existing native call source\n"u8.ToArray();
        await File.WriteAllBytesAsync(source, expected, token);
        string selection = Path.Join(output, "symbols.txt");
        await File.WriteAllTextAsync(selection, "ExecutorRun_hook\n", token);
        ProcessResult failed = await RunDotnetAsync(
            [helper, "binding-call-sources", selection, MajorText(), s_installation.PgConfigPath, output], token);
        Assert.AreEqual(1, failed.ExitCode);
        Assert.Contains("requires a function declaration", failed.StandardError);
        Assert.AreSequenceEqual(expected, await File.ReadAllBytesAsync(source, token));
        Assert.IsEmpty(Directory.GetFiles(output, "native-calls-*.tmp.c"));
        await File.WriteAllTextAsync(selection, "FullTransactionIdFromU64\n", token);
        ProcessResult compilerFailure = await RunDotnetAsync(
            [helper, "binding-call-sources", selection, MajorText(), s_installation.PgConfigPath, output,
                "", "", "", "", "", Path.Join(output, "missing-native-compiler")], token);
        Assert.AreEqual(1, compilerFailure.ExitCode);
        Assert.Contains("missing-native-compiler", compilerFailure.StandardError);
        Assert.AreSequenceEqual(expected, await File.ReadAllBytesAsync(source, token));
        Assert.IsEmpty(Directory.GetFiles(output, "native-calls-*.tmp.c"));
        ProcessResult recovered = await RunDotnetAsync(
            [helper, "binding-call-sources", selection, MajorText(), s_installation.PgConfigPath, output,
                "", "", "", "", "", OperatingSystem.IsWindows() ? "cl.exe" : "clang"], token);
        recovered.EnsureSuccess("dotnet", [helper, "binding-call-sources"]);
        Assert.Contains("ankus_native_call_FullTransactionIdFromU64", await File.ReadAllTextAsync(source, token));
        Assert.IsEmpty(Directory.GetFiles(output, "native-calls-*.tmp.c"));
    }
}
