using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using Ankus.PgConfig;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    private static readonly string[] s_getProjectOptions = ["--project", "--property", "--configuration", "--pg", "--pg-config"];

    private static Command CreateGet(Option<string?> home)
    {
        var command = new Command("get", "Print one extension control property or derived project value.");
        var property = new Argument<string>("property") { Description = "Control property, extname, or project git_hash." };
        var from = new Option<string?>("--from") { Description = "Read a published directory without building or discovering PostgreSQL." };
        command.Arguments.Add(property);
        command.Options.Add(from);
        AddSelectionOptions(command);
        AddBuildOptions(command);
        command.SetAction(async (result, token) =>
        {
            string name = result.GetValue(property)!;
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            string? value;
            if (result.GetValue(from) is string publication)
            {
                foreach (string option in s_getProjectOptions)
                {
                    if (result.GetResult(option) is OptionResult { Implicit: false })
                    {
                        throw new ArgumentException($"Use either --from or {option}.");
                    }
                }

                if (name == "git_hash")
                {
                    throw new ArgumentException("git_hash describes a source project; use --project instead of --from.");
                }

                PublishedExtension manifest = PublishedExtension.Read(publication);
                IReadOnlyDictionary<string, string> settings = ExtensionControlFile.Read(Path.Join(publication, "extension", manifest.Control));
                value = name == "extname" ? Path.GetFileNameWithoutExtension(manifest.Control) : settings.GetValueOrDefault(name);
            }
            else
            {
                string project = await ExtensionBuilder.ResolveProjectAsync(result.GetValue<string?>("--project"),
                    GetConfiguration(result), token, BuildProperties(result));
                if (name == "git_hash")
                {
                    using var output = new MemoryStream();
                    int code = await ToolProcess.RunAsync("git", ["-C", Path.GetDirectoryName(project)!, "rev-parse", "--verify", "HEAD"],
                        token, outputStream: output);
                    if (code == 0)
                    {
                        await Console.Out.WriteAsync(Encoding.UTF8.GetString(output.ToArray()).AsMemory(), token);
                    }

                    return code;
                }

                int major = await SelectMajorAsync(result, token, project);
                string configuration = GetConfiguration(result);
                if (name == "extname")
                {
                    value = await ExtensionBuilder.GetExtensionNameAsync(project, configuration, major,
                        result.GetValue<string?>("--pg-config"), token, BuildProperties(result));
                }
                else
                {
                    PostgresInstallation installation = await SelectAsync(result, home, token, resolvedProject: project);
                    string temporary = Directory.CreateTempSubdirectory("ankus-control-query-").FullName;
                    try
                    {
                        string control = Path.Join(temporary, "extension.control");
                        int code = await ExtensionBuilder.GenerateControlAsync(project, configuration, installation, control, token, BuildProperties(result));
                        if (code != 0)
                        {
                            return code;
                        }

                        value = ExtensionControlFile.Read(control).GetValueOrDefault(name);
                    }
                    finally
                    {
                        Directory.Delete(temporary, recursive: true);
                    }
                }
            }

            if (value is not null)
            {
                await Console.Out.WriteLineAsync(value.AsMemory(), token);
            }

            return 0;
        });
        return command;
    }
}
