using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Observes actual include resolution and macro expansion before reusing collected declarations.
/// </summary>
internal static class NativeBindingPreprocessor
{
    /// <summary>
    /// Hashes the complete current preprocessing result, including unexpanded macro definitions.
    /// </summary>
    /// <param name="compiler">The selected Clang driver.</param>
    /// <param name="options">The semantic collector's language, diagnostic and target arguments.</param>
    /// <param name="source">The complete header and synthetic-root translation unit.</param>
    /// <param name="directory">Owned working storage for this observation.</param>
    /// <param name="cancellationToken">Cancels the compiler and joins its output before returning.</param>
    internal static async Task<NativeBindingPreprocessed> ObserveAsync(string compiler, IReadOnlyList<string> options,
        string source, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        directory = NativeBuildDirectory.PhysicalPath(new DirectoryInfo(directory));
        const string Input = "native-binding-input.c";
        await File.WriteAllTextAsync(Path.Join(directory, Input), source, cancellationToken);
        string output = Path.Join(directory, "native-binding-input.i");
        string[] preprocessing = OperatingSystem.IsWindows()
            ? ["/clang:-E", "/clang:-P", "/clang:-dD"] : ["-E", "-P", "-dD"];
        await NativeBindingHeaderCommand.CompileAsync(compiler, [.. options, .. preprocessing, Input], output,
            directory, cancellationToken, inspectBodies: true);
        const string Version = "#define __clang_major__ ";
        int major = 0;
        foreach (string line in File.ReadLines(output))
        {
            if (!line.StartsWith(Version, StringComparison.Ordinal))
            {
                continue;
            }

            if (major != 0 || !int.TryParse(line.AsSpan(Version.Length), NumberStyles.None, CultureInfo.InvariantCulture, out major))
            {
                throw new FormatException("Native preprocessing requires one supported Clang identity.");
            }
        }

        if (major == 0)
        {
            throw new FormatException("Native preprocessing did not retain its Clang identity.");
        }

        if (major < ClangSelection.MinimumMajor)
        {
            throw new InvalidOperationException($"'{compiler}' is Clang {major.ToString(CultureInfo.InvariantCulture)}, and Ankus needs LLVM " +
                $"Clang {ClangSelection.MinimumMajor.ToString(CultureInfo.InvariantCulture)} or later to read PostgreSQL's headers. " +
                "Set AnkusClangPath to a newer clang, or leave it unset so Ankus finds one.");
        }

        // Ask the driver for its effective frontend command. Default and explicit
        // configuration files can change semantic options without changing C tokens.
        string invocation = await NativeBindingHeaderCommand.CompileAsync(compiler,
            [.. options, "-###",
                OperatingSystem.IsWindows() ? "/Zs" : "-fsyntax-only", Input],
            Path.Join(directory, "native-binding-driver.txt"), directory, cancellationToken, inspectBodies: true);
        if (!invocation.Contains("\"-cc1\"", StringComparison.Ordinal))
        {
            throw new FormatException("The selected Clang driver did not report its semantic frontend invocation.");
        }

        foreach (string path in new[] { directory.Replace("\\", "\\\\", StringComparison.Ordinal), directory }.Distinct(StringComparer.Ordinal))
        {
            foreach (string option in new[] { "-fdebug-compilation-dir=", "-fcoverage-compilation-dir=" })
            {
                invocation = invocation.Replace(option + path + "\"", option + "/_/Ankus.Postgres\"", StringComparison.Ordinal);
            }
        }

        return new(await NativeBindingCache.HashAsync(output, cancellationToken), major,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation))));
    }
}

/// <summary>
/// Identifies the selected frontend and its complete observed preprocessing content.
/// </summary>
/// <param name="Hash">The current content hash, independent of temporary source locations.</param>
/// <param name="ClangMajor">The frontend's predefined major version.</param>
/// <param name="InvocationHash">The effective semantic command, including compiler configuration and diagnostic options.</param>
internal sealed record NativeBindingPreprocessed(string Hash, int ClangMajor, string InvocationHash);
