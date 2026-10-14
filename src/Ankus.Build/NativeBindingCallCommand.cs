using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Emits selected-header native call bodies after validating their complete C implementation.
/// </summary>
internal static class NativeBindingCallCommand
{
    /// <summary>
    /// Atomically publishes C call bodies while preserving any existing output when collection or compilation fails.
    /// </summary>
    /// <param name="arguments">Record collection arguments followed by an optional native body compiler.</param>
    /// <param name="cancellationToken">Cancels collection, C compilation and publication.</param>
    internal static async Task RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is < 4 or > 10)
        {
            throw new ArgumentException("Expected binding-call-sources <symbol-list> <major> <pg_config> <output-directory> [clang] [windows-library-directories] [runtime-identifier] [target-triple] [libclang] [native-compiler].", nameof(arguments));
        }

        cancellationToken.ThrowIfCancellationRequested();
        NativeHeaderRecords records = await NativeBindingRecordCommand.RunAsync(arguments.Length == 10 ? arguments[..9] : arguments, cancellationToken);
        int major = records.Headers.Target.PostgresVersion / 10000;
        string source = NativeBindingCallSource.Generate(records, NativeBindingResources.ReadHeaders(major));
        PostgresInstallation installation = arguments[2].Length == 0
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
            : await PostgresInstallation.CreateAsync(arguments[2], cancellationToken);
        string output = Path.GetFullPath(arguments[3]);
        string[] bodyArguments = [.. arguments.Take(8), .. Enumerable.Repeat("", Math.Max(0, 8 - arguments.Length))];
        bodyArguments[4] = arguments.Length == 10 && arguments[9].Length != 0 ? arguments[9]
            : OperatingSystem.IsWindows() ? "cl.exe" : bodyArguments[4];
        if (OperatingSystem.IsWindows() && Path.GetFileNameWithoutExtension(bodyArguments[4]).Equals("cl", StringComparison.OrdinalIgnoreCase))
        {
            // MSVC selects its target through the compiler executable. Generated target checks reject a mismatch.
            bodyArguments[7] = "";
        }

        await PublishAsync(output, source, (temporary, token) => NativeBindingHeaderCommand.InspectAsync(installation,
            bodyArguments, temporary, Path.Join(output, "native-call-checks.txt"),
            output, token, dumpAst: false, inspectBodies: true), cancellationToken);
        Console.WriteLine($"PG{major}: validated {records.Headers.Symbols.Count} fixed native call bodies for native guard integration.");
    }

    /// <summary>
    /// Replaces only the final source after its complete native validation succeeds and cancellation is rechecked.
    /// </summary>
    /// <param name="output">The existing command output directory.</param>
    /// <param name="source">The complete candidate source.</param>
    /// <param name="inspect">Compiles the candidate, throwing on any compiler or contract failure.</param>
    /// <param name="cancellationToken">Cancels writing, validation and publication.</param>
    internal static async Task PublishAsync(string output, string source, Func<string, CancellationToken, Task> inspect,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = Path.Join(output, "native-calls-" + Guid.NewGuid().ToString("N") + ".tmp.c");
        try
        {
            await File.WriteAllTextAsync(temporary, source, cancellationToken);
            await inspect(temporary, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Join(output, "native-calls.c"), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
