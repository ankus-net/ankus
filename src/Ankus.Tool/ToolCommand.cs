using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Runtime.InteropServices;
using Ankus.PgConfig;

namespace Ankus.Tool;

/// <summary>
/// Defines the command-line interface for PostgreSQL registration and native extension builds.
/// </summary>
internal static partial class ToolCommand
{
    /// <summary>
    /// Parses and executes one command, reporting failures with a nonzero exit code.
    /// </summary>
    /// <param name="arguments">The command-line tokens.</param>
    /// <returns>The command exit code.</returns>
    internal static async Task<int> RunAsync(string[] arguments)
    {
        using var interactiveCancellation = new InteractiveCommandCancellation();
        RootCommand root = CreateRoot(interactiveCancellation);
        try
        {
            if (arguments.Length != 0 && arguments[0] == RegressionDriver.ClientSwitch)
            {
                return await RegressionDriver.RunClientAsync(arguments[1..], interactiveCancellation.Token);
            }

            ParseResult result = root.Parse(arguments);
            bool interactive = result.CommandResult.Command.Name is "run" or "connect" or "test" or "bench";
            if (interactive)
            {
                interactiveCancellation.Enable();
            }

            return await result.InvokeAsync(new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
                ProcessTerminationTimeout = interactive ? null : TimeSpan.FromSeconds(30),
            }, interactiveCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Ankus: command canceled.");
            return 130;
        }
        // codeql[cs/catch-of-all-exceptions] The command reports any failure as a message and a nonzero exit code.
        catch (Exception error)
        {
            Console.Error.WriteLine($"Ankus: {error.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Creates the root command with every subcommand.
    /// </summary>
    /// <param name="interactiveCancellation">Cancels interactive commands on the console's interrupt key.</param>
    /// <returns>The root command.</returns>
    internal static RootCommand CreateRoot(InteractiveCommandCancellation interactiveCancellation)
    {
        var home = new Option<string?>("--home")
        {
            Description = "Ankus home directory (default: ANKUS_HOME, otherwise ~/.ankus).",
            Recursive = true,
        };
        var root = new RootCommand("Build and manage .NET PostgreSQL extensions.");

        // The root command's help option comes first, and every command lists inherited options in that order.
        root.Options.Insert(0, home);
        HelpOption help = root.Options.OfType<HelpOption>().Single();
        help.Action = new RootHelpAction((SynchronousCommandLineAction)help.Action!);
        root.Subcommands.Add(CreateInit(home));
        root.Subcommands.Add(CreateInfo(home));
        root.Subcommands.Add(CreateCluster("start", home));
        root.Subcommands.Add(CreateCluster("stop", home));
        root.Subcommands.Add(CreateCluster("status", home));
        root.Subcommands.Add(CreateConnect(home, interactiveCancellation));
        root.Subcommands.Add(CreateRun(home, interactiveCancellation));
        root.Subcommands.Add(CreateNew());
        root.Subcommands.Add(CreateBuild("build", "Build the native extension and SQL files.", home));
        root.Subcommands.Add(CreateBuild("publish", "Publish the native extension and SQL files to a directory.", home));
        root.Subcommands.Add(CreateInstall(home));
        root.Subcommands.Add(CreateInstall(home, package: true));
        root.Subcommands.Add(CreateSchema(home));
        root.Subcommands.Add(CreateGet(home));
        root.Subcommands.Add(CreateRegress(home));
        root.Subcommands.Add(CreateTest(home));
        root.Subcommands.Add(CreateBench(home));
        root.Subcommands.Add(CreateUpgrade());
        root.Subcommands.Add(CreateTarget(home));
        return root;
    }

    private static Command CreateNew()
    {
        var command = new Command("new", "Create an extension solution with managed and PostgreSQL tests.");
        var name = new Argument<string>("name") { Description = "C# project name, such as Acme.Search." };
        var output = new Option<string?>("--output", "-o") { Description = "New directory (default: the project name)." };
        var extension = new Option<string?>("--extension-name") { Description = "SQL extension name (default: snake_case project name)." };
        var worker = new Option<bool>("--background-worker") { Description = "Include a preloaded PostgreSQL worker and its backend test." };
        var framework = new Option<string>("--test-framework")
        {
            Description = "Consumer test framework: mstest, xunit or nunit.",
            DefaultValueFactory = static _ => "mstest",
        };
        framework.AcceptOnlyFromAmong("mstest", "xunit", "nunit");
        var targetFramework = new Option<string>("--framework", "-f")
        {
            Description = "Target framework: net10.0 or net11.0.",
            DefaultValueFactory = static _ => "net10.0",
        };
        targetFramework.AcceptOnlyFromAmong(ProjectScaffolder.TargetFrameworks);
        command.Arguments.Add(name);
        command.Options.Add(output);
        command.Options.Add(extension);
        command.Options.Add(worker);
        command.Options.Add(framework);
        command.Options.Add(targetFramework);
        command.SetAction(async (result, token) =>
        {
            string path = await ProjectScaffolder.CreateAsync(result.GetValue(name)!, result.GetValue(output),
                result.GetValue(extension), result.GetValue(worker), result.GetValue(framework)!, result.GetValue(targetFramework)!, token);
            Console.WriteLine($"Created extension solution at {path}");
            Console.WriteLine("Run dotnet test from that directory to build and test the extension in PostgreSQL 18.");
            Console.WriteLine("Run ankus publish to publish using your registered PostgreSQL installation.");
        });
        return command;
    }

    private static Command CreateInit(Option<string?> home)
    {
        var command = new Command("init", "Download or register PostgreSQL versions for extension development.");
        var versions = new Dictionary<int, Option<string?>>();
        for (int major = 13; major <= 19; major++)
        {
            string environmentName = $"PG{major}_PG_CONFIG";
            var option = new Option<string?>($"--pg{major}")
            {
                Description = $"Path to PostgreSQL {major}'s pg_config executable, or 'download' to install it locally (default: {environmentName}).",
                DefaultValueFactory = _ => Environment.GetEnvironmentVariable(environmentName),
            };
            versions.Add(major, option);
            command.Options.Add(option);
        }

        var jobs = new Option<int>("--jobs", "-j")
        {
            Description = "Maximum parallel PostgreSQL source-build jobs.",
            DefaultValueFactory = _ => Environment.ProcessorCount,
        };
        var configure = new Option<string[]>("--configure-flag")
        {
            Description = "Additional Unix configure argument; repeat for multiple arguments.",
        };
        var valgrind = new Option<bool>("--valgrind") { Description = "Enable PostgreSQL's Valgrind instrumentation on Unix." };
        var release = new Option<bool>("--release")
        {
            Description = "Build without assertions or memory randomization on Unix, for benchmarking.",
        };
        var basePort = new Option<int?>("--base-port") { Description = "Development port base, added to each major (0–65516; default 28800)." };
        var baseTestingPort = new Option<int?>("--base-testing-port") { Description = "Port base for explicitly configured test fixtures (0–65516; default 32200)." };
        command.Options.Add(jobs);
        command.Options.Add(configure);
        command.Options.Add(valgrind);
        command.Options.Add(release);
        command.Options.Add(basePort);
        command.Options.Add(baseTestingPort);
        command.SetAction(async (result, token) =>
        {
            var ports = new PostgresPortOptions(result.GetValue(basePort), result.GetValue(baseTestingPort));
            var paths = new Dictionary<int, string>();
            foreach ((int major, Option<string?> option) in versions)
            {
                if (result.GetValue(option) is string path)
                {
                    paths.Add(major, path);
                }
            }

            var registry = new PostgresRegistry(result.GetValue(home));
            if (paths.Count == 0)
            {
                foreach (int major in versions.Keys)
                {
                    paths.Add(major, "download");
                }
            }

            int[] downloads = [.. paths.Where(static pair => pair.Value == "download").Select(static pair => pair.Key)];
            if (downloads.Length != 0)
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
                var provisioner = new PostgresProvisioner(client, registry.HomeDirectory);
                var options = new PostgresProvisionOptions
                {
                    Jobs = result.GetValue(jobs),
                    ConfigureFlags = result.GetValue(configure) ?? [],
                    EnableValgrind = result.GetValue(valgrind),
                    Release = result.GetValue(release),
                };
                IReadOnlyList<PostgresInstallation> downloaded = await provisioner.InstallAsync(downloads, options,
                    new ConsoleProgress(), token);
                foreach (PostgresInstallation installation in downloaded)
                {
                    paths[installation.Version.Major] = installation.PgConfigPath;
                }
            }

            IReadOnlyList<PostgresInstallation> installations = await registry.RegisterAsync(paths, ports, token);
            foreach (PostgresInstallation installation in installations)
            {
                Console.WriteLine($"Registered {installation.Label}: {installation.PgConfigPath}");
            }

            return 0;
        });
        return command;
    }

    private static Command CreateBuild(string name, string description, Option<string?> home)
    {
        var command = new Command(name, description);
        AddSelectionOptions(command);
        AddTargetInfoOption(command);
        AddBuildOptions(command);
        command.Options.Add(new Option<string?>("--output", "-o") { Description = "Publish directory." });
        command.SetAction(async (result, token) =>
        {
            ExtensionSelection selection = await SelectExtensionAsync(result, home, token);
            string output = GetOutputDirectory(result, selection);
            return await PublishAsync(selection, output, token);
        });
        return command;
    }

    private static Command CreateInstall(Option<string?> home, bool package = false)
    {
        Command command = package
            ? new Command("package", "Build an extension installation tree under a separate output directory.")
            : new Command("install", "Build and copy an extension into a PostgreSQL installation.");
        AddSelectionOptions(command);
        if (package)
        {
            AddTargetInfoOption(command);
        }

        AddBuildOptions(command);
        var from = new Option<string?>("--from") { Description = "Use an existing publish directory without rebuilding." };
        Option<string?> destination = package
            ? new Option<string?>("--output", "-o") { Description = "Package root (default: extension-pgMAJOR beneath the publish directory)." }
            : new Option<string?>("--destdir") { Description = "Stage files under this root, preserving PostgreSQL's installation paths." };
        command.Options.Add(from);
        command.Options.Add(destination);
        var sudo = new Option<bool>("--sudo", "-s")
        {
            Description = "Copy the files into place with sudo, for installations the current account cannot write (Unix).",
        };
        if (!package)
        {
            command.Options.Add(sudo);
        }

        var prefix = new Option<string?>("--prefix-dir")
        {
            Description = "Unix package asset directory within the output root; Windows retains its portable lib/share layout.",
        };
        if (package)
        {
            command.Options.Add(prefix);
        }

        command.SetAction(async (result, token) =>
        {
            if (result.GetValue(from) is not null && result.GetValue<string?>("--project") is not null)
            {
                throw new ArgumentException("Use either --from or --project.");
            }

            if (result.GetValue(from) is not null && BuildProperties(result).Count != 0)
            {
                throw new ArgumentException("Use either --from or --property.");
            }

            bool elevated = !package && result.GetValue(sudo);
            if (elevated && OperatingSystem.IsWindows())
            {
                throw new ArgumentException("--sudo is not supported on Windows; install from an elevated terminal instead.");
            }

            string? prefixDirectory = package ? result.GetValue(prefix) : null;
            if (prefixDirectory is not null)
            {
                ExtensionInstaller.GetPackagePrefix(result.GetValue(destination) ?? Environment.CurrentDirectory, prefixDirectory);
            }

            PostgresInstallation installation;
            string? source = result.GetValue(from);
            if (source is null)
            {
                ExtensionSelection selection = await SelectExtensionAsync(result, home, token);
                installation = selection.Installation;
                source = GetOutputDirectory(result, selection);
                int exitCode = await PublishAsync(selection, source, token);
                if (exitCode != 0)
                {
                    return exitCode;
                }
            }
            else
            {
                installation = await SelectAsync(result, home, token);
            }

            string? root = result.GetValue(destination);
            if (package && root is null)
            {
                string name = Path.GetFileNameWithoutExtension(PublishedExtension.Read(source).Control);
                root = Path.Join(Path.GetFullPath(source), name + "-" + installation.Label);
            }

            if (elevated)
            {
                return await ExtensionSudoInstaller.InstallAsync(source, installation, root, token);
            }

            IReadOnlyList<string> installed;
            try
            {
                installed = ExtensionInstaller.Install(source, installation, root, token, packageLayout: package,
                    prefixDirectory: prefixDirectory);
            }
            catch (UnauthorizedAccessException error) when (!package && !OperatingSystem.IsWindows())
            {
                throw new UnauthorizedAccessException(error.Message + " Add --sudo to copy the files with sudo.", error);
            }

            foreach (string path in installed)
            {
                Console.WriteLine($"{(package ? "Packaged" : "Installed")} {path}");
            }

            return 0;
        });
        return command;
    }

    private static void AddSelectionOptions(Command command)
    {
        command.Options.Add(new Option<int?>("--pg")
        {
            Description = "PostgreSQL major version (13–19; default: project selection, otherwise 18).",
        });
        command.Options.Add(new Option<string?>("--pg-config")
        {
            Description = "Use this pg_config instead of a registered installation.",
        });
    }

    private static void AddTargetInfoOption(Command command)
    {
        command.Options.Add(new Option<string?>("--target-info")
        {
            Description = "Build for another machine from information that 'ankus target export' captured there.",
        });
    }

    private static Command CreateTarget(Option<string?> home)
    {
        var command = new Command("target", "Prepare builds for PostgreSQL installed on another machine.");
        var export = new Command("export",
            "Capture this machine's PostgreSQL build values, headers and native bindings for builds on other machines.");
        AddSelectionOptions(export);
        AddBuildOptions(export);
        var output = new Option<string?>("--output", "-o")
        {
            Description = "Archive path (default: ankus-target.pgMAJOR.RID.zip in the current directory).",
        };
        export.Options.Add(output);
        export.SetAction(async (result, token) =>
        {
            ExtensionSelection selection = await SelectExtensionAsync(result, home, token);
            string archive = Path.GetFullPath(result.GetValue(output) ??
                PostgresTargetInfo.GetArchiveFileName(selection.Installation.Version.Major, RuntimeInformation.RuntimeIdentifier));
            int code = await ExtensionBuilder.ExportTargetAsync(selection.Project, selection.Configuration, selection.Installation,
                archive, token, selection.Properties);
            if (code == 0)
            {
                Console.WriteLine($"Exported {selection.Installation.Label} target information for {RuntimeInformation.RuntimeIdentifier} to {archive}");
            }

            return code;
        });
        command.Subcommands.Add(export);
        return command;
    }

    private static void AddBuildOptions(Command command)
    {
        command.Options.Add(new Option<string?>("--project")
        {
            Description = "Extension project or directory (default: current directory).",
        });
        AddConfigurationOption(command, "Release");
        AddPropertyOption(command);
    }

    private static void AddConfigurationOption(Command command, string defaultConfiguration)
    {
        var configuration = new Option<string>("--configuration", "-c")
        {
            Description = "MSBuild configuration, including custom configurations.",
            DefaultValueFactory = _ => defaultConfiguration,
        };
        configuration.Validators.Add(result =>
        {
            string? value = result.GetValueOrDefault<string>();
            if (ConfigurationError(value) is string error)
            {
                result.AddError(error);
            }
        });
        command.Options.Add(configuration);
    }

    private static string? ConfigurationError(string? value)
        => string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0 ||
            value.Any(char.IsControl) || value.EndsWith(' ') || value.EndsWith('.')
            ? "Configuration must be a nonempty directory name without path separators, invalid filename characters, or a trailing dot or space."
            : null;

    /// <summary>
    /// Places publication output beside the evaluated extension project unless the command selects an explicit destination.
    /// </summary>
    /// <param name="result">The parsed build command.</param>
    /// <param name="selection">The invocation's resolved extension and build context.</param>
    /// <returns>The absolute publication directory.</returns>
    private static string GetOutputDirectory(ParseResult result, ExtensionSelection selection)
    {
        string? output = result.CommandResult.Command.Name is "install" or "package" or "run" or "regress" or "bench"
            ? null : result.GetValue<string?>("--output");
        return Path.GetFullPath(output ?? Path.Join(Path.GetDirectoryName(selection.Project)!, "bin", "ankus",
            selection.Installation.Label, selection.Installation.TargetInfo?.RuntimeIdentifier ?? RuntimeInformation.RuntimeIdentifier,
            selection.Configuration));
    }

    private static async Task<int> PublishAsync(
        ExtensionSelection selection,
        string output,
        CancellationToken token,
        bool diagnosticsToStandardError = false,
        bool announce = true)
    {
        int code = await ExtensionBuilder.PublishAsync(selection.Project,
            selection.Configuration, selection.Installation, output, token, diagnosticsToStandardError, selection.Properties);
        if (code == 0 && announce)
        {
            Console.WriteLine($"Published {selection.Installation.Label} extension to {output}");
        }

        return code;
    }

    private static string GetConfiguration(ParseResult result)
    {
        string selected = result.GetValue<string>("--configuration")!;
        if (BuildProperties(result).GetValueOrDefault("Configuration") is not string forwarded)
        {
            return selected;
        }

        if (ConfigurationError(forwarded) is string error)
        {
            throw new ArgumentException(error);
        }

        if (result.GetResult("--configuration") is System.CommandLine.Parsing.OptionResult { Implicit: false } && selected != forwarded)
        {
            throw new ArgumentException("Select the same configuration for --configuration and Configuration.");
        }

        return forwarded;
    }
}
