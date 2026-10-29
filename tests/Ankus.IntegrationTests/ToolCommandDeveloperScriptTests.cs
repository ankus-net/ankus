using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// An editor extension can set NoDefaultCurrentDirectoryInExePath for the editor's build host. Visual Studio's
    /// developer script then cannot run vswhere.exe by name and reports it on stderr, which .NET 10 Native AOT's own
    /// toolchain discovery reads with the tools directory. A new extension's design-time build and publication both
    /// succeed there, and neither log carries that error, which an editor lists as a problem.
    /// </summary>
    /// <param name="designTime">Whether to run an editor's design-time build instead of a publication.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BuildsIgnoreDeveloperScriptErrors(bool designTime)
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
        string[] restore = ["restore", project, "-nologo"];
        (await PackageProcessRunner.RunAsync("dotnet", restore, environment, token, workingDirectory: output))
            .EnsureSuccess("dotnet", restore);

        // The design-time build sets the properties an editor sets to collect compiler arguments.
        string log = Path.Join(output, "build.log");
        string[] build = designTime
            ? ["msbuild", project, "-t:Compile", "-p:DesignTimeBuild=true", "-p:SkipCompilerExecution=true", "-p:ProvideCommandLineArgs=true"]
            : ["publish", project, "--no-restore", "-c", "Release", "-o", Path.Join(output, "published")];
        ProcessResult result = await PackageProcessRunner.RunAsync("dotnet",
            [.. build, "-nologo", "-nodeReuse:false", "-flp:logfile=" + log + ";verbosity=diagnostic"], environment, token,
            workingDirectory: output);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.DoesNotContain("is not recognized as an internal or external command", await File.ReadAllTextAsync(log, token));
        if (!designTime)
        {
            Assert.IsTrue(File.Exists(Path.Join(output, "published", "developer_script.dll")), result.StandardOutput);
        }
    }
}
