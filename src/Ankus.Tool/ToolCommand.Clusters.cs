using System.CommandLine;
using Ankus.PgConfig;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    private static Command CreateCluster(string name, Option<string?> home)
    {
        var command = new Command(name, name switch
        {
            "start" => "Start a persistent local PostgreSQL development server.",
            "stop" => "Stop a development server, preserving its databases.",
            _ => "Show whether a development server is running.",
        });
        AddSelectionOptions(command);
        var all = new Option<bool>("--all") { Description = "Use all registered PostgreSQL versions." };
        var port = new Option<int?>("--port") { Description = "TCP port (default: the saved development base plus the PostgreSQL major)." };
        var timeout = new Option<int>("--timeout") { Description = "Startup timeout in seconds (1–600).", DefaultValueFactory = _ => 60 };
        var settings = new Option<string[]>("--postgresql-conf") { Description = "Literal name=value setting; repeat for multiple settings." };
        command.Options.Add(all);
        if (name == "start")
        {
            command.Options.Add(port);
            command.Options.Add(timeout);
            command.Options.Add(settings);
            AddValgrindOption(command);
        }

        command.SetAction(async (result, token) =>
        {
            bool every = result.GetValue(all);
            var registry = new PostgresRegistry(result.GetValue(home));
            var installations = new List<PostgresInstallation>();
            if (every)
            {
                if (result.GetResult("--pg") is System.CommandLine.Parsing.OptionResult { Implicit: false } || result.GetValue<string?>("--pg-config") is not null ||
                    (name == "start" && result.GetValue(port) is not null))
                {
                    throw new ArgumentException("Use --all without --pg, --pg-config, or --port.");
                }

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
                installations.Add(await SelectAsync(result, home, token));
            }

            PostgresDevelopmentOptions? options = name == "start" ? new PostgresDevelopmentOptions
            {
                Port = result.GetValue(port),
                TimeoutSeconds = result.GetValue(timeout),
                UseValgrind = result.GetValue<bool>("--valgrind"),
                Settings = ParseServerSettings(result.GetValue(settings) ?? []),
            } : null;
            foreach (PostgresInstallation installation in installations)
            {
                var cluster = new PostgresDevelopmentCluster(installation, registry.HomeDirectory);
                if (name == "start")
                {
                    bool started = await cluster.StartAsync(options, token);
                    Console.WriteLine($"{installation.Label}: {(started ? "started" : "already running")}");
                    Console.WriteLine($"Data: {cluster.DataDirectory}");
                    Console.WriteLine($"Log: {cluster.LogFilePath}");
                }
                else if (name == "stop")
                {
                    bool stopped = await cluster.StopAsync(token);
                    Console.WriteLine($"{installation.Label}: {(stopped ? "stopped" : "already stopped")}");
                }
                else
                {
                    bool running = await cluster.IsRunningAsync(token);
                    Console.WriteLine($"{installation.Label}: {(running ? "running" : "stopped")}");
                }
            }
        });
        return command;
    }

    private static void AddValgrindOption(Command command)
        => command.Options.Add(new Option<bool>("--valgrind")
        {
            Description = "Start PostgreSQL under Valgrind Memcheck from PATH; diagnostics go to the server log (supported Unix platforms).",
        });

    private static Dictionary<string, string> ParseServerSettings(string[] settings)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string setting in settings)
        {
            int separator = setting.IndexOf('=');
            if (separator < 1)
            {
                throw new ArgumentException($"Expected a name=value PostgreSQL setting: {setting}");
            }

            string name = setting[..separator];
            string value = setting[(separator + 1)..];

            // PostgreSQL would read a relative pg_hba.conf path from its data directory; use the caller's directory.
            parsed[name] = string.Equals(name, "hba_file", StringComparison.OrdinalIgnoreCase) && value.Length > 0
                ? Path.GetFullPath(value)
                : value;
        }

        return parsed;
    }
}
