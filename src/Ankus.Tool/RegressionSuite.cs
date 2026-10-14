using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Describes one project's PostgreSQL SQL and expected-output files without changing them during discovery.
/// </summary>
internal sealed class RegressionSuite
{
    /// <summary>
    /// Discovers exact SQL test names in stable order beneath the extension project.
    /// </summary>
    internal RegressionSuite(string project)
    {
        DirectoryPath = Path.Join(Path.GetDirectoryName(project)!, "pg_regress");
        RequireRepresentable(DirectoryPath);
        string sql = Path.Join(DirectoryPath, "sql");
        Names = Directory.Exists(sql)
            ? [.. Directory.EnumerateFiles(sql).Where(static path => Path.GetExtension(path) == ".sql")
                .Select(static path => Path.GetFileNameWithoutExtension(path)).Order(StringComparer.Ordinal)]
            : [];
        foreach (string name in Names)
        {
            RequireRepresentable(name);
            if (name.Contains('\\', StringComparison.Ordinal))
            {
                throw new ArgumentException($"pg_regress cannot safely represent SQL test name '{name}'.");
            }
        }
    }

    /// <summary>
    /// Gets the suite's input/output directory.
    /// </summary>
    internal string DirectoryPath { get; }

    /// <summary>
    /// Gets all SQL tests, including setup when present, in ordinal filename order.
    /// </summary>
    internal IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Reports whether setup was changed after its expected output was recorded.
    /// </summary>
    internal bool SetupChanged => Names.Contains("setup") &&
        (!HasExpected("setup") || File.GetLastWriteTimeUtc(Path.Join(DirectoryPath, "sql", "setup.sql")) > File.GetLastWriteTimeUtc(Expected("setup")));

    /// <summary>
    /// Selects ordinary tests and rejects an explicit filter that has no ordinary match.
    /// </summary>
    internal string[] Select(string? filter)
    {
        string[] selected = [.. Names.Where(name => name != "setup" && (filter is null || name.Contains(filter, StringComparison.Ordinal)))];
        if (filter is not null && selected.Length == 0)
        {
            throw new ArgumentException($"No regression tests match '{filter}'.");
        }

        return selected;
    }

    /// <summary>
    /// Reports whether the primary expected file is present.
    /// </summary>
    internal bool HasExpected(string name) => File.Exists(Expected(name));

    /// <summary>
    /// Requires expected output for explicit selections before changing a development database.
    /// </summary>
    internal void RequireExpected(IEnumerable<string> names)
    {
        foreach (string name in names)
        {
            if (!HasExpected(name))
            {
                throw new ArgumentException($"Regression test '{name}' has no expected output. Run 'ankus regress --add {name}' first.");
            }
        }
    }

    /// <summary>
    /// Creates expected output only after a fresh native run and successful client completion.
    /// </summary>
    internal async Task<int> BootstrapAsync(string name, PostgresInstallation installation, string driver, string connection,
        string verbosity, CancellationToken token)
    {
        Directory.CreateDirectory(Path.Join(DirectoryPath, "expected"));
        string expected = Expected(name);
        bool created = false;
        bool promoted = false;
        try
        {
            await using (var placeholder = new FileStream(expected, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await placeholder.FlushAsync(token);
            }

            int code = await RegressionDriver.RunAsync(installation, driver, connection, DirectoryPath, [name], verbosity, token);
            if (code is not (0 or 1))
            {
                return code;
            }

            await PromoteAsync(name, token);
            promoted = true;
            Console.WriteLine($"Created expected output for {name}.");
            return 0;
        }
        finally
        {
            if (created && !promoted)
            {
                File.Delete(expected);
            }
        }
    }

    /// <summary>
    /// Retains native diffs and optionally promotes only selected tests that the native comparator reported different.
    /// </summary>
    internal async Task RecordFailureAsync(IReadOnlyList<string> names, int run, int repeat, bool verbose, bool promote, CancellationToken token)
    {
        string path = Path.Join(DirectoryPath, "regression.diffs");
        if (!File.Exists(path))
        {
            return;
        }

        string[] lines = await File.ReadAllLinesAsync(path, token);
        if (repeat > 1)
        {
            string retained = Path.Join(DirectoryPath, $"regression.{run}.diffs");
            File.Move(path, retained, overwrite: true);
            path = retained;
        }

        Console.Error.WriteLine($"Regression differences: {path}");
        if (verbose)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, lines));
        }

        if (promote)
        {
            foreach (string name in names.Where(name => lines.Any(line => line.StartsWith("diff ", StringComparison.Ordinal) &&
                    line.EndsWith("/results/" + name + ".out", StringComparison.Ordinal))))
            {
                await PromoteAsync(name, token);
                Console.WriteLine($"Updated expected output for {name}.");
            }
        }
    }

    private string Expected(string name) => Path.Join(DirectoryPath, "expected", name + ".out");

    private static void RequireRepresentable(string value)
    {
        // pg_regress interpolates absolute input/output paths into double-quoted shell arguments.
        if (value.Length == 0 || value.IndexOfAny(['"', '$', '`', '\r', '\n']) >= 0 || value.Any(char.IsControl) ||
            (OperatingSystem.IsWindows() ? value.IndexOfAny(['%', '!']) >= 0 : value.Contains('\\', StringComparison.Ordinal)))
        {
            throw new ArgumentException($"pg_regress cannot safely represent path or test name '{value}'.");
        }
    }

    private async Task PromoteAsync(string name, CancellationToken token)
    {
        string target = Expected(name);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(Path.Join(DirectoryPath, "results", name + ".out"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await input.CopyToAsync(output, token);
            }

            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
