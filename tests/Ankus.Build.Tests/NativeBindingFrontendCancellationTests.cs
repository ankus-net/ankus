using System.Diagnostics;
using System.Globalization;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Cancelling an active frontend joins its process before returning and releases its diagnostic output file.
    /// </summary>
    [TestMethod]
    public async Task NativeFrontendCancellationJoinsProcessAndReleasesFiles()
    {
        const string Source = """
            #include <stdio.h>
            #include <string.h>
            #if defined(_WIN32)
            #include <windows.h>
            #include <process.h>
            #define PROCESS_ID _getpid
            #define WAIT() Sleep(10)
            #else
            #include <unistd.h>
            #define PROCESS_ID getpid
            #define WAIT() sleep(1)
            #endif
            static FILE *open_file(const char *name)
            {
                FILE *stream = NULL;
            #if defined(_WIN32)
                if (fopen_s(&stream, name, "w") != 0) return NULL;
            #else
                stream = fopen(name, "w");
            #endif
                return stream;
            }
            int main(int argc, char **argv)
            {
                if (argc == 2 && strcmp(argv[1], "fail") == 0)
                {
                    puts("native stdout diagnostic");
                    fputs("native stderr diagnostic\n", stderr);
                    return 3;
                }

                FILE *identity = open_file(argv[argc - 2]);
                if (identity == NULL) return 1;
                fprintf(identity, "%d", (int) PROCESS_ID());
                fclose(identity);
                FILE *ready = open_file(argv[argc - 1]);
                if (ready == NULL) return 2;
                fclose(ready);
                for (;;) { WAIT(); }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), $"ankus-frontend-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        Task? frontend = null;
        try
        {
            string file = Path.Join(directory, "frontend.c");
            await File.WriteAllTextAsync(file, Source, context.CancellationToken);
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "frontend.exe" : "frontend");
            string compiler = await HostCompilers.ClangAsync();
            string[] options = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", file, "-o", executable];
            await RunAsync(compiler, options, directory);
            InvalidOperationException diagnostic = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(executable, ["fail"], Path.Join(directory, "failure.txt"),
                    directory, context.CancellationToken, inspectBodies: true));
            Assert.Contains("exited with 3", diagnostic.Message);
            Assert.Contains("native stdout diagnostic", diagnostic.Message);
            Assert.Contains("native stderr diagnostic", diagnostic.Message);
            string identity = Path.Join(directory, "identity.txt");
            string ready = Path.Join(directory, "ready.txt");
            string output = Path.Join(directory, "output.txt");
            frontend = NativeBindingHeaderCommand.CompileAsync(executable, [identity, ready], output, directory, cancellation.Token, inspectBodies: true);
            while (!File.Exists(ready))
            {
                if (frontend.IsCompleted)
                {
                    await frontend;
                }

                await Task.Delay(10, context.CancellationToken);
            }

            using Process child = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(identity, context.CancellationToken), CultureInfo.InvariantCulture));
            Assert.IsFalse(child.HasExited);
            await cancellation.CancelAsync();
            OperationCanceledException failure = await Assert.ThrowsAsync<OperationCanceledException>(() => frontend);
            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
            child.Refresh();
            Assert.IsTrue(child.HasExited);
            using FileStream released = File.Open(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.AreEqual(0L, released.Length);
        }
        finally
        {
            await cancellation.CancelAsync();
            if (frontend is not null)
            {
                await frontend.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await DeleteDirectoryAsync(directory);
        }
    }
}
