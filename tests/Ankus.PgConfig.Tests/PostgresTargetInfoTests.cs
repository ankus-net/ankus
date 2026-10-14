using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies the exported target format: path mapping, the installation it describes, integrity checks and extraction.
/// </summary>
[TestClass]
public sealed class PostgresTargetInfoTests(TestContext context)
{
    private const string Header = "#define PG_TARGET_PROBE 1\n";

    private readonly string _directory = Directory.CreateTempSubdirectory("ankus-target-tests-").FullName;

    /// <summary>
    /// Removes this test's directories.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Header directories and search flags name the captured copies, while the directories packages mirror keep the
    /// target's values and the target's tools are unavailable.
    /// </summary>
    [TestMethod]
    public async Task InstallationReadsTargetBuildValues()
    {
        string root = WriteTarget(Path.Join(_directory, "target"));
        PostgresInstallation installation = await PostgresInstallation.CreateAsync(Path.Join(root, PostgresTargetInfo.ManifestFileName),
            context.CancellationToken);
        PostgresTargetInfo target = installation.TargetInfo!;
        Assert.IsNotNull(target);
        Assert.AreEqual(new PostgresVersion(18, 6, PostgresReleaseStage.Stable, 0), installation.Version);
        Assert.AreEqual("linux-x64", target.RuntimeIdentifier);
        Assert.AreEqual("1.2.3+abc", target.AnkusVersion);
        Assert.AreEqual(Rooted("opt", "pg", "lib"), installation.LibraryDirectory);
        Assert.AreEqual(Rooted("opt", "pg", "share"), installation.SharedDirectory);
        Assert.AreEqual(target.MapPath(Rooted("opt", "pg", "include", "server")), installation.ServerIncludeDirectory);
        Assert.AreEqual(Path.Join(root, "files", ArchivePath("opt", "pg", "include", "server")), installation.ServerIncludeDirectory);
        Assert.AreEqual(Header, await File.ReadAllTextAsync(Path.Join(installation.ServerIncludeDirectory, "postgres.h"), context.CancellationToken));
        Assert.AreSequenceEqual(
        [
            "-I" + target.MapPath(Rooted("opt", "deps", "include")),
            "-D_GNU_SOURCE",
            "-isystem",
            target.MapPath(Rooted("opt", "more")),
            "-Irelative",
        ], installation.PreprocessorArguments);
        Assert.ThrowsExactly<InvalidOperationException>(() => installation.PsqlPath);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => installation.GetRegressionDriverPathAsync(context.CancellationToken));
        Assert.AreEqual(Path.Join(root, "bindings", "native-binding.identity"), target.GetBindingPath("native-binding.identity"));
        Assert.ThrowsExactly<ArgumentException>(() => target.GetBindingPath("../native-binding.identity"));
        Assert.AreEqual("ankus-target.pg18.osx-x64.zip", PostgresTargetInfo.GetArchiveFileName(18, "osx-x64"));
    }

    /// <summary>
    /// Verification names a changed or missing file.
    /// </summary>
    [TestMethod]
    public async Task VerifyRejectsChangedAndMissingFiles()
    {
        string root = WriteTarget(Path.Join(_directory, "target"));
        PostgresTargetInfo target = PostgresTargetInfo.Open(root);
        await target.VerifyAsync(context.CancellationToken);
        string header = Path.Join(target.MapPath(Rooted("opt", "pg", "include", "server")), "postgres.h");
        await File.WriteAllTextAsync(header, Header + "#define CHANGED 1\n", context.CancellationToken);
        InvalidDataException changed = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => target.VerifyAsync(context.CancellationToken));
        Assert.Contains("does not match its manifest", changed.Message);
        File.Delete(header);
        InvalidDataException missing = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => target.VerifyAsync(context.CancellationToken));
        Assert.Contains("is missing", missing.Message);
    }

    /// <summary>
    /// A manifest cannot name files outside its own directories, and its plain values must agree with it.
    /// </summary>
    /// <param name="name">A file name the manifest lists.</param>
    [TestMethod]
    [DataRow("files/../outside.h")]
    [DataRow("other/postgres.h")]
    [DataRow("/files/postgres.h")]
    [DataRow("files/./postgres.h")]
    [DataRow("files\\postgres.h")]
    [DataRow("files/C:/postgres.h")]
    public void OpenRejectsUnsafeFileNames(string name)
    {
        string root = WriteTarget(Path.Join(_directory, "target"));
        RewriteManifest(root, manifest => manifest["files"]!.AsObject()[name] = new string('0', 64));
        Assert.ThrowsExactly<InvalidDataException>(() => PostgresTargetInfo.Open(root));
    }

    /// <summary>
    /// Plain values that disagree with the manifest, or an unknown format, are rejected.
    /// </summary>
    [TestMethod]
    public void OpenRejectsInconsistentValuesAndFormats()
    {
        string root = WriteTarget(Path.Join(_directory, "runtime"));
        File.WriteAllText(Path.Join(root, "runtime-identifier"), "linux-arm64\n");
        Assert.ThrowsExactly<InvalidDataException>(() => PostgresTargetInfo.Open(root));

        root = WriteTarget(Path.Join(_directory, "major"));
        File.WriteAllText(Path.Join(root, "postgres-major"), "17\n");
        Assert.ThrowsExactly<InvalidDataException>(() => PostgresTargetInfo.Open(root));

        root = WriteTarget(Path.Join(_directory, "format"));
        RewriteManifest(root, manifest => manifest["format"] = 2);
        InvalidDataException format = Assert.ThrowsExactly<InvalidDataException>(() => PostgresTargetInfo.Open(root));
        Assert.Contains("format 2 is not supported", format.Message);
    }

    /// <summary>
    /// An archive extracts once into a directory named by its hash, and a second extraction reuses it.
    /// </summary>
    [TestMethod]
    public async Task ExtractReusesTheArchiveDirectory()
    {
        string root = WriteTarget(Path.Join(_directory, "target"));
        string archive = Path.Join(_directory, "target.zip");
        ZipFile.CreateFromDirectory(root, archive);
        string cache = Path.Join(_directory, "cache");
        PostgresTargetInfo first = await PostgresTargetInfo.ExtractAsync(archive, cache, context.CancellationToken);
        string expected = Path.Join(cache, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))));
        Assert.AreEqual(expected, first.RootDirectory);
        File.WriteAllText(Path.Join(expected, "unrelated"), "kept");
        PostgresTargetInfo second = await PostgresTargetInfo.ExtractAsync(archive, cache, context.CancellationToken);
        Assert.AreEqual(expected, second.RootDirectory);
        Assert.IsTrue(File.Exists(Path.Join(expected, "unrelated")));
        Assert.HasCount(1, Directory.GetDirectories(cache));
    }

    /// <summary>
    /// Writes an extracted target with one server header and one binding artifact.
    /// </summary>
    private static string WriteTarget(string root)
    {
        string header = Path.Join(root, "files", ArchivePath("opt", "pg", "include", "server"), "postgres.h");
        Directory.CreateDirectory(Path.GetDirectoryName(header)!);
        File.WriteAllText(header, Header);
        string binding = Path.Join(root, "bindings", "native-binding.identity");
        Directory.CreateDirectory(Path.GetDirectoryName(binding)!);
        File.WriteAllText(binding, "identity\n");
        var files = new JsonObject
        {
            ["files/" + ArchivePath("opt", "pg", "include", "server").Replace('\\', '/') + "/postgres.h"] = Hash(Header),
            ["bindings/native-binding.identity"] = Hash("identity\n"),
        };
        var manifest = new JsonObject
        {
            ["format"] = 1,
            ["ankusVersion"] = "1.2.3+abc",
            ["runtimeIdentifier"] = "linux-x64",
            ["targetTriple"] = "",
            ["postgresMajor"] = 18,
            ["postgres"] = new JsonObject
            {
                ["version"] = "PostgreSQL 18.6",
                ["bindir"] = Rooted("opt", "pg", "bin"),
                ["pkglibdir"] = Rooted("opt", "pg", "lib"),
                ["sharedir"] = Rooted("opt", "pg", "share"),
                ["includedir"] = Rooted("opt", "pg", "include"),
                ["includedirServer"] = Rooted("opt", "pg", "include", "server"),
                // pg_config reports shell words, where a backslash escapes; Windows paths here use forward slashes.
                ["cppflags"] = "-I" + Rooted("opt", "deps", "include").Replace('\\', '/') + " -D_GNU_SOURCE -isystem " +
                    Rooted("opt", "more").Replace('\\', '/') + " -Irelative",
            },
            ["files"] = files,
        };
        File.WriteAllText(Path.Join(root, PostgresTargetInfo.ManifestFileName), manifest.ToJsonString());
        File.WriteAllText(Path.Join(root, "postgres-major"), "18\n");
        File.WriteAllText(Path.Join(root, "runtime-identifier"), "linux-x64\n");
        return root;
    }

    private static void RewriteManifest(string root, Action<JsonObject> change)
    {
        string path = Path.Join(root, PostgresTargetInfo.ManifestFileName);
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(manifest);
        File.WriteAllText(path, manifest.ToJsonString());
    }

    /// <summary>
    /// Builds an absolute path on this platform, on drive C for Windows.
    /// </summary>
    private static string Rooted(params string[] segments)
        => Path.Join([OperatingSystem.IsWindows() ? @"C:\" : "/", .. segments]);

    /// <summary>
    /// Gives the location of an absolute path beneath the files directory.
    /// </summary>
    private static string ArchivePath(params string[] segments)
        => Path.Join([.. OperatingSystem.IsWindows() ? ["C"] : Array.Empty<string>(), .. segments]);

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
