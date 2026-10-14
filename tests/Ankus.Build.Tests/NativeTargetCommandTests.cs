using System.Text.Json.Nodes;
using Ankus.PgConfig;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies the dependency rule reader and the checks that guard imported target bindings.
/// </summary>
[TestClass]
public sealed class NativeTargetCommandTests(TestContext context)
{
    /// <summary>
    /// Escaped spaces, escaped number signs, doubled dollars and continuations are read as make writes them.
    /// </summary>
    [TestMethod]
    public void DependencyRulesKeepEscapedPaths()
    {
        const string Rule = "native-target-input.o: /tmp/native-target-input.c \\\n  /opt/pg/include/server/postgres.h \\\n" +
            "  /opt/with\\ space/a.h /opt/hash\\#/b.h /opt/dollar$$/c.h\n";
        Assert.AreSequenceEqual(
        [
            "native-target-input.o", "/tmp/native-target-input.c", "/opt/pg/include/server/postgres.h", "/opt/with space/a.h",
            "/opt/hash#/b.h", "/opt/dollar$/c.h",
        ], NativeTargetCommand.ParseDependencies(Rule));
        Assert.AreSequenceEqual(["a.o", "a.c"], NativeTargetCommand.ParseDependencies("a.o: a.c\r\n"));
    }

    /// <summary>
    /// Imports reject information for another major, runtime or Ankus version before copying any binding.
    /// </summary>
    [TestMethod]
    public async Task ImportRejectsAnotherMajorRuntimeOrVersion()
    {
        CancellationToken token = context.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("ankus-target-import-").FullName;
        try
        {
            string root = Path.Join(directory, "target");
            Directory.CreateDirectory(root);
            var manifest = new JsonObject
            {
                ["format"] = 1,
                ["ankusVersion"] = "0.0.0-other",
                ["runtimeIdentifier"] = "linux-x64",
                ["targetTriple"] = "",
                ["postgresMajor"] = 18,
                ["postgres"] = new JsonObject
                {
                    ["version"] = "PostgreSQL 18.6",
                    ["bindir"] = Rooted("pg", "bin"),
                    ["pkglibdir"] = Rooted("pg", "lib"),
                    ["sharedir"] = Rooted("pg", "share"),
                    ["includedir"] = Rooted("pg", "include"),
                    ["includedirServer"] = Rooted("pg", "include", "server"),
                    ["cppflags"] = "",
                },
                ["files"] = new JsonObject(),
            };
            await File.WriteAllTextAsync(Path.Join(root, PostgresTargetInfo.ManifestFileName), manifest.ToJsonString(), token);
            await File.WriteAllTextAsync(Path.Join(root, "postgres-major"), "18\n", token);
            await File.WriteAllTextAsync(Path.Join(root, "runtime-identifier"), "linux-x64\n", token);
            PostgresTargetInfo target = PostgresTargetInfo.Open(root);
            string output = Path.Join(directory, "output");
            InvalidOperationException major = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => NativeTargetCommand.ImportAsync(target, 17, "linux-x64", output, token));
            Assert.Contains("describes PostgreSQL 18, but this build selects 17", major.Message);
            InvalidOperationException runtime = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => NativeTargetCommand.ImportAsync(target, 18, "osx-arm64", output, token));
            Assert.Contains("describes linux-x64, but this build targets osx-arm64", runtime.Message);
            InvalidOperationException version = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => NativeTargetCommand.ImportAsync(target, 18, "linux-x64", output, token));
            Assert.Contains("exported by Ankus 0.0.0-other, but this build uses " + NativeTargetCommand.Version, version.Message);
            Assert.IsFalse(Directory.Exists(output));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Rooted(params string[] segments)
        => Path.Join([OperatingSystem.IsWindows() ? @"C:\" : "/", .. segments]);

}
