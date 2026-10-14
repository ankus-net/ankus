using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Target information exported on this machine packages a loadable extension without measuring the headers again:
    /// the build cannot reach Clang or libclang, so its bindings must come from the archive.
    /// </summary>
    [TestMethod]
    public async Task TargetInfoPackagesLoadableExtensionWithoutLocalMeasurement()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = CreateTargetProject(directory);
        string archive = Path.Join(directory, "target", "exported.zip");
        ProcessResult export = await InvokeAsync(["target", "export", "--home", s_home, "--pg", MajorText(), "--project", project,
            "--output", archive], token);
        Assert.AreEqual(0, export.ExitCode, export.StandardOutput + export.StandardError);
        Assert.Contains($"Exported {s_installation.Label} target information for {RuntimeInformation.RuntimeIdentifier}", export.StandardOutput);
        string extracted = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ankus", "targets",
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(archive, token))));
        try
        {
            string output = Path.Join(directory, "package");
            string missing = Path.Join(directory, "no-clang", OperatingSystem.IsWindows() ? "clang-cl.exe" : "clang");
            ProcessResult package = await InvokeTargetAsync(["package", "--home", s_home, "--target-info", archive, "--project", project,
                "--output", output, "-p:AnkusClangPath=" + missing, "-p:AnkusLibClangPath=" + missing], token);
            Assert.AreEqual(0, package.ExitCode, package.StandardOutput + package.StandardError);
            Assert.Contains($"Native binding sources: imported from PostgreSQL {s_installation.Version} on {RuntimeInformation.RuntimeIdentifier}.",
                package.StandardOutput);

            PostgresTargetInfo target = PostgresTargetInfo.Open(extracted);
            Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, target.RuntimeIdentifier);
            Assert.AreEqual(s_installation.Version, target.Version);
            Assert.IsTrue(File.Exists(Path.Join(target.MapPath(s_installation.ServerIncludeDirectory), "postgres.h")));
            Assert.AreEqual(await File.ReadAllTextAsync(target.GetBindingPath("native-binding.identity"), token),
                await File.ReadAllTextAsync(Path.Join(directory, "obj", "Release", "net10.0", RuntimeInformation.RuntimeIdentifier,
                    "ankus-bindings", "native-binding.identity"), token));

            string publication = Path.Join(directory, "bin", "ankus", s_installation.Label, RuntimeInformation.RuntimeIdentifier, "Release");
            PublishedExtension manifest = PublishedExtension.Read(publication);
            Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, manifest.RuntimeIdentifier);
            await AssertPackagedExtensionAsync(output, manifest, "ankus_target_probe", "0.1.0", token);
        }
        finally
        {
            if (Directory.Exists(extracted))
            {
                Directory.Delete(extracted, recursive: true);
            }
        }
    }

    /// <summary>
    /// A build rejects target information for another runtime, from another Ankus version or with altered files, and a
    /// command cannot select both target information and a pg_config.
    /// </summary>
    [TestMethod]
    public async Task TargetInfoRejectsMismatchedOrAlteredInformation()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = CreateTargetProject(directory);
        string archive = Path.Join(directory, "exported.zip");
        ProcessResult export = await InvokeAsync(["target", "export", "--home", s_home, "--pg", MajorText(), "--project", project,
            "--output", archive], token);
        Assert.AreEqual(0, export.ExitCode, export.StandardOutput + export.StandardError);

        ProcessResult both = await InvokeTargetAsync(["package", "--home", s_home, "--target-info", archive,
            "--pg-config", s_installation.PgConfigPath, "--project", project], token);
        Assert.AreEqual(1, both.ExitCode);
        Assert.Contains("Use either --target-info or a pg_config.", both.StandardError);

        string altered = Path.Join(directory, "altered");
        ZipFile.ExtractToDirectory(archive, altered);
        await File.AppendAllTextAsync(Path.Join(altered, "bindings", "native-binding.g.cs"), "// altered\n", token);
        ProcessResult changed = await InvokeTargetAsync(["package", "--home", s_home, "--target-info", altered, "--project", project], token);
        Assert.AreEqual(1, changed.ExitCode);
        Assert.Contains("Target information file 'bindings/native-binding.g.cs' does not match its manifest.", changed.StandardError);

        string foreign = Path.Join(directory, "foreign");
        ZipFile.ExtractToDirectory(archive, foreign);
        string other = RuntimeInformation.RuntimeIdentifier.EndsWith("-x64", StringComparison.Ordinal)
            ? RuntimeInformation.RuntimeIdentifier[..^4] + "-arm64" : RuntimeInformation.RuntimeIdentifier[..^6] + "-x64";
        await RewriteManifestAsync(foreign, manifest => manifest["runtimeIdentifier"] = other, token);
        await File.WriteAllTextAsync(Path.Join(foreign, "runtime-identifier"), other + "\n", token);
        ProcessResult runtime = await PackageProcessRunner.RunAsync("dotnet", ["build", project, "-nologo",
            "-p:AnkusTargetInfo=" + foreign], TargetEnvironment(), token, workingDirectory: s_root);
        Assert.AreNotEqual(0, runtime.ExitCode);
        Assert.Contains($"AnkusTargetInfo describes {other}. Build with RuntimeIdentifier={other}.", runtime.StandardOutput);

        string older = Path.Join(directory, "older");
        ZipFile.ExtractToDirectory(archive, older);
        await RewriteManifestAsync(older, manifest => manifest["ankusVersion"] = "0.0.0-older", token);
        ProcessResult version = await InvokeTargetAsync(["package", "--home", s_home, "--target-info", older, "--project", project], token);
        Assert.AreEqual(1, version.ExitCode);
        Assert.Contains("The target information was exported by Ankus 0.0.0-older", version.StandardOutput + version.StandardError);
    }

    private static string CreateTargetProject(string directory)
    {
        string project = Path.Join(directory, "TargetProbe.csproj");
        string text = File.ReadAllText(s_project).Replace("ankus_tool_probe", "ankus_target_probe", StringComparison.Ordinal);
        File.WriteAllText(project, text);
        File.Copy(Path.Join(Path.GetDirectoryName(s_project)!, "Hello.cs"), Path.Join(directory, "Hello.cs"));
        return project;
    }

    private static async Task RewriteManifestAsync(string directory, Action<JsonObject> change, CancellationToken token)
    {
        string path = Path.Join(directory, PostgresTargetInfo.ManifestFileName);
        JsonObject manifest = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        change(manifest);
        await File.WriteAllTextAsync(path, manifest.ToJsonString(), token);
    }

    /// <summary>
    /// Runs the tool without the fixture's pg_config selection, which target information replaces.
    /// </summary>
    private static Task<ProcessResult> InvokeTargetAsync(string[] arguments, CancellationToken token)
        => PackageProcessRunner.RunAsync(s_tool, arguments, TargetEnvironment(), token, workingDirectory: s_root);

    private static Dictionary<string, string?> TargetEnvironment()
        => new(s_environment) { ["AnkusPgConfigPath"] = null };
}
