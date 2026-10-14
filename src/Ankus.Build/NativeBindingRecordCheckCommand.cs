using System.Globalization;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Certifies collected native record contracts with the selected native compiler before publishing their checks.
/// </summary>
internal static class NativeBindingRecordCheckCommand
{
    /// <summary>
    /// Reads a complete collected contract, compiles every declaration and executes its physical bitfield checks.
    /// </summary>
    /// <param name="arguments">The records JSON, pg_config, output directory, compiler, Windows libraries and target triple.</param>
    /// <param name="cancellationToken">Cancels reading, compilation, execution or publication.</param>
    internal static async Task RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is < 3 or > 6)
        {
            throw new ArgumentException("Expected binding-record-checks <records-json> <pg_config> <output-directory> [compiler] [windows-library-directories] [target-triple].", nameof(arguments));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var input = new FileInfo(arguments[0]);
        if (input.Length > 512 * 1024 * 1024)
        {
            throw new InvalidDataException("Native record contract exceeds the byte limit.");
        }

        NativeHeaderRecords records;
        await using (FileStream stream = input.OpenRead())
        {
            records = await JsonSerializer.DeserializeAsync<NativeHeaderRecords>(stream, NativeBindingRecordWorker.JsonOptions, cancellationToken)
                ?? throw new FormatException("Empty native record contract.");
        }

        int major = records.Headers.Target.PostgresVersion / 10000;
        string source = NativeBindingRecordChecks.Generate(records, NativeBindingResources.ReadHeaders(major)) +
            NativeBindingRecordChecks.ExecutableEntryPoint;
        PostgresInstallation installation = arguments[1].Length == 0
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
            : await PostgresInstallation.CreateAsync(arguments[1], cancellationToken);
        string output = Path.GetFullPath(arguments[2]);
        string compiler = arguments.Length >= 4 && arguments[3].Length != 0 ? arguments[3] : OperatingSystem.IsWindows() ? "cl.exe" : "clang";
        string[] toolchain = [major.ToString(CultureInfo.InvariantCulture), arguments[1], output, compiler,
            arguments.Length >= 5 ? arguments[4] : "", records.Headers.Target.RuntimeIdentifier, arguments.Length >= 6 ? arguments[5] : ""];
        await PublishAsync(output, source, async (file, directory, token) =>
        {
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "checks.exe" : "checks");
            await NativeBindingLayoutCommand.CompileProbeAsync(installation, toolchain, file, executable, directory, token, requireC11: true);
        }, cancellationToken);
        Console.WriteLine($"PG{major}: compiled {records.Graph.Declarations.Count} native declaration contracts and executed their named bitfield checks.");
    }

    /// <summary>
    /// Publishes only after compiler and executable success, preserving prior evidence on failure or cancellation.
    /// </summary>
    /// <param name="output">The destination for successfully validated source.</param>
    /// <param name="source">The complete candidate executable source.</param>
    /// <param name="verify">The selected native compiler and executable verification.</param>
    /// <param name="cancellationToken">Cancels staging, verification and publication.</param>
    internal static async Task PublishAsync(string output, string source, Func<string, string, CancellationToken, Task> verify,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        string directory = Path.Join(output, "record-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Join(directory, "checks.c");
            await File.WriteAllTextAsync(file, source, cancellationToken);
            await verify(file, directory, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(file, Path.Join(output, "native-record-checks.c"), overwrite: true);
        }
        finally
        {
            await NativeBuildDirectory.DeleteAsync(directory);
        }
    }
}
