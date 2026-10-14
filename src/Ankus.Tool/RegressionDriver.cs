using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Runs PostgreSQL's regression comparator with a managed psql launcher and observable child completion.
/// </summary>
internal static class RegressionDriver
{
    /// <summary>
    /// Selects the private process boundary used by pg_regress to launch psql.
    /// </summary>
    internal const string ClientSwitch = "--ankus-regression-client";

    private const string ExecutableVariable = "ANKUS_REGRESS_EXECUTABLE";
    private const string AssemblyVariable = "ANKUS_REGRESS_ASSEMBLY";
    private const string PsqlVariable = "ANKUS_REGRESS_PSQL";
    private const string ConnectionVariable = "ANKUS_REGRESS_CONNECTION";
    private const string VerbosityVariable = "ANKUS_REGRESS_VERBOSITY";
    private const string StatusVariable = "ANKUS_REGRESS_STATUS";

    /// <summary>
    /// Runs native comparison without substituting managed text comparison for PostgreSQL's result rules.
    /// </summary>
    internal static async Task<int> RunAsync(PostgresInstallation installation, string driver, string connection,
        string directory, IReadOnlyList<string> names, string verbosity, CancellationToken token)
    {
        if (names.Count == 0)
        {
            return 0;
        }

        string status = Directory.CreateTempSubdirectory("ankus-regression-status-").FullName;
        try
        {
            using WindowsRegressionWorkspace? workspace = WindowsRegressionWorkspace.Create(directory);
            string nativeDirectory = workspace?.DirectoryPath ?? directory;
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the Ankus executable.");
            bool hosted = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
            var environment = new Dictionary<string, string?>
            {
                [ExecutableVariable] = executable,
                [AssemblyVariable] = hosted ? Path.GetFullPath(Environment.GetCommandLineArgs()[0]) : null,
                [PsqlVariable] = installation.PsqlPath,
                [ConnectionVariable] = connection,
                [VerbosityVariable] = verbosity,
                [StatusVariable] = status,
                ["PATH"] = installation.BinDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            };
            string launcher = ReferenceVariable(ExecutableVariable) +
                (hosted ? " " + ReferenceVariable(AssemblyVariable) : "") + " " + ClientSwitch;
            string results = Path.Join(directory, "results");
            if (Directory.Exists(results))
            {
                foreach (string name in names)
                {
                    File.Delete(Path.Join(results, name + ".out"));
                }
            }

            // PostgreSQL normalizes these suite paths before invoking its comparator.
            // The managed launcher supplies the actual psql and connection as individual native arguments.
            int code;
            try
            {
                code = await ToolProcess.RunAsync(driver,
                    ["--use-existing", "--host=127.0.0.1", "--port=" + new Uri(connection).Port.ToString(CultureInfo.InvariantCulture),
                        "--user=postgres", "--dbname=regression", "--bindir=.", "--inputdir=.", "--outputdir=.",
                        "--launcher=" + launcher, "--", .. names], token, postgresClient: true,
                    workingDirectory: nativeDirectory, environment: environment);
            }
            finally
            {
                workspace?.CopyOutputsTo(directory);
            }

            foreach (string name in names)
            {
                string completion = StatusFile(status, "pg_regress/" + name);
                if (!File.Exists(completion) || await File.ReadAllTextAsync(completion, token) != "0")
                {
                    Console.Error.WriteLine($"Regression client '{name}' did not complete successfully; expected output was not updated.");
                    return 2;
                }
            }

            return code;
        }
        finally
        {
            Directory.Delete(status, recursive: true);
        }
    }

    /// <summary>
    /// Forwards pg_regress's input and output while supplying exact connection values and psql verbosity.
    /// </summary>
    internal static async Task<int> RunClientAsync(string[] arguments, CancellationToken token)
    {
        if (arguments.Length == 0)
        {
            throw new ArgumentException("The regression launcher requires a psql command.");
        }

        string executable = RequireEnvironment(PsqlVariable);
        string connection = RequireEnvironment(ConnectionVariable);
        string verbosity = RequireEnvironment(VerbosityVariable);
        string status = RequireEnvironment(StatusVariable);
        string application = RequireEnvironment("PGAPPNAME");
        int code = await ToolProcess.RunAsync(executable,
            [.. arguments[1..], "--dbname=" + connection, "--set=VERBOSITY=" + verbosity], token);
        await File.WriteAllTextAsync(StatusFile(status, application), code.ToString(CultureInfo.InvariantCulture), token);
        return code;
    }

    private static string ReferenceVariable(string name)
        => OperatingSystem.IsWindows() ? "\"%" + name + "%\"" : "\"$" + name + "\"";

    private static string RequireEnvironment(string name)
        => Environment.GetEnvironmentVariable(name) is string value && value.Length != 0
            ? value : throw new InvalidOperationException($"Missing regression launcher setting {name}.");

    private static string StatusFile(string directory, string application)
        => Path.Join(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(application))) + ".status");
}
