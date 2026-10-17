using System.CommandLine;
using System.Globalization;
using Ankus.PgConfig;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    private static Command CreateRegress(Option<string?> home)
    {
        var command = new Command("regress", "Run PostgreSQL SQL and expected-output regression tests for an extension.");
        AddSelectionOptions(command);
        AddBuildOptions(command);
        AddValgrindOption(command);
        var filter = new Argument<string?>("filter") { Description = "Case-sensitive substring of SQL test names.", Arity = ArgumentArity.ZeroOrOne };
        var database = new Option<string?>("--database", "-d") { Description = "Literal test database name (default: EXTENSION_regress)." };
        var reset = new Option<bool>("--resetdb") { Description = "Recreate the selected database before each run." };
        var add = new Option<string?>("--add") { Description = "Bootstrap one new SQL test and its expected output, recreating the database." };
        var auto = new Option<bool>("--auto") { Description = "Update expected outputs for differences; the current run still fails." };
        var dry = new Option<bool>("--dry-run") { Description = "Describe selection and database actions without building, starting PostgreSQL or writing files." };
        var repeat = new Option<int>("--repeat") { Description = "Run the selection this many times.", DefaultValueFactory = _ => 1 };
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Print native regression differences as well as their paths." };
        var verbosity = new Option<string>("--psql-verbosity") { Description = "SQL error detail: terse, default, verbose, or sqlstate.", DefaultValueFactory = _ => "terse" };
        var noBuild = new Option<bool>("--no-build") { Description = "Install the project's existing publication without rebuilding." };
        var runAs = new Option<string?>("--runas")
        {
            Description = "Create and drop the test database as this Unix account and its role through sudo -u.",
        };
        command.Arguments.Add(filter);
        command.Options.Add(database);
        command.Options.Add(reset);
        command.Options.Add(add);
        command.Options.Add(auto);
        command.Options.Add(dry);
        command.Options.Add(repeat);
        command.Options.Add(verbose);
        command.Options.Add(verbosity);
        command.Options.Add(noBuild);
        command.Options.Add(runAs);
        command.Options.Add(new Option<int?>("--port") { Description = "TCP port for the development server." });
        command.Options.Add(new Option<int>("--timeout") { Description = "Startup timeout in seconds (1–600).", DefaultValueFactory = _ => 60 });
        command.Options.Add(new Option<string[]>("--postgresql-conf") { Description = "Literal name=value setting; repeat for multiple settings." });
        command.SetAction(async (result, token) =>
        {
            int iterations = result.GetValue(repeat);
            ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
            string detail = result.GetValue(verbosity)!;
            if (detail is not ("terse" or "default" or "verbose" or "sqlstate"))
            {
                throw new ArgumentException("psql verbosity must be terse, default, verbose, or sqlstate.");
            }

            string? account = result.GetValue(runAs);
            if (account is not null && OperatingSystem.IsWindows())
            {
                throw new ArgumentException("--runas is not supported on Windows.");
            }

            string project = await ExtensionBuilder.ResolveProjectAsync(result.GetValue<string?>("--project"),
                GetConfiguration(result), token, BuildProperties(result));
            var suite = new RegressionSuite(project);
            string? pattern = result.GetValue(filter);
            string? newTest = result.GetValue(add);
            if (newTest is not null)
            {
                if (pattern is not null || result.GetValue(auto) || iterations != 1)
                {
                    throw new ArgumentException("Use --add without a filter, --auto, or --repeat greater than one.");
                }

                if (!suite.Names.Contains(newTest) || suite.HasExpected(newTest))
                {
                    throw new ArgumentException($"--add requires a SQL test without existing expected output: {newTest}");
                }
            }

            string[] selected = suite.Select(pattern);
            if (pattern is not null)
            {
                suite.RequireExpected(selected);
            }

            int major = await SelectMajorAsync(result, token, project);
            string name = await ExtensionBuilder.GetExtensionNameAsync(project, GetConfiguration(result), major,
                result.GetValue<string?>("--pg-config"), token, BuildProperties(result));
            string targetDatabase = result.GetValue(database) ?? name + "_regress";
            ArgumentException.ThrowIfNullOrEmpty(targetDatabase);
            if (targetDatabase.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("Database names cannot contain NUL.");
            }

            Dictionary<string, string> settings = ParseServerSettings(result.GetValue<string[]>("--postgresql-conf") ?? []);
            settings["client_min_messages"] = "warning";
            var options = new PostgresDevelopmentOptions
            {
                Port = result.GetValue<int?>("--port"),
                TimeoutSeconds = result.GetValue<int>("--timeout"),
                UseValgrind = result.GetValue<bool>("--valgrind"),
                Settings = settings,
            };
            if (options.Port is int port)
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(options.TimeoutSeconds, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(options.TimeoutSeconds, 600);
            if (result.GetValue(dry))
            {
                Console.WriteLine($"Would {(result.GetValue(noBuild) ? "install existing publication for" : "build and install")} {name} ({GetConfiguration(result)}, pg{major}).");
                if (options.UseValgrind)
                {
                    Console.WriteLine("Would start PostgreSQL under Valgrind Memcheck, with diagnostics in the server log.");
                }

                Console.WriteLine($"Would {(result.GetValue(reset) || newTest is not null || suite.SetupChanged ? "recreate" : "create or reuse")} database {targetDatabase}" +
                    (account is null ? "." : $" as {account} through sudo."));
                if (newTest is not null)
                {
                    Console.WriteLine($"Would bootstrap {newTest}, running setup.sql first when present.");
                }
                else
                {
                    foreach (string item in selected)
                    {
                        Console.WriteLine($"Would {(suite.HasExpected(item) ? "run" : "skip without expected output")} {item}.");
                    }

                    Console.WriteLine($"Would run the selection {iterations} time(s).");
                    if (result.GetValue(auto))
                    {
                        Console.WriteLine("Would update expected outputs for failed comparisons.");
                    }
                }

                return 0;
            }

            ExtensionSelection selection = await SelectExtensionAsync(result, home, token, project);
            PostgresInstallation installation = selection.Installation;
            string driver = await installation.GetRegressionDriverPathAsync(token);
            Directory.CreateDirectory(suite.DirectoryPath);
            using var suiteLock = new FileStream(Path.Join(suite.DirectoryPath, ".ankus-regress.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var cluster = new PostgresDevelopmentCluster(installation, result.GetValue(home));
            await cluster.StopAsync(token);
            string output = GetOutputDirectory(result, selection);
            if (!result.GetValue(noBuild))
            {
                int buildCode = await PublishAsync(selection, output, token);
                if (buildCode != 0)
                {
                    return buildCode;
                }
            }

            IReadOnlyList<string> installed;
            if (installation.Version.Major >= 18)
            {
                // PostgreSQL 18 finds extensions through extension_control_path, so the files stay beside the publication
                // and the installation, such as a distribution's, needs no write access.
                string controlBase = ExtensionStaging.Stage(output, ".ankus-regress", token, out installed);
                char separator = OperatingSystem.IsWindows() ? ';' : ':';
                PrependSearchPath(settings, "extension_control_path", controlBase, "$system", separator);
                PrependSearchPath(settings, "dynamic_library_path", output, "$libdir", separator);
            }
            else
            {
                try
                {
                    installed = ExtensionInstaller.Install(output, installation, null, token);
                }
                catch (UnauthorizedAccessException error)
                {
                    throw new UnauthorizedAccessException($"Before PostgreSQL 18, ankus regress installs the extension into the " +
                        $"installation, and this account can't write there: {error.Message} Use an installation you own, such as " +
                        $"one from 'ankus init --pg{installation.Version.Major} download'. PostgreSQL 18 and later need no write access.", error);
                }
            }

            foreach (string path in installed)
            {
                Console.WriteLine($"Installed {path}");
            }

            await cluster.StartAsync(options, token);
            bool failed = false;
            for (int index = 0; index < iterations; index++)
            {
                int iteration = index + 1;
                token.ThrowIfCancellationRequested();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Regression run {iteration} of {iterations}: {targetDatabase}"));
                if (result.GetValue(reset) || newTest is not null || suite.SetupChanged)
                {
                    await cluster.DropDatabaseAsync(targetDatabase, force: true, account, token);
                }

                bool created = await cluster.CreateDatabaseAsync(targetDatabase, account, token);
                Console.WriteLine($"{(created ? "Created" : "Reusing")} database {targetDatabase}");
                string connection = await cluster.GetConnectionStringAsync(targetDatabase, token);
                if (newTest is not null)
                {
                    if (newTest != "setup" && suite.Names.Contains("setup"))
                    {
                        int setupCode = suite.HasExpected("setup")
                            ? await RegressionDriver.RunAsync(installation, driver, connection, suite.DirectoryPath, ["setup"], detail, token)
                            : await suite.BootstrapAsync("setup", installation, driver, connection, detail, token);
                        if (setupCode != 0)
                        {
                            await suite.RecordFailureAsync(["setup"], 1, 1, result.GetValue(verbose), false, token);
                            return setupCode;
                        }
                    }

                    return await suite.BootstrapAsync(newTest, installation, driver, connection, detail, token);
                }

                string[] tests = created && suite.Names.Contains("setup") ? ["setup", .. selected] : selected;
                if (pattern is not null)
                {
                    suite.RequireExpected(tests);
                }

                foreach (string skipped in tests.Where(item => !suite.HasExpected(item)))
                {
                    Console.WriteLine($"SKIP {skipped}: no expected output; use --add {skipped}.");
                }

                string[] ready = [.. tests.Where(suite.HasExpected)];
                Console.WriteLine($"Selected {ready.Length} tests; skipped {tests.Length - ready.Length}.");
                if (iterations > 1)
                {
                    File.Delete(Path.Join(suite.DirectoryPath, "regression." + iteration.ToString(CultureInfo.InvariantCulture) + ".diffs"));
                }

                int code = await RegressionDriver.RunAsync(installation, driver, connection, suite.DirectoryPath, ready, detail, token);
                if (code != 0)
                {
                    failed = true;
                    await suite.RecordFailureAsync(ready, iteration, iterations, result.GetValue(verbose), result.GetValue(auto) && code == 1, token);
                    if (code != 1)
                    {
                        return code;
                    }
                }
            }

            return failed ? 1 : 0;
        });
        return command;
    }
}
