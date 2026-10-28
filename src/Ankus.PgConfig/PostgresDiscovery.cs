using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Ankus.PgConfig;

/// <summary>
/// Enumerates configured, Ankus-managed, and conventional platform installation locations without environment setup.
/// </summary>
internal static class PostgresDiscovery
{
    /// <summary>
    /// Enumerates candidate pg_config executables for a PostgreSQL major version, in discovery order.
    /// </summary>
    /// <param name="major">The required PostgreSQL major version.</param>
    /// <param name="homeDirectory">The resolved home used for registered and managed installations.</param>
    /// <returns>Configured and conventional executable paths.</returns>
    internal static IEnumerable<string> GetCandidates(int major, string homeDirectory)
    {
        string version = major.ToString(CultureInfo.InvariantCulture);
        string fileName = OperatingSystem.IsWindows() ? "pg_config.exe" : "pg_config";
        string managed = Path.Join(homeDirectory, "postgres");
        if (Directory.Exists(managed))
        {
            foreach (string directory in Directory.EnumerateDirectories(managed).OrderDescending(StringComparer.Ordinal))
            {
                yield return Path.Join(directory, "bin", fileName);
            }
        }

        string? onPath = ExecutableLocator.Find("pg_config");
        if (onPath is not null)
        {
            yield return onPath;
        }

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PostgreSQL", version, "bin", fileName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return $"/opt/homebrew/opt/postgresql@{version}/bin/pg_config";
            yield return $"/usr/local/opt/postgresql@{version}/bin/pg_config";
            yield return $"/Applications/Postgres.app/Contents/Versions/{version}/bin/pg_config";
            yield return $"/Library/PostgreSQL/{version}/bin/pg_config";
        }
        else
        {
            yield return $"/usr/lib/postgresql/{version}/bin/pg_config";
            yield return $"/usr/pgsql-{version}/bin/pg_config";
            yield return "/usr/local/pgsql/bin/pg_config";
        }
    }
}
