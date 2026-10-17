using System.CommandLine;
using System.Globalization;
using Ankus.Tool;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies what the ankus command's help shows, as people read it before running anything.
/// </summary>
[TestClass]
public sealed class ToolHelpTests
{
    /// <summary>
    /// The root help lists commands by name and description, as the .NET CLI does, without argument labels that make
    /// optional arguments such as <c>run</c>'s client arguments look required.
    /// </summary>
    [TestMethod]
    public void RootHelpListsCommandsWithoutArguments()
    {
        string help = Help("--help");
        Assert.Contains("\n  run      Build and install an extension, start PostgreSQL, and open a SQL client.\n", help);
        Assert.Contains("\n  upgrade  Upgrade the Ankus framework packages and project SDK references.\n", help);
        string commands = help[help.IndexOf("\n  init ", StringComparison.Ordinal)..];
        Assert.DoesNotContain("<", commands);
        Assert.Contains("--version", help);
    }

    /// <summary>
    /// Every command shows each default once and lists the Ankus home option before the standard help option.
    /// </summary>
    [TestMethod]
    public void CommandHelpShowsDefaultsOnceAndHomeBeforeHelp()
    {
        using var cancellation = new InteractiveCommandCancellation();
        foreach (Command command in ToolCommand.CreateRoot(cancellation).Subcommands)
        {
            string help = Help(command.Name, "--help");
            foreach (string line in help.Split('\n'))
            {
                Assert.IsFalse(line.Contains("(default:", StringComparison.Ordinal) && line.Contains("[default:", StringComparison.Ordinal), line);
            }

            if (help.Contains("--home <home>", StringComparison.Ordinal))
            {
                Assert.IsLessThan(help.IndexOf("-?, -h, --help", StringComparison.Ordinal), help.IndexOf("--home <home>", StringComparison.Ordinal), command.Name);
            }
        }

        Assert.Contains("MSBuild configuration, including custom configurations. [default: Release]", Help("package", "--help"));
        Assert.Contains("MSBuild configuration, including custom configurations. [default: Debug]", Help("test", "--help"));
    }

    /// <summary>
    /// <c>schema</c> offers <c>--no-build</c>, as <c>run</c> and <c>dotnet</c> do, and still accepts the
    /// <c>--skip-build</c> name <c>cargo pgrx schema</c> uses without listing it.
    /// </summary>
    [TestMethod]
    public void SchemaUsesNoBuildAndAcceptsSkipBuild()
    {
        string help = Help("schema", "--help");
        Assert.Contains("--no-build", help);
        Assert.DoesNotContain("--skip-build", help);
        using var cancellation = new InteractiveCommandCancellation();
        RootCommand root = ToolCommand.CreateRoot(cancellation);
        Assert.IsEmpty(root.Parse(["schema", "--skip-build"]).Errors);
        Assert.IsEmpty(root.Parse(["schema", "--no-build"]).Errors);
    }

    private static string Help(params string[] arguments)
    {
        using var cancellation = new InteractiveCommandCancellation();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        int code = ToolCommand.CreateRoot(cancellation).Parse(arguments).Invoke(new InvocationConfiguration { Output = output });
        Assert.AreEqual(0, code);
        return output.ToString().ReplaceLineEndings("\n");
    }
}
