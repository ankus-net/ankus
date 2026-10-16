using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Exports a PostgreSQL installation's native build information on the machine it runs on, and imports it on a build
/// machine for that runtime identifier, as pgrx's target bundles do.
/// </summary>
internal static class NativeTargetCommand
{
    /// <summary>
    /// Gets the version that exported or must consume target information; bindings are tied to the generator that wrote them.
    /// </summary>
    internal static string Version { get; } = typeof(NativeTargetCommand).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("The Ankus build tool has no informational version.");

    /// <summary>
    /// Measures an installation's bindings with the selected toolchain and archives them with the headers they need.
    /// </summary>
    /// <param name="arguments">The major, pg_config path, archive path, native compiler, Windows library directories,
    /// runtime identifier, target triple, Clang, libclang and shared cache directory.</param>
    /// <param name="cancellationToken">Cancels measurement and archiving.</param>
    /// <returns>The export operation.</returns>
    internal static async Task ExportAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length != 10)
        {
            throw new ArgumentException("Expected target-export <major> <pg_config> <archive> <compiler> <windows-library-directories> <runtime-identifier> <target-triple> <clang> <libclang> <cache-directory>.", nameof(arguments));
        }

        int major = int.Parse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture);
        PostgresInstallation installation = arguments[1].Length == 0
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
            : await PostgresInstallation.CreateAsync(arguments[1], cancellationToken);
        if (installation.TargetInfo is not null)
        {
            throw new InvalidOperationException("Export target information on the target machine, from its pg_config.");
        }

        if (installation.Version.Major != major)
        {
            throw new InvalidOperationException($"Expected PostgreSQL {major}, but the selected installation is {installation.Label}.");
        }

        string runtime = arguments[5].Length != 0 ? arguments[5] : RuntimeInformation.RuntimeIdentifier;
        string directory = Directory.CreateTempSubdirectory("ankus-target-export-").FullName;
        try
        {
            string bindings = Path.Join(directory, "bindings");
            await NativeBindingSourceCommand.RunAsync([arguments[0], installation.PgConfigPath, bindings, arguments[3], arguments[4],
                runtime, arguments[6], arguments[7], arguments[8], arguments[9]], cancellationToken);
            IReadOnlyList<string> headers = OperatingSystem.IsWindows() ? []
                : await ReadDependenciesAsync(installation, arguments, runtime, directory, cancellationToken);
            await PostgresTargetInfo.CreateArchiveAsync(installation, runtime, arguments[6], Version,
                NativeBindingSourceCache.Artifacts.ToDictionary(static name => name, name => Path.Join(bindings, name)),
                headers, arguments[2], cancellationToken);
            Console.WriteLine($"Target information: PostgreSQL {installation.Version} on {runtime} written to {Path.GetFullPath(arguments[2])}.");
        }
        finally
        {
            await NativeBuildDirectory.DeleteAsync(directory);
        }
    }

    /// <summary>
    /// Copies bindings measured on the target after checking that this build can consume them.
    /// </summary>
    /// <param name="target">The extracted target information.</param>
    /// <param name="major">The build's PostgreSQL major.</param>
    /// <param name="runtimeIdentifier">The build's runtime identifier, or an empty string.</param>
    /// <param name="output">The binding output directory.</param>
    /// <param name="cancellationToken">Cancels verification and copying.</param>
    /// <returns>The import operation.</returns>
    internal static async Task ImportAsync(PostgresTargetInfo target, int major, string runtimeIdentifier, string output,
        CancellationToken cancellationToken)
    {
        if (target.Version.Major != major)
        {
            throw new InvalidOperationException($"The target information describes PostgreSQL {target.Version.Major}, but this build selects {major}.");
        }

        if (runtimeIdentifier.Length != 0 && runtimeIdentifier != target.RuntimeIdentifier)
        {
            throw new InvalidOperationException($"The target information describes {target.RuntimeIdentifier}, but this build targets {runtimeIdentifier}.");
        }

        if (target.AnkusVersion != Version)
        {
            throw new InvalidOperationException($"The target information was exported by Ankus {target.AnkusVersion}, but this build uses {Version}. Export it again with this version.");
        }

        await target.VerifyAsync(cancellationToken);
        Directory.CreateDirectory(output);
        foreach (string name in NativeBindingSourceCache.Artifacts)
        {
            string source = target.GetBindingPath(name);
            string destination = Path.Join(output, name);
            if (!File.Exists(destination) || await NativeBindingCache.HashAsync(destination, cancellationToken) !=
                await NativeBindingCache.HashAsync(source, cancellationToken))
            {
                File.Copy(source, destination, overwrite: true);
            }
        }

        Console.WriteLine($"Native binding sources: imported from PostgreSQL {target.Version} on {target.RuntimeIdentifier}.");
    }

    /// <summary>
    /// Lists every header the binding translation unit reads, as the selected Clang resolves them.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadDependenciesAsync(PostgresInstallation installation, string[] arguments,
        string runtime, string directory, CancellationToken cancellationToken)
    {
        string compiler = arguments[7].Length != 0 ? NativeBindingRecordCommand.FindCompiler(arguments[7])
            : await ClangSelection.FindAsync(cancellationToken);
        List<string> options = await NativeBindingHeaderCommand.CreateArgumentsAsync(installation,
            ["", arguments[0], installation.PgConfigPath, directory, compiler, arguments[4], runtime, arguments[6]], cancellationToken);
        string input = Path.Join(directory, "native-target-input.c");
        await File.WriteAllTextAsync(input, NativeBindingResources.ReadManifest(installation.Version.Major), cancellationToken);
        string rule = await NativeBindingLayoutCommand.RunProcessAsync(compiler, [.. options, "-M", input], directory, cancellationToken);
        return [.. ParseDependencies(rule).Skip(1).Where(static path => Path.IsPathFullyQualified(path)).Select(Path.GetFullPath)];
    }

    /// <summary>
    /// Splits a make rule into its target and prerequisites, honoring escaped spaces and line continuations.
    /// </summary>
    /// <param name="rule">The compiler's dependency output.</param>
    /// <returns>The rule's words, without the target's colon.</returns>
    internal static IEnumerable<string> ParseDependencies(string rule)
    {
        var word = new StringBuilder();
        for (int index = 0; index < rule.Length; index++)
        {
            char current = rule[index];
            if (current == '\\' && index + 1 < rule.Length && rule[index + 1] is '\n' or '\r')
            {
                index++;
                continue;
            }

            if (current == '\\' && index + 1 < rule.Length && rule[index + 1] is ' ' or '#' or '\\')
            {
                word.Append(rule[++index]);
                continue;
            }

            if (current == '$' && index + 1 < rule.Length && rule[index + 1] == '$')
            {
                word.Append('$');
                index++;
                continue;
            }

            if (char.IsWhiteSpace(current))
            {
                if (word.Length != 0)
                {
                    yield return Finish(word);
                }

                continue;
            }

            word.Append(current);
        }

        if (word.Length != 0)
        {
            yield return Finish(word);
        }

        static string Finish(StringBuilder word)
        {
            string value = word.ToString();
            word.Clear();
            return value.EndsWith(':') ? value[..^1] : value;
        }
    }
}
