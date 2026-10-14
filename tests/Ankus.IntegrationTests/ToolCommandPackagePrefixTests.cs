using System.Runtime.InteropServices;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Prefix partitions select exact package destinations while retaining source bytes and unrelated output.
    /// </summary>
    /// <param name="kind">The requested prefix partition.</param>
    [TestMethod]
    [DataRow("relative")]
    [DataRow("absolute")]
    [DataRow("root")]
    [DataRow("normalized")]
    public async Task PackagePrefixPreservesLayoutAndPayload(string kind)
    {
        CancellationToken token = context.CancellationToken;
        string source = CreateDirectory();
        PublishedExtension manifest = await CopyPrefixPublicationAsync(source, token);
        string output = CreateDirectory();
        string prefix = kind switch
        {
            "absolute" => Path.Join(Path.GetPathRoot(s_installation.SharedDirectory)!, "custom prefix", "extension"),
            "root" => Path.GetPathRoot(s_installation.SharedDirectory)!,
            "normalized" => Path.Join("custom prefix", "temporary", "..", "extension"),
            _ => Path.Join("custom prefix", "extension"),
        };
        string assets = kind == "root" ? output : Path.Join(output, "custom prefix", "extension");
        string libraries = OperatingSystem.IsWindows() ? PackageLibraryDirectory(output) : assets;
        string controls = OperatingSystem.IsWindows() ? Path.Join(PackageSharedDirectory(output), "extension") : assets;
        string marker = Path.Join(output, "keep.txt");
        await File.WriteAllTextAsync(marker, "unrelated package content", token);
        string[] installedPaths =
        [
            Path.Join(s_installation.LibraryDirectory, manifest.Library),
            Path.Join(s_installation.SharedDirectory, "extension", manifest.Control),
            Path.Join(s_installation.SharedDirectory, "extension", manifest.Sql),
        ];
        Dictionary<string, byte[]?> installed = installedPaths.ToDictionary(static path => path,
            static path => File.Exists(path) ? File.ReadAllBytes(path) : null);
        ProcessResult result = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--from", source,
            "--output", output, "--prefix-dir", prefix], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string[] published = [manifest.Library, Path.Join("extension", manifest.Control), Path.Join("extension", manifest.Sql)];
        string[] packaged = [Path.Join(libraries, manifest.Library), Path.Join(controls, manifest.Control), Path.Join(controls, manifest.Sql)];
        await File.WriteAllTextAsync(packaged[2], "stale prefixed SQL", token);
        ProcessResult repeated = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--from", source,
            "--output", output, "--prefix-dir", prefix], token);
        Assert.AreEqual(0, repeated.ExitCode, repeated.StandardOutput + repeated.StandardError);
        for (int index = 0; index < published.Length; index++)
        {
            byte[] original = await File.ReadAllBytesAsync(Path.Join(s_published, published[index]), token);
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(packaged[index], token));
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(Path.Join(source, published[index]), token));
        }

        Assert.HasCount(4, Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        Assert.AreEqual("unrelated package content", await File.ReadAllTextAsync(marker, token));
        Assert.IsFalse(Directory.Exists(Path.Join(output, "custom prefix", "temporary")));
        foreach ((string path, byte[]? original) in installed)
        {
            if (original is null)
            {
                Assert.IsFalse(File.Exists(path));
            }
            else
            {
                Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(path, token));
            }
        }
    }

    /// <summary>
    /// Building a project with a prefix retains the default output root and produces a loadable native extension.
    /// </summary>
    [TestMethod]
    public async Task PackagePrefixBuildsWithDefaultOutput()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "PrefixProbe.csproj");
        XDocument.Load(s_project).Save(project);
        File.Copy(Path.Join(Path.GetDirectoryName(s_project)!, "Hello.cs"), Path.Join(directory, "Hello.cs"));
        ProcessResult result = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--project", project,
            "--prefix-dir", Path.Join("custom prefix", "extension")], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string publication = Path.Join(directory, "bin", "ankus", s_installation.Label, RuntimeInformation.RuntimeIdentifier, "Release");
        PublishedExtension manifest = PublishedExtension.Read(publication);
        string name = Path.GetFileNameWithoutExtension(manifest.Control);
        string output = Path.Join(publication, name + "-" + s_installation.Label);
        string libraries = OperatingSystem.IsWindows() ? PackageLibraryDirectory(output) : Path.Join(output, "custom prefix", "extension");
        string shared = OperatingSystem.IsWindows() ? PackageSharedDirectory(output) : Path.Join(output, "custom prefix");
        Assert.HasCount(3, Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        await AssertPackagedExtensionAsync(output, manifest, name, "0.1.0", token, libraryDirectory: libraries, sharedDirectory: shared);
    }

    /// <summary>
    /// SQL directory declarations retain PostgreSQL's version-specific base and all upgrade/control payloads.
    /// </summary>
    /// <param name="absolute">Whether the SQL directory is absolute rather than relative to the selected control base.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PackagePrefixPreservesCustomSqlDirectories(bool absolute)
    {
        CancellationToken token = context.CancellationToken;
        string absoluteDirectory = Path.Join(CreateDirectory(), "SQL 'files");
        string directory = absolute ? absoluteDirectory : "nested/SQL files";
        string source = CreateDirectory();
        PublishedExtension manifest = await CopyScriptDirectoryPublicationAsync(source, directory, token);
        string output = CreateDirectory();
        ProcessResult result = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--from", source,
            "--output", output, "--prefix-dir", Path.Join("custom prefix", "extension")], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string assets = Path.Join(output, "custom prefix", "extension");
        string controls = OperatingSystem.IsWindows() ? Path.Join(PackageSharedDirectory(output), "extension") : assets;
        string libraries = OperatingSystem.IsWindows() ? PackageLibraryDirectory(output) : assets;
        string shared = OperatingSystem.IsWindows() || s_installation.Version.Major < 18
            ? PackageSharedDirectory(output) : Path.Join(output, "custom prefix");
        string scripts = absolute ? StagedPath(output, absoluteDirectory) : Path.Join(shared, "nested", "SQL files");
        string[] scriptNames = [manifest.Sql, .. manifest.UpgradeScripts, .. manifest.VersionControlFiles];
        foreach (string name in scriptNames)
        {
            Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(source, "extension", name), token),
                await File.ReadAllBytesAsync(Path.Join(scripts, name), token));
        }

        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(source, manifest.Library), token),
            await File.ReadAllBytesAsync(Path.Join(libraries, manifest.Library), token));
        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(source, "extension", manifest.Control), token),
            await File.ReadAllBytesAsync(Path.Join(controls, manifest.Control), token));
        Assert.AreEqual(directory, ExtensionControlFile.Read(Path.Join(controls, manifest.Control))["directory"]);
        Assert.HasCount(5, Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(absoluteDirectory));
    }

    /// <summary>
    /// A real server discovers and executes the prefixed package after its entire output root moves.
    /// </summary>
    [TestMethod]
    public async Task PackagePrefixLoadsAfterMove()
    {
        CancellationToken token = context.CancellationToken;
        string output = CreateDirectory();
        ProcessResult result = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--from", s_published,
            "--output", output, "--prefix-dir", Path.Join("custom prefix", "extension")], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string relocated = output + " moved";
        Directory.Move(output, relocated);
        string libraries = OperatingSystem.IsWindows() ? PackageLibraryDirectory(relocated) : Path.Join(relocated, "custom prefix", "extension");
        string shared = OperatingSystem.IsWindows() ? PackageSharedDirectory(relocated) : Path.Join(relocated, "custom prefix");
        await AssertPackagedExtensionAsync(relocated, PublishedExtension.Read(s_published), "ankus_tool_probe", "0.1.0", token,
            libraryDirectory: libraries, sharedDirectory: shared);
    }

    /// <summary>
    /// Invalid prefixes fail before installation lookup, preserve existing output and never create sibling payloads.
    /// </summary>
    /// <param name="prefix">The invalid prefix.</param>
    /// <param name="message">The required diagnostic.</param>
    [TestMethod]
    [DataRow("", "prefixDirectory")]
    [DataRow("   ", "prefixDirectory")]
    [DataRow("../escaped prefix", "escapes the output directory")]
    [DataRow("nested/../../escaped prefix", "escapes the output directory")]
    public Task PackagePrefixRejectsInvalidDirectory(string prefix, string message)
        => AssertInvalidPackagePrefixAsync(prefix, message);

    /// <summary>
    /// Drive-relative and drive-less rooted prefixes fail instead of selecting a different Windows destination.
    /// </summary>
    /// <param name="prefix">The partially rooted Windows path.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("C:relative prefix")]
    [DataRow("\\partially rooted prefix")]
    public Task PackagePrefixRejectsPartiallyRootedWindowsDirectories(string prefix)
        => AssertInvalidPackagePrefixAsync(prefix, "partially rooted");

    /// <summary>
    /// Verifies early validation with exact preservation of the owned output and original publication.
    /// </summary>
    /// <param name="prefix">The rejected prefix.</param>
    /// <param name="message">The required diagnostic.</param>
    /// <returns>The asynchronous verification.</returns>
    private async Task AssertInvalidPackagePrefixAsync(string prefix, string message)
    {
        string parent = CreateDirectory();
        string output = Path.Join(parent, "package");
        Directory.CreateDirectory(output);
        string marker = Path.Join(output, "keep.txt");
        await File.WriteAllTextAsync(marker, "preserved prefix rejection", context.CancellationToken);
        byte[] original = await File.ReadAllBytesAsync(Path.Join(s_published, "extension", "ankus_tool_probe.control"), context.CancellationToken);
        ProcessResult result = await InvokeAsync(["package", "--home", CreateDirectory(), "--from", s_published,
            "--output", output, "--prefix-dir", prefix], context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains(message, result.StandardError);
        Assert.IsEmpty(result.StandardOutput);
        Assert.AreEqual("preserved prefix rejection", await File.ReadAllTextAsync(marker, context.CancellationToken));
        Assert.HasCount(1, Directory.GetFiles(parent, "*", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(Path.Join(parent, "escaped prefix")));
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(Path.Join(s_published, "extension", "ankus_tool_probe.control"),
            context.CancellationToken));
    }

    /// <summary>
    /// Missing values, misplaced option tokens and install-only misuse fail before any filesystem writes.
    /// </summary>
    /// <param name="kind">The invalid invocation partition.</param>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("following-option")]
    [DataRow("install")]
    public async Task PackagePrefixRejectsInvalidCommandArguments(string kind)
    {
        string output = Path.Join(CreateDirectory(), "uncreated output");
        string home = CreateDirectory();
        string[] arguments = kind switch
        {
            "following-option" => ["package", "--from", s_published, "--output", output, "--prefix-dir", "--home", home],
            "install" => ["install", "--home", home, "--from", s_published, "--destdir", output, "--prefix-dir", "custom prefix"],
            _ => ["package", "--home", home, "--from", s_published, "--output", output, "--prefix-dir"],
        };
        ProcessResult result = await InvokeAsync(arguments, context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsNotEmpty(result.StandardError);
        Assert.Contains(kind == "following-option" ? home : "--prefix-dir", result.StandardError);
        Assert.IsFalse(Directory.Exists(output));
    }

    /// <summary>
    /// Copies the standard publication into a uniquely owned source directory.
    /// </summary>
    /// <param name="source">The owned publication directory.</param>
    /// <param name="token">Cancels file copies.</param>
    /// <returns>The unchanged published manifest.</returns>
    private static async Task<PublishedExtension> CopyPrefixPublicationAsync(string source, CancellationToken token)
    {
        PublishedExtension manifest = PublishedExtension.Read(s_published);
        manifest.Write(source);
        Directory.CreateDirectory(Path.Join(source, "extension"));
        string[] names = [manifest.Library, Path.Join("extension", manifest.Control), Path.Join("extension", manifest.Sql)];
        foreach (string name in names)
        {
            await File.WriteAllBytesAsync(Path.Join(source, name), await File.ReadAllBytesAsync(Path.Join(s_published, name), token), token);
        }

        return manifest;
    }
}
