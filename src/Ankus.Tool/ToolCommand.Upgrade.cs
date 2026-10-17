using System.CommandLine;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    private static Command CreateUpgrade()
    {
        var command = new Command("upgrade", "Upgrade the Ankus framework packages and project SDK references.");
        var project = new Option<string?>("--project") { Description = "Project file or directory (default: the current project or solution)." };
        var solution = new Option<string?>("--solution") { Description = "Solution whose project references should be upgraded." };
        var package = new Option<string?>("--package", "-p") { Description = "Select one project by name within the solution." };
        var target = new Option<string?>("--to") { Description = "NuGet version or requirement (default: discover the latest stable version)." };
        var prerelease = new Option<bool>("--include-prereleases") { Description = "Include prerelease versions during discovery." };
        var dryRun = new Option<bool>("--dry-run", "-n") { Description = "Print the updated manifests without writing them." };
        var config = new Option<string?>("--configfile") { Description = "Explicit NuGet configuration file for version discovery." };
        var sources = new Option<string[]>("--source") { Description = "Override NuGet package sources; repeat for multiple sources." };
        command.Options.Add(project);
        command.Options.Add(solution);
        command.Options.Add(package);
        command.Options.Add(target);
        command.Options.Add(prerelease);
        command.Options.Add(dryRun);
        command.Options.Add(config);
        command.Options.Add(sources);
        command.SetAction(async (result, token) =>
        {
            if (result.GetValue(project) is not null && result.GetValue(solution) is not null)
            {
                throw new ArgumentException("Select either --project or --solution.");
            }

            string? configuration = result.GetValue(config);
            var versions = new FrameworkVersionResolver(result.GetValue(target), result.GetValue(prerelease),
                configuration is null ? null : Path.GetFullPath(configuration), result.GetValue(sources) ?? []);
            string[] projects = FrameworkUpgrade.SelectProjects(result.GetValue(project) ?? result.GetValue(solution), result.GetValue(package));
            UpgradeDocument[] documents = await FrameworkUpgrade.PlanAsync(projects, versions, token);
            UpgradeDocument[] changed = [.. documents.Where(static document => document.Changed)];
            if (result.GetValue(dryRun))
            {
                foreach (UpgradeDocument document in changed)
                {
                    Console.WriteLine($"--- {document.Path}");
                    Console.WriteLine(document.Render());
                }
            }
            else
            {
                await UpgradeTransaction.WriteAsync(documents, token);
                foreach (UpgradeDocument document in changed)
                {
                    Console.WriteLine($"Updated {document.Path}");
                }
            }

            if (changed.Length == 0)
            {
                Console.WriteLine("Ankus framework references are already current.");
            }

            if (versions.Pending is string pending)
            {
                Console.WriteLine(pending);
            }
        });
        return command;
    }
}
