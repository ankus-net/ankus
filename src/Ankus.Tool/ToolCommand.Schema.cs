using System.CommandLine;
using System.CommandLine.Parsing;
using System.Runtime.InteropServices;
using Ankus.PgConfig;

namespace Ankus.Tool;

internal static partial class ToolCommand
{
    private static readonly string[] s_schemaBuildOptions = ["--project", "--property", "--pg", "--pg-config", "--configuration", "--skip-build"];

    private static Command CreateSchema(Option<string?> home)
    {
        var command = new Command("schema", "Extract installation SQL from a Native AOT extension.");
        AddSelectionOptions(command);
        AddBuildOptions(command);
        var from = new Option<string?>("--from")
        {
            Description = "Read this native library without building or requiring a PostgreSQL installation.",
        };
        var skipBuild = new Option<bool>("--skip-build")
        {
            Description = "Read the project's existing default publish directory without rebuilding.",
        };
        var runtime = new Option<string?>("--runtime", "-r")
        {
            Description = "Required library RID; selects an architecture in a universal macOS library. Builds use the host RID.",
        };
        var output = new Option<string?>("--output", "-o") { Description = "SQL output file (default: stdout)." };
        var items = new Argument<string[]>("items")
        {
            Description = "Exact SQL names, managed names, signatures or dependency identifiers to select with their prerequisites.",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var dot = new Option<string?>("--dot") { Description = "Write the complete dependency graph as Graphviz DOT." };
        var noAlter = new Option<bool>("--no-alter-extension")
        {
            Description = "Emit selected SQL without a transaction or ALTER EXTENSION ADD statements.",
        };
        var targetSchema = new Option<string?>("--schema")
        {
            Description = "Installation schema for selected SQL; resolves @extschema@ and must agree with any fixed control schema.",
        };
        command.Arguments.Add(items);
        command.Options.Add(dot);
        command.Options.Add(noAlter);
        command.Options.Add(targetSchema);
        command.Options.Add(from);
        command.Options.Add(skipBuild);
        command.Options.Add(runtime);
        command.Options.Add(output);
        command.SetAction(async (result, token) =>
        {
            string[] selected = result.GetValue(items) ?? [];
            if (result.GetValue(noAlter) && selected.Length == 0)
            {
                throw new ArgumentException("--no-alter-extension requires at least one schema item.");
            }

            if (result.GetValue(targetSchema) is not null && selected.Length == 0)
            {
                throw new ArgumentException("--schema requires at least one schema item; full installation SQL retains PostgreSQL's schema substitution.");
            }

            string? library = result.GetValue(from);
            string? rid = result.GetValue(runtime);
            if (library is not null)
            {
                foreach (string option in s_schemaBuildOptions)
                {
                    if (result.GetResult(option) is OptionResult { Implicit: false })
                    {
                        throw new ArgumentException($"Use either --from or {option}.");
                    }
                }
            }
            else
            {
                if (rid is not null && rid != RuntimeInformation.RuntimeIdentifier)
                {
                    throw new ArgumentException("Project schema builds use the host runtime identifier; use --from for another target.");
                }

                if (result.GetValue(skipBuild) && result.GetValue<string?>("--pg-config") is not null)
                {
                    throw new ArgumentException("--pg-config requires a build; use --pg to select an existing publication.");
                }

                string configuration = GetConfiguration(result);
                string project = await ExtensionBuilder.ResolveProjectAsync(result.GetValue<string?>("--project"),
                    configuration, token, BuildProperties(result));
                int major = await SelectMajorAsync(result, token, project);
                string directory = Path.Join(Path.GetDirectoryName(project)!, "bin", "ankus",
                    "pg" + major.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    RuntimeInformation.RuntimeIdentifier, configuration);
                if (!result.GetValue(skipBuild))
                {
                    PostgresInstallation installation = await SelectAsync(result, home, token, resolvedProject: project);
                    int code = await ExtensionBuilder.PublishAsync(project, configuration, installation, directory, token,
                        diagnosticsToStandardError: true, properties: BuildProperties(result));
                    if (code != 0)
                    {
                        return code;
                    }
                }

                PublishedExtension publication = PublishedExtension.Read(directory);
                if (publication.PostgresMajor != major || publication.RuntimeIdentifier != RuntimeInformation.RuntimeIdentifier)
                {
                    throw new FormatException("The published extension does not match the selected PostgreSQL target.");
                }

                library = Path.Join(directory, publication.Library);
                ExtensionSchema existing = ExtensionSchema.Read(library, RuntimeInformation.RuntimeIdentifier);
                if (existing.Artifacts.PostgresMajor != major || existing.Artifacts.Library != publication.Library ||
                    existing.Artifacts.Control != publication.Control || existing.Artifacts.Sql != publication.Sql)
                {
                    throw new FormatException("The published manifest disagrees with the embedded schema identity.");
                }

                await WriteSchemaAsync(existing, library, result.GetValue(output), result.GetValue(dot), selected, !result.GetValue(noAlter), result.GetValue(targetSchema), token);
                return 0;
            }

            ExtensionSchema schema = ExtensionSchema.Read(library, rid);
            await WriteSchemaAsync(schema, library, result.GetValue(output), result.GetValue(dot), selected, !result.GetValue(noAlter), result.GetValue(targetSchema), token);
            return 0;
        });
        return command;
    }

    private static async Task WriteSchemaAsync(ExtensionSchema schema, string library, string? output, string? dot,
        string[] names, bool alterExtension, string? extensionSchema, CancellationToken token)
    {
        ExtensionSchemaSelection? selection = names.Length == 0 ? null : extensionSchema is null ? schema.Select(names, alterExtension) :
            schema.Select(names, extensionSchema, alterExtension);
        string sql = selection?.Sql ?? schema.Sql;
        string? graph = dot is null ? null : (schema.Graph ?? throw new InvalidOperationException(
            "This library does not contain a dependency graph. Rebuild it with a current Ankus SDK to export Graphviz DOT.")).ToGraphviz();
        string source = SchemaOutputPath(library);
        string? sqlPath = output is null ? null : SchemaOutputPath(output);
        string? graphPath = dot is null ? null : SchemaOutputPath(dot);
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(source, sqlPath, comparison))
        {
            throw new ArgumentException("The SQL output must differ from the native library path.");
        }

        if (string.Equals(source, graphPath, comparison) || sqlPath is not null && string.Equals(sqlPath, graphPath, comparison))
        {
            throw new ArgumentException("The Graphviz output must differ from the native library and SQL output paths.");
        }

        var outputs = new List<(string Path, string Text)>();
        if (output is not null)
        {
            outputs.Add((Path.GetFullPath(output), sql));
        }

        if (dot is not null)
        {
            outputs.Add((Path.GetFullPath(dot), graph!));
        }

        await WriteSchemaOutputsAsync(outputs, token);
        if (selection is not null)
        {
            foreach (string warning in selection.Warnings)
            {
                await Console.Error.WriteLineAsync(warning.AsMemory(), token);
            }
        }

        if (output is null)
        {
            await Console.Out.WriteAsync(sql.AsMemory(), token);
        }
    }

