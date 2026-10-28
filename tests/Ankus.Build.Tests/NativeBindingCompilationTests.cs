using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Exercises real isolated compiler processes, shared artifacts and consumer recovery.
/// </summary>
/// <param name="context">The test's cooperative cancellation context.</param>
[TestClass]
public sealed class NativeBindingCompilationTests(TestContext context)
{
    private static readonly string[] s_offlinePackages = ["microsoft.net.illink.tasks", "microsoft.netcore.app.ref", "microsoft.aspnetcore.app.ref"];
    private static readonly Guid s_embeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid s_typeDefinitionDocuments = new("932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");

    private const string AssemblyName = "Ankus.Postgres.CacheProbe";
    private const string Source = """
        /// <summary>
        /// An executable witness for the compiled input bytes.
        /// </summary>
        public static class Probe
        {
            /// <summary>
            /// Returns the selected source value.
            /// </summary>
            public static int Read() => 41;
        }
        """;

    /// <summary>
    /// Concurrent processes compile once, survive the producer's cleanup and preserve unchanged local timestamps.
    /// </summary>
    /// <param name="separatePackages">Whether consumers restore into different package directories.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompiledCompanionsShareAcrossProcessesAndConsumerCleanup(bool separatePackages)
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiled-sharing-").FullName;
        try
        {
            string[] settings = await SettingsAsync(root);
            string first = await SourceAsync(root, "first consumer");
            string second = await SourceAsync(root, "second consumer");
            string[] firstArguments = Arguments(root, first, settings);
            string[] secondSettings = separatePackages
                ? await SettingsAsync(Directory.CreateDirectory(Path.Join(root, "other restore")).FullName,
                    ["-property:RestorePackagesPath=" + Path.Join(root, "other packages")])
                : settings;
            string[] secondArguments = Arguments(root, second, secondSettings);
            secondArguments[3] = "ConsumerSpecificConfiguration";
            string tool = typeof(NativeBindingCompilationCommand).Assembly.Location;
            string program = "dotnet";
            string[] prefix = [];
            if (!OperatingSystem.IsWindows())
            {
                string physical = Directory.CreateDirectory(Path.Join(root, "temporary")).FullName;
                string alias = Path.Join(root, "linked temporary");
                Directory.CreateSymbolicLink(alias, physical);
                program = "/usr/bin/env";
                prefix = ["TMPDIR=" + alias, "dotnet"];
            }

            string[] results = await Task.WhenAll(
                NativeBindingLayoutCommand.RunProcessAsync(program, [.. prefix, tool, "binding-compile", .. firstArguments], root, context.CancellationToken),
                NativeBindingLayoutCommand.RunProcessAsync(program, [.. prefix, tool, "binding-compile", .. secondArguments], root, context.CancellationToken));
            Assert.ContainsSingle(results.Where(static result => result.Contains("Managed binding compilation: built ", StringComparison.Ordinal)));
            Assert.ContainsSingle(results.Where(static result => result.Contains("Managed binding compilation: reused ", StringComparison.Ordinal)));
            Assert.HasCount(separatePackages ? 2 : 1,
                results.Where(static result => result.Contains("Managed binding compilation: preparing compiler inputs", StringComparison.Ordinal)));
            string artifact = Artifact(second);
            Assert.AreEqual(41, Execute(artifact));
            Assert.AreEqual(await NativeBindingCache.HashAsync(Artifact(first), context.CancellationToken),
                await NativeBindingCache.HashAsync(artifact, context.CancellationToken));
            Directory.Delete(first, recursive: true);
            DateTime timestamp = DateTime.UtcNow.AddDays(-1);
            File.SetLastWriteTimeUtc(artifact, timestamp);
            timestamp = File.GetLastWriteTimeUtc(artifact);
            string reused = await NativeBindingLayoutCommand.RunProcessAsync(program, [.. prefix, tool, "binding-compile", .. secondArguments], root, context.CancellationToken);
            Assert.Contains("Managed binding compilation: reused ", reused);
            Assert.DoesNotContain("Managed binding compilation: preparing compiler inputs", reused);
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(artifact));
            Assert.AreEqual(41, Execute(artifact));
            string cache = Path.Join(root, "cache");
            Assert.HasCount(separatePackages ? 2 : 1, RequestEntries(cache));
            Assert.HasCount(1, RequestEntries(Path.Join(cache, "compilations")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Package targets that change compiler semantics invalidate both cache levels.
    /// </summary>
    [TestMethod]
    public async Task CompiledCompanionsInvalidatePackageCompilerSettings()
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiled-options-").FullName;
        try
        {
            string packages = Path.Join(root, "packages");
            string[] settings = await SettingsAsync(root, ["-property:RestorePackagesPath=" + packages]);
            string source = await SourceAsync(root, "consumer");
            await File.WriteAllTextAsync(Path.Join(source, "native-binding.g.cs"), Source.Replace(
                "public static int Read() => 41;", """
                public static int Read() => Add(int.MaxValue, 1);

                    /// <summary>
                    /// Preserves runtime arithmetic so overflow policy affects observable behavior.
                    /// </summary>
                    private static int Add(int left, int right) => left + right;
                """, StringComparison.Ordinal), context.CancellationToken);
            string[] arguments = Arguments(root, source, settings);
            await NativeBindingCompilationCommand.RunAsync(arguments, context.CancellationToken);
            Assert.AreEqual(int.MinValue, Execute(Artifact(source)));

            string package = Assert.ContainsSingle(Directory.GetDirectories(Path.Join(packages, "microsoft.net.illink.tasks")));
            string target = Path.Join(package, "build", "Microsoft.NET.ILLink.targets");
            XDocument project = XDocument.Load(target);
            XNamespace ns = project.Root!.Name.Namespace;
            project.Root.Add(new XElement(ns + "PropertyGroup", new XElement(ns + "CheckForOverflowUnderflow", "true")));
            project.Save(target);

            await NativeBindingCompilationCommand.RunAsync(arguments, context.CancellationToken);
            TargetInvocationException error = Assert.ThrowsExactly<TargetInvocationException>(() => Execute(Artifact(source)));
            Assert.IsInstanceOfType<OverflowException>(error.InnerException);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The companion's PDB embeds one small document per declaration, so Go to Definition opens the declaration's own
    /// source. A member maps to its document line by line, and a type without methods names its own document.
    /// </summary>
    [TestMethod]
    public async Task CompiledCompanionsEmbedOneDocumentPerDeclaration()
    {
        const string Documented = """
            // <auto-generated/>
            #nullable enable

            // ankus-document: Probe.g.cs
            /// <summary>
            /// An executable witness for the documents the companion embeds.
            /// </summary>
            public static partial class Probe
            {
            // ankus-member: Probe.Read.g.cs
                /// <summary>
                /// Returns the selected source value.
                /// </summary>
                public static int Read()
                {
                    return (int)Mode.Selected + 41;
                }

            // ankus-end
            }

            // ankus-document: Mode.g.cs
            /// <summary>
            /// A type whose only document comes from its declaration.
            /// </summary>
            public enum Mode
            {
                /// <summary>
                /// The selected value.
                /// </summary>
                Selected,
            }

            """;
        string root = Directory.CreateTempSubdirectory("ankus-compiled-documents-").FullName;
        try
        {
            string[] settings = await SettingsAsync(root);
            string source = await SourceAsync(root, "consumer");
            await File.WriteAllTextAsync(Path.Join(source, "native-binding.g.cs"), Documented, context.CancellationToken);
            await NativeBindingCompilationCommand.RunAsync(Arguments(root, source, settings), context.CancellationToken);
            string artifact = Artifact(source);
            Assert.AreEqual(41, Execute(artifact));

            using PEReader image = new(File.OpenRead(artifact));
            MetadataReader metadata = image.GetMetadataReader();
            using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(artifact, ".pdb")));
            MetadataReader pdb = provider.GetMetadataReader();
            NativeBindingDocumentLayout layout = NativeBindingDocuments.Split(Documented);
            Dictionary<string, string?> documents = pdb.Documents.ToDictionary(handle => pdb.GetString(pdb.GetDocument(handle).Name),
                handle => EmbeddedText(pdb, handle), StringComparer.Ordinal);
            Assert.AreEquivalent(layout.Embedded.ToDictionary(static document => "/_/Ankus.Postgres/" + document.Path, static document => (string?)document.Text),
                documents.Where(static pair => pair.Value is not null).ToDictionary());
            Assert.IsNull(documents["/_/Ankus.Postgres/members/Probe.g.cs"]);

            MethodDefinitionHandle read = metadata.MethodDefinitions.Single(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name) == "Read");
            SequencePoint first = pdb.GetMethodDebugInformation(read).GetSequencePoints().First(static point => !point.IsHidden);
            Assert.AreEqual("/_/Ankus.Postgres/documents/Probe.Read.g.cs", pdb.GetString(pdb.GetDocument(first.Document).Name));
            Assert.AreEqual("        return (int)Mode.Selected + 41;",
                documents["/_/Ankus.Postgres/documents/Probe.Read.g.cs"]!.Split('\n')[first.StartLine - 1]);

            TypeDefinitionHandle mode = metadata.TypeDefinitions.Single(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name) == "Mode");
            CustomDebugInformation typeDocuments = pdb.GetCustomDebugInformation(pdb.GetCustomDebugInformation(mode)
                .Single(handle => pdb.GetGuid(pdb.GetCustomDebugInformation(handle).Kind) == s_typeDefinitionDocuments));
            BlobReader rows = pdb.GetBlobReader(typeDocuments.Value);
            Assert.AreEqual("/_/Ankus.Postgres/documents/Mode.g.cs",
                pdb.GetString(pdb.GetDocument(MetadataTokens.DocumentHandle(rows.ReadCompressedInteger())).Name));
            Assert.AreEqual(0, rows.RemainingBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Identical runtime and helper copies reuse immutable companions after their original producer is removed.
    /// </summary>
    [TestMethod]
    public async Task CompiledCompanionsReuseRelocatedRuntimeAndHelper()
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiled-relocation-").FullName;
        try
        {
            string[] settings = await SettingsAsync(root);
            string originalRuntime = Path.Join(root, "Ankus.Runtime.dll");
            string relocatedRuntime = Path.Join(Directory.CreateDirectory(Path.Join(root, "moved runtime")).FullName, "Ankus.Runtime.dll");
            File.Copy(originalRuntime, relocatedRuntime);
            string originalHelper = Path.Join(root, "first helper");
            string relocatedHelper = Path.Join(root, "second helper");
            string tools = Path.GetDirectoryName(typeof(NativeBindingCompilationCommand).Assembly.Location)!;
            foreach (string file in Directory.EnumerateFiles(tools, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(tools, file);
                foreach (string destination in (new[] { originalHelper, relocatedHelper }).Select(directory => Path.Join(directory, relative)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
            }

            string first = await CompileAsync("initial consumer", originalHelper, originalRuntime, reused: false);
            Dictionary<string, string> expected = await ArtifactHashesAsync(first);
            Assert.HasCount(3, expected);
            string cache = Path.Join(root, "cache");
            string entry = Assert.ContainsSingle(RequestEntries(cache));
            string cachedAssembly = Path.Join(entry, AssemblyName + ".dll");
            File.SetLastWriteTimeUtc(cachedAssembly, DateTime.UtcNow.AddDays(-1));
            DateTime timestamp = File.GetLastWriteTimeUtc(cachedAssembly);

            await AssertReuseAsync("runtime relocation", originalHelper);
            await AssertReuseAsync("helper relocation", relocatedHelper);
            Directory.Delete(originalHelper, recursive: true);
            File.Delete(originalRuntime);
            Directory.Delete(first, recursive: true);
            await AssertReuseAsync("producer removed", relocatedHelper);

            string changedHelper = Path.Join(relocatedHelper, "Ankus.Build.dll");
            DateTime helperTimestamp = File.GetLastWriteTimeUtc(changedHelper);
            await using (FileStream helper = new(changedHelper, FileMode.Append, FileAccess.Write))
            {
                await helper.WriteAsync(new byte[] { 0 }, context.CancellationToken);
            }

            File.SetLastWriteTimeUtc(changedHelper, helperTimestamp);
            string changed = await CompileAsync("changed helper", relocatedHelper, relocatedRuntime, reused: false);
            Assert.HasCount(2, RequestEntries(cache));
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(cachedAssembly));
            Assert.AreEquivalent(expected, await ArtifactHashesAsync(changed));

            async Task AssertReuseAsync(string name, string helper)
            {
                string source = await CompileAsync(name, helper, relocatedRuntime, reused: true);
                Assert.AreEqual(entry, Assert.ContainsSingle(RequestEntries(cache)));
                Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(cachedAssembly));
                Assert.AreEquivalent(expected, await ArtifactHashesAsync(source));
            }

            async Task<string> CompileAsync(string name, string helper, string runtime, bool reused)
            {
                string source = await SourceAsync(root, name);
                string[] arguments = [source, runtime, "net10.0", "Release", .. settings, Path.Join(root, "cache")];
                string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                    [Path.Join(helper, "Ankus.Build.dll"), "binding-compile", .. arguments], root, context.CancellationToken);
                Assert.Contains(reused ? "Managed binding compilation: reused " : "Managed binding compilation: built ", output);
                Assert.AreEqual(41, Execute(Artifact(source)));
                return source;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Changed source and runtime bytes rebuild; compilation failure preserves the consumer's last usable result.
    /// </summary>
    [TestMethod]
    public async Task CompiledCompanionsInvalidateContentAndRecoverFromCompilerFailure()
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiled-recovery-").FullName;
        try
        {
            string[] settings = await SettingsAsync(root);
            string source = await SourceAsync(root, "consumer");
            string[] arguments = Arguments(root, source, settings);
            await NativeBindingCompilationCommand.RunAsync(arguments, context.CancellationToken);
            string artifact = Artifact(source);
            Assert.AreEqual(41, Execute(artifact));
            string original = await NativeBindingCache.HashAsync(artifact, context.CancellationToken);
            string input = Path.Join(source, "native-binding.g.cs");
            await File.WriteAllTextAsync(input, "This source does not compile.", context.CancellationToken);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => NativeBindingCompilationCommand.RunAsync(arguments, context.CancellationToken));
            Assert.AreEqual(original, await NativeBindingCache.HashAsync(artifact, context.CancellationToken));
            Assert.AreEqual(41, Execute(artifact));
            Assert.DoesNotContain(static path => Path.GetFileName(path).Contains(".stage-", StringComparison.Ordinal),
                Directory.GetDirectories(Path.Join(root, "cache"), "*", SearchOption.AllDirectories));
            await File.WriteAllTextAsync(input, Source.Replace("41", "43", StringComparison.Ordinal), context.CancellationToken);
            await NativeBindingCompilationCommand.RunAsync(arguments, context.CancellationToken);
            Assert.AreEqual(43, Execute(artifact));
            Assert.AreNotEqual(original, await NativeBindingCache.HashAsync(artifact, context.CancellationToken));
            int builds = Directory.GetFiles(source, "binding-compile-*.binlog").Length;
            DateTime timestamp = File.GetLastWriteTimeUtc(arguments[1]);
            await using (FileStream runtime = new(arguments[1], FileMode.Append, FileAccess.Write))
            {
                await runtime.WriteAsync(new byte[] { 0 }, context.CancellationToken);
            }

            File.SetLastWriteTimeUtc(arguments[1], timestamp);
            await NativeBindingCompilationCommand.RunAsync(arguments, context.CancellationToken);
            Assert.HasCount(builds + 1, Directory.GetFiles(source, "binding-compile-*.binlog"));
            Assert.AreEqual(43, Execute(artifact));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A fresh package cache uses the consumer's explicit offline configuration and enforces its source mapping.
    /// </summary>
    /// <param name="useSourceOverride">Whether the consumer limits the configured sources through an MSBuild override.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompiledCompanionsHonorOfflineFeedConfigurationAndSourceMapping(bool useSourceOverride)
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiled-offline-").FullName;
        try
        {
            string[] settings = await SettingsAsync(root);
            NativeBindingRestoreSettings initial = await NativeBindingRestoreSettings.ReadAsync(settings[2], context.CancellationToken);
            string configurationDirectory = Directory.CreateDirectory(Path.Join(root, "feed settings")).FullName;
            string feed = Directory.CreateDirectory(Path.Join(configurationDirectory, "feed")).FullName;
            string[] packages = [.. s_offlinePackages
                .Select(package => Path.Join(initial.Packages, package)).Where(Directory.Exists)
                .SelectMany(static directory => Directory.GetFiles(directory, "*.nupkg", SearchOption.AllDirectories))];
            Assert.IsNotEmpty(packages);
            foreach (string package in packages)
            {
                File.Copy(package, Path.Join(feed, Path.GetFileName(package)));
            }

            var configuration = XDocument.Parse("""
                <configuration>
                  <packageSources><clear /><add key="offline" value="feed" /></packageSources>
                  <packageSourceMapping>
                    <clear />
                    <packageSource key="offline">
                      <package pattern="Microsoft.*" />
                      <package pattern="Microsoft.NETCore.App.Ref" />
                      <package pattern="Microsoft.AspNetCore.App.Ref" />
                    </packageSource>
                  </packageSourceMapping>
                </configuration>
                """);
            string configurationPath = Path.Join(configurationDirectory, "explicit.config");
            string[] restoreArguments = ["-property:RestoreConfigFile=" + configurationPath];
            if (useSourceOverride)
            {
                configuration.Root!.Element("packageSources")!.Add(new XElement("add", new XAttribute("key", "unavailable"),
                    new XAttribute("value", "missing-feed")));
                restoreArguments = [.. restoreArguments, "-property:RestoreSources=" + feed];
            }

            await File.WriteAllTextAsync(configurationPath, configuration.ToString(), context.CancellationToken);
            // An inherited NUGET_PACKAGES overrides NuGet.Config's globalPackagesFolder.
            // An explicit MSBuild property keeps this consumer's cache isolated on dedicated runners too.
            string packagesDirectory = Path.Join(configurationDirectory, "packages");
            settings = await SettingsAsync(root, [.. restoreArguments, "-property:RestorePackagesPath=" + packagesDirectory]);
            NativeBindingRestoreSettings resolved = await NativeBindingRestoreSettings.ReadAsync(settings[2], context.CancellationToken);
            Assert.AreEqual(packagesDirectory, resolved.Packages);
            Assert.Contains(feed, resolved.Sources);
            Assert.DoesNotContain(static source => !Path.IsPathFullyQualified(source), resolved.Sources);
            Assert.DoesNotContain(Path.Join(configurationDirectory, "missing-feed"), resolved.Sources);
            Assert.AreSequenceEqual([configurationPath], resolved.Configurations);
            string source = await SourceAsync(root, "consumer");
            string[] arguments = Arguments(root, source, settings);
            await NativeBindingCompilationCommand.RunAsync(arguments, context.CancellationToken);
            Assert.AreEqual(41, Execute(Artifact(source)));
            Assert.IsNotEmpty(Directory.GetFiles(resolved.Packages, "*.nupkg", SearchOption.AllDirectories));

            // A warm compiled artifact must not bypass a changed restore policy with a new package cache.
            configuration.Root!.Element("packageSourceMapping")!.Element("packageSource")!.Element("package")!.SetAttributeValue("pattern", "Unrelated.*");
            await File.WriteAllTextAsync(configurationPath, configuration.ToString(), context.CancellationToken);
            string deniedDirectory = Path.Join(configurationDirectory, "denied-packages");
            settings = await SettingsAsync(root, [.. restoreArguments, "-property:RestorePackagesPath=" + deniedDirectory]);
            NativeBindingRestoreSettings denied = await NativeBindingRestoreSettings.ReadAsync(settings[2], context.CancellationToken);
            Assert.AreEqual(deniedDirectory, denied.Packages);
            string tool = typeof(NativeBindingCompilationCommand).Assembly.Location;
            InvalidOperationException rejected = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingLayoutCommand.RunProcessAsync("dotnet", [tool, "binding-compile", .. Arguments(root, source, settings)], root, context.CancellationToken));
            Assert.Contains("NU1100", rejected.Message);
            Assert.Contains("PackageSourceMapping", rejected.Message);
            Assert.AreEqual(41, Execute(Artifact(source)));

            configuration.Root.Element("packageSourceMapping")!.Element("packageSource")!.Element("package")!.SetAttributeValue("pattern", "Microsoft.*");
            await File.WriteAllTextAsync(configurationPath, configuration.ToString(), context.CancellationToken);
            await NativeBindingCompilationCommand.RunAsync(Arguments(root, source, settings), context.CancellationToken);
            Assert.AreEqual(41, Execute(Artifact(source)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<string[]> SettingsAsync(string root, string[]? restoreArguments = null)
    {
        await TestDotnetSdk.ConfigureAsync(root, context.CancellationToken);
        string project = Path.Join(root, "Settings.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", context.CancellationToken);
        string properties = await NativeBindingLayoutCommand.RunProcessAsync("dotnet", ["msbuild", project, "-nologo",
            "-restore", "-target:ResolveReferences", "-verbosity:quiet", "-getProperty:NETCoreSdkVersion,MSBuildToolsPath,ProjectAssetsFile", .. restoreArguments ?? []], root, context.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(properties);
        JsonElement values = document.RootElement.GetProperty("Properties");
        Assert.AreEqual(TestDotnetSdk.Version, values.GetProperty("NETCoreSdkVersion").GetString());
        File.Copy(typeof(PgDatum).Assembly.Location, Path.Join(root, "Ankus.Runtime.dll"), overwrite: true);
        return [values.GetProperty("NETCoreSdkVersion").GetString()!, values.GetProperty("MSBuildToolsPath").GetString()!, values.GetProperty("ProjectAssetsFile").GetString()!];
    }

    private async Task<string> SourceAsync(string root, string name)
    {
        string directory = Path.Join(root, name);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Join(directory, "native-binding.assembly-name"), AssemblyName, context.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(directory, "native-binding.g.cs"), Source, context.CancellationToken);
        return directory;
    }

    private static string[] Arguments(string root, string source, string[] settings)
        => [source, Path.Join(root, "Ankus.Runtime.dll"), "net10.0", "Release", .. settings, Path.Join(root, "cache")];

    private static string Artifact(string source) => Path.Join(source, "compiled", AssemblyName + ".dll");

    /// <summary>
    /// Selects immutable entries without treating the shared compilation store as a request.
    /// </summary>
    private static IEnumerable<string> RequestEntries(string cache)
        => Directory.GetDirectories(cache).Where(static path => Path.GetFileName(path).Length == 64);

    /// <summary>
    /// Observes every delivered assembly, symbol and documentation artifact by content.
    /// </summary>
    /// <param name="source">The consumer's generated source directory.</param>
    /// <returns>Artifact names and exact content hashes.</returns>
    private async Task<Dictionary<string, string>> ArtifactHashesAsync(string source)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Join(source, "compiled")))
        {
            hashes.Add(Path.GetFileName(file), await NativeBindingCache.HashAsync(file, context.CancellationToken));
        }

        return hashes;
    }

    /// <summary>
    /// Reads a document's source from the PDB, or null when the PDB does not embed it.
    /// </summary>
    private static string? EmbeddedText(MetadataReader pdb, DocumentHandle document)
    {
        foreach (CustomDebugInformationHandle handle in pdb.GetCustomDebugInformation(document))
        {
            CustomDebugInformation information = pdb.GetCustomDebugInformation(handle);
            if (pdb.GetGuid(information.Kind) != s_embeddedSource)
            {
                continue;
            }

            // The blob starts with the uncompressed size, or zero when the text follows uncompressed.
            byte[] blob = pdb.GetBlobBytes(information.Value);
            int size = BitConverter.ToInt32(blob, 0);
            if (size == 0)
            {
                return Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
            }

            using var deflate = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            string text = reader.ReadToEnd();
            Assert.AreEqual(size, Encoding.UTF8.GetByteCount(text));
            return text;
        }

        return null;
    }

    private static int Execute(string artifact)
    {
        var loader = new AssemblyLoadContext("binding-compile-test", isCollectible: true);
        try
        {
            using FileStream stream = File.OpenRead(artifact);
            Assembly assembly = loader.LoadFromStream(stream);
            return (int)assembly.GetType("Probe", throwOnError: true)!.GetMethod("Read")!.Invoke(null, null)!;
        }
        finally
        {
            loader.Unload();
        }
    }
}
