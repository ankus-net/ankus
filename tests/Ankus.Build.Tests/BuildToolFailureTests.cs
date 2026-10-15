using System.Diagnostics;
using System.Security;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies that a build tool failure reaches the person building, not only its exit code.
/// </summary>
/// <param name="context">The current cancellation context.</param>
[TestClass]
public sealed class BuildToolFailureTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus build failure ").FullName;

    /// <summary>
    /// Removes the test's project and output.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// The first line on standard error is in MSBuild's canonical error format and carries the failure's message, and
    /// the details follow.
    /// </summary>
    [TestMethod]
    public async Task FailuresStartWithAnMSBuildError()
    {
        string pgConfig = Path.Join(_root, "missing", "pg_config");
        (int exitCode, string error) = await RunAsync("dotnet", [Tool, "binding-sources", "18", pgConfig, Path.Join(_root, "bindings")]);
        Assert.AreEqual(1, exitCode);
        string[] lines = error.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual($"Ankus.Build : error : The specified pg_config executable was not found. Path: '{pgConfig}'.", lines[0]);
        Assert.StartsWith("System.IO.FileNotFoundException: ", lines[1]);
    }

    /// <summary>
    /// Run by Exec as the SDK runs it, the failure is a build error whose text says what went wrong.
    /// </summary>
    [TestMethod]
    public async Task MSBuildReportsTheFailureAsAnError()
    {
        string pgConfig = Path.Join(_root, "missing", "pg_config");
        string command = $"dotnet &quot;{SecurityElement.Escape(Tool)}&quot; binding-sources &quot;18&quot; &quot;{SecurityElement.Escape(pgConfig)}&quot; &quot;{SecurityElement.Escape(Path.Join(_root, "bindings"))}&quot;";
        string project = Path.Join(_root, "failure.proj");
        await File.WriteAllTextAsync(project, $"""
            <Project>
              <Target Name="Build">
                <Exec Command="{command}" />
              </Target>
            </Project>
            """, context.CancellationToken);
        (int exitCode, string output) = await RunAsync("dotnet", ["msbuild", project, "-nologo", "-verbosity:minimal", "-nodeReuse:false"], standardOutput: true);
        Assert.AreNotEqual(0, exitCode);
        Assert.Contains($"Ankus.Build : error : The specified pg_config executable was not found. Path: '{pgConfig}'.", output);
    }

    private static string Tool => Path.Join(Path.GetDirectoryName(typeof(NativeBindingSourceCommand).Assembly.Location)!, "Ankus.Build.dll");

    private async Task<(int ExitCode, string Output)> RunAsync(string program, string[] arguments, bool standardOutput = false)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {program}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(context.CancellationToken);
        await process.WaitForExitAsync(context.CancellationToken);
        return (process.ExitCode, standardOutput ? await output + await error : await error);
    }
}
