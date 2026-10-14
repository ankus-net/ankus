using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Identical packaged helpers share verified sources after relocation and producer cleanup, while changed helper bytes invalidate them.
    /// </summary>
    [TestMethod]
    public async Task NativeSourceCacheSurvivesHelperRelocationAndProducerCleanup()
    {
        CancellationToken token = context.CancellationToken;
        ProcessResult evaluated = await RunDotnetAsync(["msbuild", s_project, "-verbosity:quiet", "-getProperty:_AnkusBuildTool"], token);
        evaluated.EnsureSuccess("dotnet", ["msbuild"]);
        string original = Path.GetDirectoryName(evaluated.StandardOutput.Trim())!;
        string root = PhysicalBindingDirectory(Directory.CreateTempSubdirectory("ac-"));
        try
        {
            string first = Path.Join(root, "first");
            string second = Path.Join(root, "second");
            foreach (string source in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(original, source);
                foreach (string destination in (new[] { first, second }).Select(directory => Path.Join(directory, relative)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                }
            }

            string cache = Path.Join(root, "cache");
            string sources = Path.Join(cache, "sources");
            string initial = await GenerateAsync("initial", first, reused: false);
            Dictionary<string, string> expected = await SourceArtifactHashesAsync(initial, token);
            Assert.HasCount(10, expected);
            string entry = Assert.ContainsSingle(Directory.GetDirectories(sources));
            string catalogs = Path.Join(cache, "catalogs");
            string catalogEntry = Assert.ContainsSingle(Directory.GetDirectories(catalogs));
            using (JsonDocument derived = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(catalogEntry, "native-catalogs.json"), token)))
            {
                // The build derives its declarations from the selected headers instead of reading pgrx's pinned output.
                Assert.AreEqual(s_installation.Version.Major, derived.RootElement.GetProperty("Types").GetProperty("PostgresMajor").GetInt32());
                Assert.IsGreaterThan(1000, derived.RootElement.GetProperty("Raw").GetProperty("Functions").EnumerateObject().Count());
                Assert.AreEqual(JsonValueKind.Null, derived.RootElement.GetProperty("Raw").GetProperty("SourceRevision").ValueKind);
            }

            string cachedSource = Path.Join(entry, "native-binding.g.cs");
            File.SetLastWriteTimeUtc(cachedSource, DateTime.UtcNow.AddDays(-1));
            DateTime timestamp = File.GetLastWriteTimeUtc(cachedSource);

            await AssertReuseAsync("relocated");
            Directory.Delete(first, recursive: true);
            await AssertReuseAsync("producer-removed");

            string changed = Path.Join(second, "Ankus.Build.dll");
            DateTime helperTimestamp = File.GetLastWriteTimeUtc(changed);
            await using (FileStream helper = new(changed, FileMode.Append, FileAccess.Write))
            {
                await helper.WriteAsync(new byte[] { 0 }, token);
            }

            File.SetLastWriteTimeUtc(changed, helperTimestamp);
            string rebuilt = await GenerateAsync("changed-content", second, reused: false);
            Assert.HasCount(2, Directory.GetDirectories(sources));
            Assert.HasCount(2, Directory.GetDirectories(catalogs), "A changed build tool derives its catalogs again.");
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(cachedSource));
            Assert.AreEquivalent(expected, await SourceArtifactHashesAsync(rebuilt, token));

            async Task AssertReuseAsync(string name)
            {
                string output = await GenerateAsync(name, second, reused: true);
                Assert.AreEqual(entry, Assert.ContainsSingle(Directory.GetDirectories(sources)));
                Assert.AreEqual(catalogEntry, Assert.ContainsSingle(Directory.GetDirectories(catalogs)));
                Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(cachedSource), "Reuse must preserve the original immutable artifact.");
                Assert.AreEquivalent(expected, await SourceArtifactHashesAsync(output, token));
            }

            async Task<string> GenerateAsync(string name, string directory, bool reused)
            {
                string output = Path.Join(root, name);
                string[] arguments = [Path.Join(directory, "Ankus.Build.dll"), "binding-sources", MajorText(),
                    s_installation.PgConfigPath, output, "", "", RuntimeInformation.RuntimeIdentifier, "", "", "", cache];
                ProcessResult result = await PackageProcessRunner.RunAsync("dotnet", arguments, s_environment, token, workingDirectory: root);
                result.EnsureSuccess("dotnet", ["binding-sources"]);
                Assert.Contains(reused ? "Native binding sources: reused after native verification."
                    : "Native binding sources: collected and verified.", result.StandardOutput);
                return output;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Observes every delivered binding artifact except the project containing its consumer-specific PathMap.
    /// </summary>
    /// <param name="directory">The helper's isolated output directory.</param>
    /// <param name="token">Cancels artifact reads.</param>
    /// <returns>Exact content identities by portable artifact name.</returns>
    private static async Task<Dictionary<string, string>> SourceArtifactHashesAsync(string directory, CancellationToken token)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (Path.GetFileName(file) == "Ankus.NativeBindings.csproj")
            {
                continue;
            }

            await using FileStream stream = File.OpenRead(file);
            hashes.Add(Path.GetFileName(file), Convert.ToHexString(await SHA256.HashDataAsync(stream, token)));
        }

        return hashes;
    }
}
