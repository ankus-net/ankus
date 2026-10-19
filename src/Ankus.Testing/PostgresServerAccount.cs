using System.Diagnostics;

namespace Ankus.Testing;

/// <summary>
/// Runs PostgreSQL server tools and owns server storage as the current account, or as another Unix account through
/// <c>sudo -u</c>, as <c>cargo pgrx test --runas</c> does.
/// </summary>
internal sealed class PostgresServerAccount
{
    private PostgresServerAccount(string? name) => Name = name;

    /// <summary>
    /// Gets the account running the test process.
    /// </summary>
    internal static PostgresServerAccount Current { get; } = new(null);

    /// <summary>
    /// Gets the other account's name, or null for the current account.
    /// </summary>
    internal string? Name { get; }

    /// <summary>
    /// Selects the account that owns and runs a test server.
    /// </summary>
    /// <param name="name">A Unix account name, or null for the current account.</param>
    /// <returns>The selected account.</returns>
    /// <exception cref="ArgumentException">The name is blank, begins with a dash or contains a control character.</exception>
    /// <exception cref="PlatformNotSupportedException">Another account is requested on Windows.</exception>
    internal static PostgresServerAccount Create(string? name)
    {
        if (name is null)
        {
            return Current;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.StartsWith('-') || name.Any(char.IsControl) || name.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The server account must be a Unix account name.", nameof(name));
        }

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Running the test server as another account is not supported on Windows.");
        }

        return new PostgresServerAccount(name);
    }

    /// <summary>
    /// Runs a PostgreSQL tool as this account, passing the selected environment to it explicitly through
    /// <c>env</c> because <c>sudo</c> resets the environment.
    /// </summary>
    /// <param name="fileName">The executable's absolute path.</param>
    /// <param name="arguments">Individual command-line arguments.</param>
    /// <param name="environment">Environment variables to set; null values are omitted.</param>
    /// <param name="cancellationToken">Cancels and terminates the process.</param>
    /// <param name="captureOutput">Whether to redirect output; otherwise the child inherits the host's handles.</param>
    /// <returns>The captured process result.</returns>
    internal Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken, bool captureOutput = true)
    {
        if (Name is null)
        {
            return ProcessRunner.RunAsync(fileName, arguments, environment, cancellationToken, captureOutput);
        }

        string[] assignments = [.. environment.Where(static pair => pair.Value is not null).Select(static pair => pair.Key + "=" + pair.Value)];
        return ProcessRunner.RunAsync("sudo", ["-u", Name, "--", "env", .. assignments, fileName, .. arguments],
            new Dictionary<string, string?>(), cancellationToken, captureOutput, workingDirectory: "/", terminateProcessTree: false);
    }

    /// <summary>
    /// Runs a PostgreSQL tool as this account and throws a detailed exception when it fails.
    /// </summary>
    /// <param name="fileName">The executable's absolute path.</param>
    /// <param name="arguments">Individual command-line arguments.</param>
    /// <param name="environment">Environment variables to set; null values are omitted for another account.</param>
    /// <param name="cancellationToken">Cancels and terminates the process.</param>
    /// <param name="captureOutput">Whether to redirect output; otherwise the child inherits the host's handles.</param>
    /// <returns>The successful process result.</returns>
    internal async Task<ProcessResult> RunCheckedAsync(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken, bool captureOutput = true)
    {
        ProcessResult result = await RunAsync(fileName, arguments, environment, cancellationToken, captureOutput).ConfigureAwait(false);
        result.EnsureSuccess(fileName, arguments);
        return result;
    }

    /// <summary>
    /// Creates a directory and its missing parents owned by this account.
    /// </summary>
    /// <param name="path">The absolute directory path.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>A task completing after creation.</returns>
    internal async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (Name is null)
        {
            Directory.CreateDirectory(path);
            return;
        }

        await RunSudoCheckedAsync(["mkdir", "-p", "--", path], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes UTF-8 text to a file owned by this account, replacing any existing content.
    /// </summary>
    /// <param name="path">The absolute file path.</param>
    /// <param name="contents">The text to write.</param>
    /// <param name="cancellationToken">Cancels writing.</param>
    /// <returns>A task completing after the file is written.</returns>
    internal async Task WriteFileAsync(string path, string contents, CancellationToken cancellationToken)
    {
        if (Name is null)
        {
            await File.WriteAllTextAsync(path, contents, cancellationToken).ConfigureAwait(false);
            return;
        }

        using Process process = StartSudo(["tee", "--", path], redirectInput: true);
        await using (StreamWriter input = process.StandardInput)
        {
            await input.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await CompleteAsync(process, ["tee", "--", path], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a file that this account owns, or returns an empty string when it does not exist.
    /// </summary>
    /// <param name="path">The absolute file path.</param>
    /// <returns>The file's UTF-8 text.</returns>
    internal string ReadFile(string path)
    {
        if (Name is null)
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        string[] arguments = ["sh", "-c", "if test -e \"$1\"; then exec cat -- \"$1\"; fi", "sh", path];
        using Process process = StartSudo(arguments, redirectInput: false);
        Task<string> error = process.StandardError.ReadToEndAsync();
        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"sudo -u {Name} could not read '{path}' (exit {process.ExitCode}): {error.GetAwaiter().GetResult()}");
        }

        return text;
    }

    /// <summary>
    /// Deletes a directory tree that belonged to a stopped server.
    /// </summary>
    /// <param name="path">The absolute directory path. A missing directory is already deleted.</param>
    internal void DeleteDirectory(string path)
    {
        if (Name is null)
        {
            Ankus.PgConfig.PostgresServerStorage.Delete(path);
            return;
        }

        string[] arguments = ["rm", "-rf", "--", path];
        using Process process = StartSudo(arguments, redirectInput: false);
        Task<string> error = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException($"sudo -u {Name} could not delete '{path}' (exit {process.ExitCode}): {error.GetAwaiter().GetResult()}");
        }
    }

    private async Task RunSudoCheckedAsync(string[] arguments, CancellationToken cancellationToken)
    {
        using Process process = StartSudo(arguments, redirectInput: false);
        await CompleteAsync(process, arguments, cancellationToken).ConfigureAwait(false);
    }

    private Process StartSudo(string[] arguments, bool redirectInput)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("sudo")
            {
                UseShellExecute = false,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = "/",
            },
        };
        string[] command = ["-u", Name!, "--", .. arguments];
        foreach (string argument in command)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        return process;
    }

    private async Task CompleteAsync(Process process, string[] arguments, CancellationToken cancellationToken)
    {
        Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"sudo -u {Name} {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {await error.ConfigureAwait(false)}");
        }
    }
}