    /// <summary>
    /// Resolves existing directory and file aliases when checking schema input/output collisions.
    /// </summary>
    private static string SchemaOutputPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string component in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, component);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                current = entry.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
            }
        }

        return current;
    }

    /// <summary>
    /// Stages both schema outputs before replacing either and restores previous outputs after a failed replacement.
    /// </summary>
    private static async Task WriteSchemaOutputsAsync(List<(string Path, string Text)> outputs, CancellationToken token)
    {
        var staged = new List<(string Path, string Temporary, string? Backup)>();
        int replaced = 0;
        try
        {
            foreach ((string path, string text) in outputs)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add((path, temporary, File.Exists(path) ? temporary + ".backup" : null));
                await File.WriteAllTextAsync(temporary, text, token);
            }

            foreach ((string path, string temporary, string? backup) in staged)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (backup is null)
                    {
                        File.Move(temporary, path);
                    }
                    else
                    {
                        File.Replace(temporary, path, backup);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new IOException($"Could not replace schema output '{path}': {error.Message}", error);
                }

                replaced++;
            }
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            for (int index = replaced - 1; index >= 0; index--)
            {
                (string path, _, string? backup) = staged[index];
                try
                {
                    if (backup is null)
                    {
                        File.Delete(path);
                    }
                    else
                    {
                        File.Move(backup, path, overwrite: true);
                    }
                }
                // codeql[cs/catch-of-all-exceptions] Every file is restored even when another fails, and each failure is reported.
                catch (Exception rollback)
                {
                    failures.Add(new IOException($"Could not restore schema output '{path}'; its previous content remains at '{backup}'.", rollback));
                }
            }

            if (failures.Count != 1)
            {
                throw new AggregateException("Schema output failed and requires recovery.", failures);
            }

            throw;
        }
        finally
        {
            foreach ((_, string temporary, _) in staged)
            {
                File.Delete(temporary);
            }
        }

        foreach ((_, _, string? backup) in staged)
        {
            if (backup is not null)
            {
                File.Delete(backup);
            }
        }
    }
}
