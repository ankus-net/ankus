using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Ankus.PgConfig;

namespace Ankus.Testing;

/// <summary>
/// Reads the child-only selections supplied by ankus test without changing direct fixture defaults.
/// </summary>
internal static class TestCommandContext
{
    /// <summary>
    /// Gets the literal effective MSBuild properties selected by ankus test.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildProperties
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("ANKUS_TEST_MSBUILD_PROPERTIES_FILE");
            if (path is null)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            string? session = SessionDirectory;
            string expected = session is null ? string.Empty : Path.GetFullPath(Path.Join(session, "msbuild-properties"));
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || !string.Equals(Path.GetFullPath(path), expected,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "ANKUS_TEST_MSBUILD_PROPERTIES_FILE must be the existing property file in the command session.");
            }

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator <= 0)
                {
                    throw new InvalidOperationException("ANKUS_TEST_MSBUILD_PROPERTIES_FILE contains an invalid property.");
                }

                string name = Uri.UnescapeDataString(line[..separator]);
                string value = Uri.UnescapeDataString(line[(separator + 1)..]);
                try
                {
                    System.Xml.XmlConvert.VerifyNCName(name);
                }
                catch (System.Xml.XmlException error)
                {
                    throw new InvalidOperationException(
                        "ANKUS_TEST_MSBUILD_PROPERTIES_FILE contains an invalid property name.", error);
                }

                properties[name] = value;
            }

            return properties;
        }
    }

    /// <summary>
    /// Gets the requested publication configuration, or Release for a direct fixture invocation.
    /// </summary>
    internal static string Configuration => Environment.GetEnvironmentVariable("ANKUS_TEST_CONFIGURATION") ?? "Release";

    /// <summary>
    /// Gets the command's schema-reuse default, rejecting malformed context before publication.
    /// </summary>
    internal static bool ReuseSchema
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("ANKUS_TEST_REUSE_SCHEMA");
            if (value is null)
            {
                return false;
            }

            return bool.TryParse(value, out bool reuse) ? reuse :
                throw new InvalidOperationException("ANKUS_TEST_REUSE_SCHEMA must be true or false.");
        }
    }

    /// <summary>
    /// Gets the Unix account selected with ankus test --runas, or null for the account running the tests.
    /// </summary>
    internal static string? RunAs
    {
        get
        {
            string? account = Environment.GetEnvironmentVariable("ANKUS_TEST_RUNAS");
            if (account is not null && SessionDirectory is null)
            {
                throw new InvalidOperationException("ANKUS_TEST_RUNAS requires an ankus test command session.");
            }

            return account;
        }
    }

    /// <summary>
    /// Gets whether ankus test --valgrind, or ANKUS_TEST_VALGRIND=true, runs every test server under Valgrind.
    /// </summary>
    internal static bool UseValgrind
        => Environment.GetEnvironmentVariable("ANKUS_TEST_VALGRIND") switch
        {
            null or "" or "false" => false,
            "true" => true,
            _ => throw new InvalidOperationException("ANKUS_TEST_VALGRIND must be true or false."),
        };

    /// <summary>
    /// Gets the command-owned temporary root, or null outside ankus test.
    /// </summary>
    internal static string? SessionDirectory
        => ReadDirectory("ANKUS_TEST_SESSION_DIRECTORY");

    /// <summary>
    /// Gets the command-owned data root while retaining the separate temporary socket and publication root.
    /// </summary>
    internal static string? DataDirectory
    {
        get
        {
            string? session = SessionDirectory;
            string? data = ReadDirectory("ANKUS_TEST_DATA_DIRECTORY");
            if (data is not null && session is null)
            {
                throw new InvalidOperationException("The ankus test data directory requires an existing command session.");
            }

            return data ?? (session is null ? null : Path.Join(session, "pgdata"));
        }
    }

    private static string? ReadDirectory(string variable)
    {
        string? directory = Environment.GetEnvironmentVariable(variable);
        if (directory is not null && (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)))
        {
            throw new InvalidOperationException($"{variable} must be an existing absolute directory.");
        }

        return directory;
    }

    /// <summary>
    /// Rejects fixtures that would silently execute a different PostgreSQL target within a selected test run.
    /// </summary>
    /// <param name="installation">The installation the fixture intends to use.</param>
    internal static void ValidateInstallation(PostgresInstallation installation)
    {
        string? value = Environment.GetEnvironmentVariable("ANKUS_TEST_POSTGRES_MAJOR");
        if (value is null)
        {
            return;
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) || major is < 13 or > 19)
        {
            throw new InvalidOperationException("ANKUS_TEST_POSTGRES_MAJOR must select PostgreSQL 13–19.");
        }

        if (installation.Version.Major != major)
        {
            throw new InvalidOperationException($"ankus test selected PostgreSQL {major}, but the fixture selected PostgreSQL {installation.Version.Major}.");
        }
    }
}
