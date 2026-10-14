using System.Text;

namespace Ankus.Build;

/// <summary>
/// Generates the deterministic PostgreSQL header manifest used by pgrx 0.19.3.
/// </summary>
internal static class NativeBindingHeaderManifest
{
    private static readonly string[] s_headerDirectories =
    [
        "access",
        "catalog",
        "commands",
        "executor",
        "foreign",
        "nodes",
        "optimizer",
        "parser",
        "partitioning",
        "postmaster",
        "replication",
        "rewrite",
        "statistics",
        "storage",
        "tcop",
        "tsearch",
    ];

    private static readonly string[] s_headerFiles =
    [
        "common/config_info.h",
        "common/controldata_utils.h",
        "common/pg_lzcompress.h",
        "funcapi.h",
        "jit/jit.h",
        "lib/stringinfo.h",
        "libpq/pqformat.h",
        "mb/pg_wchar.h",
        "miscadmin.h",
        "pgstat.h",
        "plpgsql.h",
        "utils/acl.h",
        "utils/builtins.h",
        "utils/catcache.h",
        "utils/date.h",
        "utils/datetime.h",
        "utils/datum.h",
        "utils/elog.h",
        "utils/float.h",
        "utils/fmgroids.h",
        "utils/fmgrprotos.h",
        "utils/geo_decls.h",
        "utils/guc.h",
        "utils/guc_tables.h",
        "utils/json.h",
        "utils/jsonb.h",
        "utils/lsyscache.h",
        "utils/memutils.h",
        "utils/numeric.h",
        "utils/palloc.h",
        "utils/ps_status.h",
        "utils/rangetypes.h",
        "utils/regproc.h",
        "utils/rel.h",
        "utils/relcache.h",
        "utils/resowner.h",
        "utils/resowner_private.h",
        "utils/rls.h",
        "utils/ruleutils.h",
        "utils/sampling.h",
        "utils/selfuncs.h",
        "utils/snapmgr.h",
        "utils/sortsupport.h",
        "utils/spccache.h",
        "utils/syscache.h",
        "utils/tuplesort.h",
        "utils/tuplestore.h",
        "utils/typcache.h",
        "utils/varlena.h",
        "utils/wait_event.h",
        "varatt.h",
    ];

    private static readonly HashSet<string> s_excludedHeaders = new(StringComparer.Ordinal)
    {
        "access/rmgrlist.h",
        "catalog/syscache_ids.h",
        "catalog/syscache_info.h",
        "nodes/nodetags.h",
        "parser/gram.h",
        "parser/kwlist.h",
        "postmaster/proctypelist.h",
        "replication/backup_manifest.h",
        "storage/checksum_block_internal.h",
        "storage/lwlocklist.h",
        "storage/lwlocknames.h",
        "storage/subsystemlist.h",
        "tcop/cmdtaglist.h",
    };

    private const string Preamble =
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
        "#include \"pg_config.h\"\n\n";

    private const string Suffix =
        "\n" +
        "#if PG_VERSION_NUM < 140000\n" +
        "#ifndef WIN32\n" +
        "#define PGERROR ERROR\n" +
        "#endif\n" +
        "#endif\n";

    /// <summary>
    /// Generates an include manifest from an installed PostgreSQL server-header directory.
    /// </summary>
    /// <param name="serverIncludeDirectory">The directory reported by <c>pg_config --includedir-server</c>.</param>
    /// <returns>The exact UTF-8 text of the generated header manifest.</returns>
    internal static string Generate(string serverIncludeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverIncludeDirectory);
        string root = Path.GetFullPath(serverIncludeDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"PostgreSQL server header directory does not exist: {root}");
        }

        var headers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string path in s_headerDirectories.Select(directory => Path.Join(root, directory)))
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (string header in Directory.EnumerateFiles(path, "*.h", SearchOption.TopDirectoryOnly))
            {
                Add(headers, root, header);
            }
        }

        foreach (string header in s_headerFiles)
        {
            string path = Path.Join(root, header.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                Add(headers, root, path);
            }
        }

        var manifest = new StringBuilder(Preamble);
        foreach (string header in headers)
        {
            manifest.Append("#include \"").Append(header).Append("\"\n");
        }

        return manifest.Append(Suffix).ToString();
    }

    private static void Add(SortedSet<string> headers, string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        if (!s_excludedHeaders.Contains(relative) &&
            !(relative.StartsWith("catalog/pg_", StringComparison.Ordinal) &&
              relative.Length >= "catalog/pg_".Length + "_d.h".Length &&
              relative.EndsWith("_d.h", StringComparison.Ordinal)))
        {
            headers.Add(relative);
        }
    }
}
