namespace Ankus.Build.Tests;

/// <summary>
/// Verifies deterministic pgrx-compatible header-manifest generation.
/// </summary>
/// <param name="context">The test run context used to own generated fixture files.</param>
[TestClass]
public sealed class NativeBindingHeaderManifestTests(TestContext context)
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Join(context.TestRunDirectory ?? throw new InvalidOperationException("The test run directory is unavailable."),
            $"header-manifest-{Guid.NewGuid():N}")).FullName;

    /// <summary>
    /// Removes the owned synthetic PostgreSQL header tree.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Supported headers are sorted and generated PostgreSQL inputs are excluded exactly.
    /// </summary>
    [TestMethod]
    public void GenerateMatchesPgrxHeaderDiscoveryAndFormatting()
    {
        Add("access/zeta.h");
        Add("access/alpha.h");
        Add("access/rmgrlist.h");
        Add("catalog/pg_class.h");
        Add("catalog/pg_class_d.h");
        Add("catalog/pg__d.h");
        Add("catalog/pg_d.h");
        Add("catalog/syscache_ids.h");
        Add("catalog/syscache_info.h");
        Add("common/config_info.h");
        Add("common/controldata_utils.h");
        Add("common/ignored.h");
        Add("common/pg_lzcompress.h");
        Add("commands/command.h");
        Add("nodes/nodes.h");
        Add("nodes/nodetags.h");
        Add("parser/gram.h");
        Add("parser/kwlist.h");
        Add("postmaster/proctypelist.h");
        Add("replication/backup_manifest.h");
        Add("storage/checksum_block_internal.h");
        Add("storage/lwlocklist.h");
        Add("storage/lwlocknames.h");
        Add("storage/subsystemlist.h");
        Add("tcop/cmdtaglist.h");
        Add("utils/elog.h");
        Add("utils/ignored.h");
        Add("varatt.h");
        Add("unsupported/ignored.h");

        string manifest = NativeBindingHeaderManifest.Generate(_root);
        const string Expected =
            "//LICENSE Portions Copyright 2019-2021 ZomboDB, LLC.\n" +
            "//LICENSE\n" +
            "//LICENSE Portions Copyright 2021-2023 Technology Concepts & Design, Inc.\n" +
            "//LICENSE\n" +
            "//LICENSE Portions Copyright 2023-2023 PgCentral Foundation, Inc. <contact@pgcentral.org>\n" +
            "//LICENSE\n" +
            "//LICENSE All rights reserved.\n" +
            "//LICENSE\n" +
            "//LICENSE Use of this source code is governed by the MIT license that can be found in the LICENSE file.\n" +
            "\n" +
            "#include \"postgres.h\"\n" +
            "#include \"pg_config.h\"\n" +
            "\n" +
            "#include \"access/alpha.h\"\n" +
            "#include \"access/zeta.h\"\n" +
            "#include \"catalog/pg_class.h\"\n" +
            "#include \"catalog/pg_d.h\"\n" +
            "#include \"commands/command.h\"\n" +
            "#include \"common/config_info.h\"\n" +
            "#include \"common/controldata_utils.h\"\n" +
            "#include \"common/pg_lzcompress.h\"\n" +
            "#include \"nodes/nodes.h\"\n" +
            "#include \"utils/elog.h\"\n" +
            "#include \"varatt.h\"\n" +
            "\n" +
            "#if PG_VERSION_NUM < 140000\n" +
            "#ifndef WIN32\n" +
            "#define PGERROR ERROR\n" +
            "#endif\n" +
            "#endif\n";
        Assert.AreEqual(Expected, manifest);
    }

    /// <summary>
    /// Missing server-header roots fail before producing an incomplete manifest.
    /// </summary>
    [TestMethod]
    public void MissingServerHeaderDirectoryFailsExplicitly()
    {
        string missing = Path.Join(_root, "missing");
        DirectoryNotFoundException failure = Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            NativeBindingHeaderManifest.Generate(missing));
        Assert.Contains(Path.GetFullPath(missing), failure.Message, StringComparison.Ordinal);
    }

    private void Add(string relativePath)
    {
        string path = Path.Join(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }
}
