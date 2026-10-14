using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Verifies an explicit native function set with the selected PostgreSQL installation and C compiler.
/// </summary>
internal static class NativeBindingSignatureCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes measured signatures only after the compiler accepts their exact native prototypes.
    /// </summary>
    /// <param name="arguments">A newline-delimited function list followed by the layout command's arguments.</param>
    /// <param name="cancellationToken">Cancels input, discovery, compilation or execution.</param>
    /// <returns>The verified native storage contract.</returns>
    internal static async Task<NativeBindingSignatures> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is < 4 or > 8)
        {
            throw new ArgumentException("Expected binding-signatures <function-list> <major> <pg_config> <output-directory> [compiler] [windows-library-directories] [runtime-identifier] [target-triple].", nameof(arguments));
        }

        string[] names = [.. (await File.ReadAllLinesAsync(arguments[0], cancellationToken)).Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => line.Trim())];
        string[] toolchain = arguments[1..];
        int major = int.Parse(toolchain[0], NumberStyles.None, CultureInfo.InvariantCulture);
        NativeBindingCatalog catalog = NativeBindingResources.ReadCatalog(major);
        NativeBindingRawCatalog raw = NativeBindingResources.ReadRawCatalog(major);
        string code = NativeBindingSignatureProbe.GenerateSource(catalog, raw, names, NativeBindingResources.ReadHeaders(major));
        PostgresInstallation installation = string.IsNullOrEmpty(toolchain[1])
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
            : await PostgresInstallation.CreateAsync(toolchain[1], cancellationToken);
        if (installation.Version.Major != major)
        {
            throw new InvalidOperationException("The selected installation has the wrong PostgreSQL major.");
        }

        string output = Path.GetFullPath(toolchain[2]);
        Directory.CreateDirectory(output);
        string source = Path.Join(output, "native-signatures.c");
        string executable = Path.Join(output, OperatingSystem.IsWindows() ? "native-signatures.exe" : "native-signatures");
        await File.WriteAllTextAsync(source, code, cancellationToken);
        string observations = await NativeBindingLayoutCommand.CompileProbeAsync(installation, toolchain, source, executable, output, cancellationToken, requireC11: true);
        NativeBindingSignatures signatures = NativeBindingSignatureProbe.Read(catalog, raw, names, observations);
        string expectedRuntime = toolchain.Length >= 6 && toolchain[5].Length != 0 ? toolchain[5] : RuntimeInformation.RuntimeIdentifier;
        if (signatures.RuntimeIdentifier != expectedRuntime)
        {
            throw new InvalidOperationException("Native signatures do not match the requested runtime ABI.");
        }

        await File.WriteAllTextAsync(Path.Join(output, "native-signatures.txt"), observations, cancellationToken);
        await File.WriteAllTextAsync(Path.Join(output, "native-signatures.json"), JsonSerializer.Serialize(signatures, s_jsonOptions) + "\n", cancellationToken);
        Console.WriteLine($"PG{major}: verified {signatures.Functions.Count} native function signatures.");
        return signatures;
    }
}
