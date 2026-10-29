using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// An editor extension can set NoDefaultCurrentDirectoryInExePath for the editor's build host. Visual Studio's
    /// developer script then cannot run vswhere.exe by name and reports it on stderr, which .NET 10 Native AOT's own
    /// toolchain discovery reads with the tools directory. A new extension's design-time build, its publication and a
    /// Visual Studio build all succeed there, and no log carries that error, which an editor lists as a problem.
    /// </summary>
    /// <param name="build">The design-time build, the publication or the Visual Studio build.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("DesignTime")]
    [DataRow("Publish")]
    [DataRow("VisualStudio")]
    public async Task BuildsIgnoreDeveloperScriptErrors(string build)
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Join(CreateDirectory(), "developer script");
        string[] creation = ["new", "DeveloperScript", "--output", output];
        (await InvokeAsync(creation, token)).EnsureSuccess(s_tool, creation);
        // CI selects its developer environment for Native AOT, so clear that to use the discovery a consumer gets.
        Dictionary<string, string?> environment = new(s_environment)
        {
            ["ANKUS_HOME"] = s_home,
            ["IlcUseEnvironmentalTools"] = null,
            ["NoDefaultCurrentDirectoryInExePath"] = "1",
        };
        string project = Path.Join(output, "src", "DeveloperScript", "DeveloperScript.csproj");
        string log = Path.Join(output, "build.log");
        string[] logging = ["-nologo", "-nodeReuse:false", "-flp:logfile=" + log + ";verbosity=diagnostic"];
        ProcessResult result;
        if (build == "VisualStudio")
        {
            string msbuild = await FindVisualStudioMSBuildAsync(token);
            result = await PackageProcessRunner.RunAsync(msbuild, [project, "-restore", "-t:Build", "-p:Configuration=Debug", .. logging],
                environment, token, workingDirectory: output);
        }
        else
        {
            string[] restore = ["restore", project, "-nologo"];
            (await PackageProcessRunner.RunAsync("dotnet", restore, environment, token, workingDirectory: output))
                .EnsureSuccess("dotnet", restore);

            // The design-time build sets the properties an editor sets to collect compiler arguments.
            string[] arguments = build == "DesignTime"
                ? ["msbuild", project, "-t:Compile", "-p:DesignTimeBuild=true", "-p:SkipCompilerExecution=true", "-p:ProvideCommandLineArgs=true"]
                : ["publish", project, "--no-restore", "-c", "Release", "-o", Path.Join(output, "published")];
            result = await PackageProcessRunner.RunAsync("dotnet", [.. arguments, .. logging], environment, token, workingDirectory: output);
        }

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.DoesNotContain("is not recognized as an internal or external command", await File.ReadAllTextAsync(log, token));
        if (build == "Publish")
        {
            Assert.IsTrue(File.Exists(Path.Join(output, "published", "developer_script.dll")), result.StandardOutput);
        }
    }
}
