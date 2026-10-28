using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ankus.PgConfig;

namespace Ankus.Testing;

/// <summary>
/// Carries explicit fixture, command and build selections through to the matching server and headers.
/// </summary>
internal static class PostgresTestSelection
{
    /// <summary>
    /// Resolves explicit selections before consulting the extension's evaluated project defaults.
    /// </summary>
    /// <param name="options">The fixture's publication and installation options.</param>
    /// <param name="properties">The literal MSBuild properties used by the eventual publication.</param>
    /// <param name="cancellationToken">Cancels project and installation queries.</param>
    /// <returns>The installation used for both native publication and the test server.</returns>
    internal static async Task<PostgresInstallation> ResolveAsync(PostgresExtensionTestOptions options,
        IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken)
    {
        if (options.Installation is PostgresInstallation explicitInstallation)
        {
            return await ValidatePropertiesAsync(options.ProjectPath, explicitInstallation, properties, cancellationToken)
                .ConfigureAwait(false);
        }

        string? commandPath = Environment.GetEnvironmentVariable("ANKUS_TEST_PG_CONFIG");
        if (!string.IsNullOrWhiteSpace(commandPath))
        {
            PostgresInstallation commandInstallation = await PostgresInstallation.CreateAsync(commandPath, cancellationToken).ConfigureAwait(false);
            return await ValidatePropertiesAsync(options.ProjectPath, commandInstallation, properties, cancellationToken)
                .ConfigureAwait(false);
        }

        string? commandMajor = Environment.GetEnvironmentVariable("ANKUS_TEST_POSTGRES_MAJOR");
        string? hostMajor = ReadHostValue("Ankus.Testing.PostgresMajor");
        int? major = ParseMajor(commandMajor ?? hostMajor,
            commandMajor is null ? "AnkusPostgresMajor" : "ANKUS_TEST_POSTGRES_MAJOR");
        string? hostPath = ReadHostValue("Ankus.Testing.PgConfigPath");
        if (!string.IsNullOrWhiteSpace(hostPath))
        {
            PostgresInstallation installation = await PostgresInstallation.CreateAsync(hostPath, cancellationToken).ConfigureAwait(false);
            RequireMajor(installation, major);
            return await ValidatePropertiesAsync(options.ProjectPath, installation, properties, cancellationToken).ConfigureAwait(false);
        }

        PostgresProjectSettings settings = await PostgresProjectSettings.ReadAsync(options.ProjectPath, options.Configuration,
            properties, major, cancellationToken).ConfigureAwait(false);
        PostgresInstallation selected = settings.PgConfigPath is null
            ? await PostgresInstallation.DiscoverAsync(settings.PostgresMajor,
                ReadHostValue("Ankus.Testing.HomeDirectory"), cancellationToken).ConfigureAwait(false)
            : await PostgresInstallation.CreateAsync(settings.PgConfigPath, cancellationToken).ConfigureAwait(false);
        RequireMajor(selected, settings.HasExplicitPostgresMajor ? settings.PostgresMajor : null);
        return selected;
    }

    private static async Task<PostgresInstallation> ValidatePropertiesAsync(string projectPath, PostgresInstallation installation,
        IReadOnlyDictionary<string, string> properties, CancellationToken cancellationToken)
    {
        if (properties.GetValueOrDefault("AnkusPostgresMajor") is string major)
        {
            RequireMajor(installation, ParseMajor(major, "AnkusPostgresMajor"));
        }

        if (properties.GetValueOrDefault("AnkusPgConfigPath") is string value && !string.IsNullOrWhiteSpace(value))
        {
            string path = value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)
                ? Path.GetFullPath(value, Path.GetDirectoryName(Path.GetFullPath(projectPath))!) : value;
            PostgresInstallation selected = await PostgresInstallation.CreateAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(selected.PgConfigPath, installation.PgConfigPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The fixture selected '{installation.PgConfigPath}', but BuildProperties selected '{selected.PgConfigPath}'.");
            }
        }

        return installation;
    }

    /// <summary>
    /// Reads the standard .NET runtime configuration without changing process-wide state.
    /// </summary>
    /// <param name="name">The runtime configuration key emitted by the testing package's build targets.</param>
    /// <returns>The configured value, or null when the calling build did not select it.</returns>
    private static string? ReadHostValue(string name)
    {
        string? value = Convert.ToString(AppContext.GetData(name), CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Validates a selected major without silently reverting to the default.
    /// </summary>
    /// <param name="value">The optional explicit major.</param>
    /// <param name="source">The setting named in an invalid-value diagnostic.</param>
    /// <returns>The selected major, or null when unspecified.</returns>
    private static int? ParseMajor(string? value, string source)
    {
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int major) && major is >= 13 and <= 19
            ? major : throw new InvalidOperationException($"{source} must select PostgreSQL 13–19.");
    }

    /// <summary>
    /// Prevents an explicitly selected configuration path from silently changing the requested major.
    /// </summary>
    /// <param name="installation">The installation reported by pg_config.</param>
    /// <param name="major">The selected major, when one was specified.</param>
    private static void RequireMajor(PostgresInstallation installation, int? major)
    {
        if (major is int expected && installation.Version.Major != expected)
        {
            throw new InvalidOperationException($"The build selected PostgreSQL {expected}, but pg_config reports PostgreSQL {installation.Version.Major}.");
        }
    }
}
