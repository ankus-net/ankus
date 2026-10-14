using System.CommandLine;
using System.Globalization;
using System.Runtime.InteropServices;
using Ankus.PgConfig;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    private static Command CreateTest(Option<string?> home)
    {
        var command = new Command("test", "Run dotnet test using a selected PostgreSQL installation or all registered versions.");
        AddSelectionOptions(command);
        AddConfigurationOption(command, "Debug");
        var all = new Option<bool>("--all") { Description = "Run sequentially against every registered PostgreSQL version." };
        var noSchema = new Option<bool>("--no-schema") { Description = "Reuse the last successful test schema while rebuilding compatible native code." };
        var reports = new Option<string?>("--results-directory") { Description = "Test report root; each PostgreSQL major gets its own subdirectory." };
        var dataDirectory = new Option<string?>("--pgdata") { Description = "Base directory for isolated, per-invocation PostgreSQL cluster data." };
        var runAs = new Option<string?>("--runas")
        {
            Description = "Initialize and run the test servers as this Unix account through sudo -u; --pgdata must be writable by it.",
        };
        var valgrind = new Option<bool>("--valgrind") { Description = "Run the test servers under Valgrind's Memcheck tool (Unix only)." };
        var forwarded = new Argument<string[]>("test-arguments")
        {
            Description = "Ordinary dotnet test arguments after --, including project selection, filters and report options.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        command.Options.Add(all);
        command.Options.Add(noSchema);
        command.Options.Add(reports);
        command.Options.Add(dataDirectory);
        command.Options.Add(runAs);
        command.Options.Add(valgrind);
        command.Arguments.Add(forwarded);
        command.SetAction(async (result, token) =>
        {
            if (result.GetValue(valgrind) && OperatingSystem.IsWindows())
            {
                throw new ArgumentException("--valgrind is not supported on Windows.");
            }

            string? account = result.GetValue(runAs);
            if (account is not null)
            {
                if (OperatingSystem.IsWindows())
                {
                    throw new ArgumentException("--runas is not supported on Windows.");
                }

                if (account.Length == 0 || account.StartsWith('-') || account.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
                {
                    throw new ArgumentException("--runas requires a Unix account name.");
                }
            }

            Dictionary<string, string> properties = TestProperties(result.GetValue(forwarded) ?? []);
            (string configuration, string[] arguments) = TestConfiguration(GetConfiguration(result),
                result.GetResult("--configuration") is System.CommandLine.Parsing.OptionResult { Implicit: false },
                result.GetValue(forwarded) ?? [], properties.GetValueOrDefault("Configuration"));
            var installations = new List<PostgresInstallation>();
            if (result.GetValue(all))
            {
                if (result.GetResult("--pg") is System.CommandLine.Parsing.OptionResult { Implicit: false } ||
                    result.GetValue<string?>("--pg-config") is not null || properties.ContainsKey("AnkusPostgresMajor") ||
                    properties.ContainsKey("AnkusPgConfigPath"))
                {
                    throw new ArgumentException("Use --all without --pg, --pg-config or forwarded PostgreSQL selection properties.");
                }

                var registry = new PostgresRegistry(result.GetValue(home));
                for (int major = 13; major <= 19; major++)
                {
                    if (registry.GetPath(major) is not null)
                    {
                        installations.Add(await registry.GetAsync(major, token));
                    }
                }

                if (installations.Count == 0)
                {
                    throw new InvalidOperationException("No PostgreSQL versions are registered. Run 'ankus init' first.");
                }
            }
            else
            {
                installations.Add(await SelectAsync(result, home, token, TestProject(arguments), configuration, properties));
            }

            string root = Path.GetFullPath(result.GetValue(reports) ?? Path.Join("TestResults", "ankus", Guid.NewGuid().ToString("N")));
            int separator = Array.IndexOf(arguments, "--");
            if (separator < 0)
            {
                separator = arguments.Length;
            }

            int firstFailure = 0;
            foreach (PostgresInstallation installation in installations)
            {
                token.ThrowIfCancellationRequested();
                string major = installation.Version.Major.ToString(CultureInfo.InvariantCulture);
                await using var session = new ExtensionTestCommandSession(installation, result.GetValue(dataDirectory), account);
                var fixtureProperties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase)
                {
                    ["Configuration"] = configuration,
                    ["AnkusPostgresMajor"] = major,
                    ["AnkusPgConfigPath"] = installation.PgConfigPath,
                };
                string propertyFile = await WriteTestPropertiesAsync(session.DirectoryPath, fixtureProperties, token);
                string resultsDirectory = Path.Join(root, installation.Label);
                Console.WriteLine($"Testing PostgreSQL {installation.Version} ({configuration}). Results: {resultsDirectory}");
                int code = await ToolProcess.RunAsync("dotnet",
                [
                    "test", .. arguments[..separator],
                    "-p:Configuration=" + ExtensionBuilder.EscapeProperty(configuration),
                    "-p:AnkusPostgresMajor=" + major,
                    "-p:AnkusPgConfigPath=" + ExtensionBuilder.EscapeProperty(installation.PgConfigPath),
                    "--results-directory", resultsDirectory,
                    .. arguments[separator..],
                ], token, environment: TestEnvironment(new Dictionary<string, string?>
                {
                    ["ANKUS_TEST_PG_CONFIG"] = installation.PgConfigPath,
                    ["ANKUS_TEST_POSTGRES_MAJOR"] = major,
                    ["ANKUS_TEST_CONFIGURATION"] = configuration,
                    ["ANKUS_TEST_REUSE_SCHEMA"] = result.GetValue(noSchema) ? "true" : "false",
                    ["ANKUS_TEST_SESSION_DIRECTORY"] = session.DirectoryPath,
                    ["ANKUS_TEST_DATA_DIRECTORY"] = session.DataDirectoryPath,
                    ["ANKUS_TEST_RUNAS"] = account,
                    ["ANKUS_TEST_MSBUILD_PROPERTIES_FILE"] = propertyFile,
                }, result.GetValue(valgrind)));
                Console.WriteLine($"{installation.Label}: dotnet test exited {code}.");
                if (firstFailure == 0)
                {
                    firstFailure = code;
                }
            }

            return firstFailure;
        });
        return command;
    }

    /// <summary>
    /// The dotnet test options that name a project or solution.
    /// </summary>
    private static readonly string[] s_projectOptions = ["--project", "--solution"];

    /// <summary>
    /// Uses a forwarded project or solution as the source of the default PostgreSQL selection.
    /// </summary>
    /// <param name="arguments">The forwarded dotnet test arguments.</param>
    /// <returns>The selected project or solution file, or null for the current directory.</returns>
    private static string? TestProject(string[] arguments)
    {
        for (int index = 0; index < arguments.Length && arguments[index] != "--"; index++)
        {
            foreach (string option in s_projectOptions)
            {
                if (OptionValue(arguments, index, option) is string value)
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads an option's value written as <c>--option value</c>, <c>--option=value</c> or <c>--option:value</c>.
    /// </summary>
    /// <param name="arguments">The forwarded dotnet test arguments.</param>
    /// <param name="index">The position of the argument to read.</param>
    /// <param name="option">The option's name.</param>
    /// <returns>The value, or null when the argument is not this option.</returns>
    private static string? OptionValue(string[] arguments, int index, string option)
    {
        string argument = arguments[index];
        if (argument == option)
        {
            return index + 1 < arguments.Length ? arguments[index + 1] : throw new ArgumentException($"{option} requires a path.");
        }

        return argument.StartsWith(option + "=", StringComparison.Ordinal) || argument.StartsWith(option + ":", StringComparison.Ordinal)
            ? argument[(option.Length + 1)..] : null;
    }

    private static (string Configuration, string[] Arguments) TestConfiguration(string configuration, bool explicitlySelected, string[] arguments,
        string? propertyConfiguration)
    {
        var forwarded = new List<string>();
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (argument == "--")
            {
                forwarded.AddRange(arguments[index..]);
                break;
            }

            string? value = null;
            if (argument is "--configuration" or "-c")
            {
                if (++index == arguments.Length || arguments[index] == "--")
                {
                    throw new ArgumentException("The forwarded configuration option requires a value.");
                }

                value = arguments[index];
            }
            else if (argument.StartsWith("--configuration=", StringComparison.Ordinal) || argument.StartsWith("--configuration:", StringComparison.Ordinal))
            {
                value = argument[16..];
            }
            else if (argument.StartsWith("-c=", StringComparison.Ordinal) || argument.StartsWith("-c:", StringComparison.Ordinal))
            {
                value = argument[3..];
            }

            if (value is null)
            {
                forwarded.Add(argument);
                continue;
            }

            if (ConfigurationError(value) is string error)
            {
                throw new ArgumentException(error);
            }

            if (explicitlySelected && !string.Equals(configuration, value, StringComparison.Ordinal))
            {
                throw new ArgumentException("Select the same configuration for ankus test and forwarded dotnet test arguments.");
            }

            configuration = value;
            explicitlySelected = true;
        }

        if (propertyConfiguration is not null)
        {
            if (ConfigurationError(propertyConfiguration) is string error)
            {
                throw new ArgumentException(error);
            }

            if (explicitlySelected && !string.Equals(configuration, propertyConfiguration, StringComparison.Ordinal))
            {
                throw new ArgumentException("Select the same configuration for configuration options and forwarded Configuration properties.");
            }

            configuration = propertyConfiguration;
        }

        return (configuration, [.. forwarded]);
    }

    /// <summary>
    /// Reads explicit MSBuild globals before selecting the server, retaining last-value precedence.
    /// </summary>
    /// <param name="arguments">The forwarded dotnet test arguments.</param>
    /// <returns>The case-insensitive global properties preceding the test-runner separator.</returns>
    private static Dictionary<string, string> TestProperties(string[] arguments)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < arguments.Length && arguments[index] != "--"; index++)
        {
            string argument = arguments[index];
            string? list = null;
            foreach (string prefix in new[] { "-p", "/p", "-property", "/property", "--property" })
            {
                if (string.Equals(argument, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (++index == arguments.Length || arguments[index] == "--")
                    {
                        throw new ArgumentException("A forwarded MSBuild property option requires name=value.");
                    }

                    list = arguments[index];
                    break;
                }

                if (argument.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase))
                {
                    list = argument[(prefix.Length + 1)..];
                    break;
                }
            }

            if (list is null)
            {
                continue;
            }

            int start = 0;
            bool quoted = false;
            for (int offset = 0; offset <= list.Length; offset++)
            {
                if (offset < list.Length && list[offset] == '"')
                {
                    quoted = !quoted;
                }

                if (offset < list.Length && (quoted || list[offset] is not (';' or ',')))
                {
                    continue;
                }

                string property = list[start..offset].Trim('"');
                int separator = property.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    throw new ArgumentException("Forwarded MSBuild properties must contain name=value.");
                }

                string name = property[..separator].Trim();
                try
                {
                    System.Xml.XmlConvert.VerifyNCName(name);
                }
                catch (System.Xml.XmlException error)
                {
                    throw new ArgumentException("Forwarded MSBuild properties must contain name=value with a valid property name.", error);
                }

                properties[name] = Uri.UnescapeDataString(property[(separator + 1)..].Trim('"'));
                start = offset + 1;
            }

            if (quoted)
            {
                throw new ArgumentException("A forwarded MSBuild property contains an unterminated quoted value.");
            }
        }

        if (properties.TryGetValue("RuntimeIdentifier", out string? runtime) && runtime != RuntimeInformation.RuntimeIdentifier)
        {
            throw new ArgumentException("ankus test publishes fixtures for the host RuntimeIdentifier.");
        }

        if (properties.TryGetValue("SelfContained", out string? selfContained) &&
            !string.Equals(selfContained, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("ankus test requires SelfContained=true for fixture publication.");
        }

        return properties;
    }

    /// <summary>
    /// Writes effective MSBuild properties into command-owned storage for nested fixture publications.
    /// </summary>
    /// <param name="sessionDirectory">The command session directory.</param>
    /// <param name="properties">The literal effective properties.</param>
    /// <param name="token">Cancels the property-file write.</param>
    /// <returns>The absolute property-file path.</returns>
    private static async Task<string> WriteTestPropertiesAsync(string sessionDirectory,
        Dictionary<string, string> properties, CancellationToken token)
    {
        string path = Path.Join(sessionDirectory, "msbuild-properties");
        string[] lines =
        [
            .. properties.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)),
        ];
        await File.WriteAllLinesAsync(path, lines, token);
        return path;
    }

    /// <summary>
    /// Adds the Valgrind request without clearing an ANKUS_TEST_VALGRIND value the caller set.
    /// </summary>
    private static Dictionary<string, string?> TestEnvironment(Dictionary<string, string?> environment, bool valgrind)
    {
        if (valgrind)
        {
            environment["ANKUS_TEST_VALGRIND"] = "true";
        }

        return environment;
    }
}
