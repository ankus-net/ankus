using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ankus.Build;

/// <summary>
/// Selects the Clang that reads PostgreSQL's headers when the build names none.
/// </summary>
internal static partial class ClangSelection
{
    /// <summary>
    /// The oldest Clang major whose frontend supports the declaration collector.
    /// </summary>
    internal const int MinimumMajor = 20;

    /// <summary>
    /// Returns the first Clang 20 or later among the <c>PATH</c> entry and the standard LLVM installation locations.
    /// macOS puts Xcode's older Apple clang on <c>PATH</c> and Homebrew keeps its LLVM off it, so a newer Clang that is
    /// installed would otherwise go unused.
    /// </summary>
    /// <param name="cancellationToken">Cancels the version queries.</param>
    /// <returns>The full path of the selected Clang.</returns>
    internal static Task<string> FindAsync(CancellationToken cancellationToken)
        => FindAsync(GetCandidates(), cancellationToken);

    /// <summary>
    /// Returns the first candidate that is Clang 20 or later, or reports each one that was too old.
    /// </summary>
    /// <param name="candidates">Clang executables in preference order; missing paths are skipped.</param>
    /// <param name="cancellationToken">Cancels the version queries.</param>
    /// <returns>The full path of the selected Clang.</returns>
    internal static async Task<string> FindAsync(IEnumerable<string> candidates, CancellationToken cancellationToken)
    {
        var rejected = new List<string>();
        foreach (string candidate in candidates.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).Where(File.Exists))
        {
            (string name, int major)? version = await GetVersionAsync(candidate, cancellationToken);
            if (version is (_, >= MinimumMajor))
            {
                return candidate;
            }

            rejected.Add(version is (string name, int found) ? $"'{candidate}' is {name} {found.ToString(CultureInfo.InvariantCulture)}"
                : $"'{candidate}' did not report its version");
        }

        string summary = rejected.Count == 0 ? "none was found" : string.Join(", ", rejected);
        throw new FileNotFoundException($"Ankus needs LLVM Clang {MinimumMajor.ToString(CultureInfo.InvariantCulture)} or later to read " +
            $"PostgreSQL's headers, and {summary}. {InstallHint} Ankus finds it there, or set AnkusClangPath to its clang.");
    }

    /// <summary>
    /// Reads a Clang driver's vendor name and major version from <c>--version</c>.
    /// </summary>
    /// <param name="compiler">The Clang executable.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The name, such as <c>Apple clang</c>, and the major version, or null when the output has neither.</returns>
    internal static async Task<(string Name, int Major)?> GetVersionAsync(string compiler, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(compiler)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--version");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {compiler}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, errors);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        Match match = VersionLine().Match(await output);
        return process.ExitCode == 0 && match.Success
            ? (match.Groups["apple"].Success ? "Apple clang" : "Clang", int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static IEnumerable<string> GetCandidates()
    {
        string name = OperatingSystem.IsWindows() ? "clang-cl.exe" : "clang";
        string[] path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string directory in path)
        {
            yield return Path.Join(directory.Trim('"'), name);
        }

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", name);
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            foreach (string prefix in new[] { "/opt/homebrew/opt", "/usr/local/opt" })
            {
                yield return Path.Join(prefix, "llvm", "bin", "clang");
                foreach (string versioned in Newest(prefix, "llvm@"))
                {
                    yield return Path.Join(versioned, "bin", "clang");
                }
            }

            yield break;
        }

        // Debian and Ubuntu install each LLVM release under /usr/lib/llvm-N and link it as clang-N.
        foreach (string directory in path)
        {
            foreach (string versioned in Newest(directory.Trim('"'), "clang-"))
            {
                yield return versioned;
            }
        }

        foreach (string versioned in Newest("/usr/lib", "llvm-"))
        {
            yield return Path.Join(versioned, "bin", "clang");
        }
    }

    // Lists the entries named <prefix><major> in a directory, newest major first.
    private static IEnumerable<string> Newest(string directory, string prefix)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFileSystemEntries(directory, prefix + "*")
            .Select(entry => (Entry: entry, Major: int.TryParse(Path.GetFileName(entry).AsSpan(prefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out int major) ? major : -1))
            .Where(static entry => entry.Major >= 0)
            .OrderByDescending(static entry => entry.Major)
            .Select(static entry => entry.Entry);
    }

    private static string InstallHint => OperatingSystem.IsMacOS() ? "Install it with 'brew install llvm';"
        : OperatingSystem.IsWindows() ? "Install LLVM from https://github.com/llvm/llvm-project/releases;"
        : "Install your distribution's clang package or one from https://apt.llvm.org;";

    [GeneratedRegex(@"(?<apple>Apple )?clang version (?<major>\d+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex VersionLine();
}
