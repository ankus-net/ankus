using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Visual Studio builds with its own MSBuild, which runs on .NET Framework: path functions reject an empty path there,
    /// and reference resolution reads each assembly's architecture. A new solution builds there without errors or
    /// warnings, whether its PostgreSQL installation comes from an explicit pg_config or from the Ankus home.
    /// </summary>
    /// <param name="explicitPgConfig">Whether the build names pg_config instead of using the Ankus home.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VisualStudioBuildsNewSolutionCleanly(bool explicitPgConfig)
    {
        CancellationToken token = context.CancellationToken;
        string msbuild = await FindVisualStudioMSBuildAsync(token);
        string output = Path.Join(CreateDirectory(), "visual studio solution");
        string[] creation = ["new", "VisualStudioBuild", "--output", output];
        (await InvokeAsync(creation, token)).EnsureSuccess(s_tool, creation);
        Dictionary<string, string?> environment = new(s_environment)
        {
            ["AnkusPgConfigPath"] = explicitPgConfig ? s_installation.PgConfigPath : null,
            ["ANKUS_HOME"] = explicitPgConfig ? null : s_home,
        };
        string[] build = [Path.Join(output, "VisualStudioBuild.slnx"), "-restore", "-t:Build", "-nologo", "-nodeReuse:false",
            "-p:Configuration=Debug", "-v:m"];
        ProcessResult result = await PackageProcessRunner.RunAsync(msbuild, build, environment, token, workingDirectory: output);
        string log = result.StandardOutput + result.StandardError;
        Assert.AreEqual(0, result.ExitCode, log);
        Assert.DoesNotContain(": warning ", log);
    }

    /// <summary>
    /// Finds the newest Visual Studio MSBuild, which the repository's Windows prerequisites already require.
    /// </summary>
    private static async Task<string> FindVisualStudioMSBuildAsync(CancellationToken token)
    {
        string locator = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        ProcessResult found = await ProcessRunner.RunAsync(locator,
            ["-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"],
            new Dictionary<string, string?>(), token);
        string path = found.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        Assert.IsTrue(File.Exists(path), "Visual Studio's MSBuild was not found: " + found.StandardOutput + found.StandardError);
        return path;
    }
}
