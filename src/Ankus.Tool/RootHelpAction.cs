using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;

namespace Ankus.Tool;

/// <summary>
/// Lists the root command's subcommands by name and description, as the .NET CLI does. System.CommandLine labels each
/// one with its arguments as <c>&lt;name&gt;</c>, which reads as required even where they are optional; each command's
/// own help shows its exact usage.
/// </summary>
/// <param name="standard">The standard help action, which writes every other section and every subcommand's help.</param>
internal sealed class RootHelpAction(SynchronousCommandLineAction standard) : SynchronousCommandLineAction
{
    /// <inheritdoc />
    public override int Invoke(ParseResult parseResult)
    {
        if (parseResult.CommandResult.Command is not RootCommand root)
        {
            return standard.Invoke(parseResult);
        }

        InvocationConfiguration configuration = parseResult.InvocationConfiguration;
        TextWriter output = configuration.Output;
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        configuration.Output = buffer;
        int code;
        try
        {
            code = standard.Invoke(parseResult);
        }
        finally
        {
            configuration.Output = output;
        }

        // The command list is the last section; find it by its first command, since System.CommandLine localizes the
        // heading.
        Command[] commands = [.. root.Subcommands.Where(static command => !command.Hidden)];
        string[] lines = buffer.ToString().ReplaceLineEndings("\n").Split('\n');
        int first = Array.FindIndex(lines, line => line.StartsWith("  " + commands[0].Name + " ", StringComparison.Ordinal));
        if (first < 1)
        {
            output.Write(buffer.ToString());
            return code;
        }

        int width = commands.Max(static command => command.Name.Length) + 2;
        foreach (string line in lines[..first])
        {
            output.WriteLine(line);
        }

        foreach (Command command in commands)
        {
            output.WriteLine("  " + command.Name.PadRight(width) + command.Description);
        }

        output.WriteLine();
        return code;
    }
}
