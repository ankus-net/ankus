using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Generated consumers report ordinary warnings, permit explicit strict builds and retain Ankus contract errors.
    /// </summary>
    /// <param name="backgroundWorker">Whether to create the background-worker template.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NewSolutionUsesConsumerWarningDefaults(bool backgroundWorker)
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Join(CreateDirectory(), "consumer warning solution");
        string[] creation = ["new", "WarningDefaults", "--output", output,
            .. backgroundWorker ? ["--background-worker"] : Array.Empty<string>()];
        (await InvokeAsync(creation, token)).EnsureSuccess(s_tool, creation);
        XDocument properties = XDocument.Load(Path.Join(output, "Directory.Build.props"));
        Assert.IsEmpty(properties.Descendants("TreatWarningsAsErrors"));
        string project = Path.Join(output, "src", "WarningDefaults", "WarningDefaults.csproj");
        string probe = Path.Join(Path.GetDirectoryName(project)!, "WarningProbe.cs");
        const string Warning = "#warning Consumer warnings remain visible.\n";
        await File.WriteAllTextAsync(probe, Warning, token);
        Dictionary<string, string?> environment = new(s_environment)
        {
            ["AnkusPostgresMajor"] = MajorText(),
            ["AnkusPgConfigPath"] = s_installation.PgConfigPath,
            ["TreatWarningsAsErrors"] = null,
            ["WarningsAsErrors"] = null,
            ["MSBuildWarningsAsErrors"] = null,
        };
        string[] build = ["build", project, "-c", "Release", "--no-incremental"];
        ProcessResult ordinary = await PackageProcessRunner.RunAsync("dotnet", build, environment, token, workingDirectory: output);
        Assert.AreEqual(0, ordinary.ExitCode, ordinary.StandardOutput + ordinary.StandardError);
        Assert.Contains("warning CS1030", ordinary.StandardOutput + ordinary.StandardError);
        Assert.Contains("Consumer warnings remain visible.", ordinary.StandardOutput + ordinary.StandardError);

        ProcessResult strict = await PackageProcessRunner.RunAsync("dotnet", [.. build, "-p:TreatWarningsAsErrors=true"],
            environment, token, workingDirectory: output);
        Assert.AreNotEqual(0, strict.ExitCode);
        Assert.Contains("error CS1030", strict.StandardOutput + strict.StandardError);

        await File.WriteAllTextAsync(probe, Warning + """
            namespace WarningDefaults;
            public static class UnsupportedEntry
            {
                [Ankus.PgFunction]
                public static System.Threading.Tasks.Task<int> Unsupported()
                    => System.Threading.Tasks.Task.FromResult(42);
            }
            """, token);
        ProcessResult invalid = await PackageProcessRunner.RunAsync("dotnet", build, environment, token, workingDirectory: output);
        Assert.AreNotEqual(0, invalid.ExitCode);
        Assert.Contains("error ANKUS031", invalid.StandardOutput + invalid.StandardError);
        Assert.Contains("warning CS1030", invalid.StandardOutput + invalid.StandardError);
    }
}
