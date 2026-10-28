using System;
using System.Diagnostics;
using System.Globalization;

namespace Ankus.PgConfig;

/// <summary>
/// Represents a PostgreSQL server version reported by <c>pg_config --version</c>,
/// including beta and release-candidate builds.
/// </summary>
public readonly struct PostgresVersion : IEquatable<PostgresVersion>
{
    /// <summary>
    /// Initializes a PostgreSQL version.
    /// </summary>
    /// <param name="major">The major PostgreSQL version.</param>
    /// <param name="minor">The stable minor release, or zero for prereleases.</param>
    /// <param name="stage">The release stage.</param>
    /// <param name="stageNumber">The beta or release-candidate number.</param>
    public PostgresVersion(int major, int minor, PostgresReleaseStage stage, int stageNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(stageNumber);

        Major = major;
        Minor = minor;
        Stage = stage;
        StageNumber = stageNumber;
    }

    /// <summary>
    /// Gets the PostgreSQL major version.
    /// </summary>
    public int Major { get; }

    /// <summary>
    /// Gets the stable minor release, or zero for a prerelease.
    /// </summary>
    public int Minor { get; }

    /// <summary>
    /// Gets the PostgreSQL release stage.
    /// </summary>
    public PostgresReleaseStage Stage { get; }

    /// <summary>
    /// Gets the beta or release-candidate number, or zero for a stable release.
    /// </summary>
    public int StageNumber { get; }

    /// <summary>
    /// Gets the selector used by Ankus commands, such as <c>pg18</c>.
    /// </summary>
    public string Label => $"pg{Major.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Parses the complete output of <c>pg_config --version</c>.
    /// </summary>
    /// <param name="value">The version text reported by PostgreSQL.</param>
    /// <returns>The parsed PostgreSQL version.</returns>
    /// <exception cref="FormatException">The text is not a recognized PostgreSQL version.</exception>
    internal static PostgresVersion Parse(string value)
    {
        const string prefix = "PostgreSQL ";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new FormatException($"Invalid PostgreSQL version string: '{value}'.");
        }

        ReadOnlySpan<char> version = value.AsSpan(prefix.Length);
        int suffix = version.IndexOfAny(' ', '(');
        if (suffix >= 0)
        {
            version = version[..suffix];
        }

        int dot = version.IndexOf('.');
        if (dot < 0)
        {
            int beta = version.IndexOf("beta", StringComparison.OrdinalIgnoreCase);
            if (beta >= 0)
            {
                return ParsePrerelease(version, beta, "beta", PostgresReleaseStage.Beta);
            }

            int releaseCandidate = version.IndexOf("rc", StringComparison.OrdinalIgnoreCase);
            if (releaseCandidate >= 0)
            {
                return ParsePrerelease(
                    version,
                    releaseCandidate,
                    "rc",
                    PostgresReleaseStage.ReleaseCandidate);
            }
        }

        // As pgrx does, a vendor suffix directly after the minor version, such as "11.2-FOO-BAR+", is ignored.
        ReadOnlySpan<char> minorText = dot < 0 ? [] : version[(dot + 1)..];
        int digits = minorText.IndexOfAnyExceptInRange('0', '9');
        minorText = digits < 0 ? minorText : minorText[..digits];
        if (dot < 1 ||
            !int.TryParse(version[..dot], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(minorText, NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
        {
            throw new FormatException($"Invalid PostgreSQL version string: '{value}'.");
        }

        return new PostgresVersion(major, minor, PostgresReleaseStage.Stable, 0);
    }

    /// <inheritdoc />
    public bool Equals(PostgresVersion other)
        => Major == other.Major &&
           Minor == other.Minor &&
           Stage == other.Stage &&
           StageNumber == other.StageNumber;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PostgresVersion other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Stage, StageNumber);

    /// <inheritdoc />
    public override string ToString()
        => Stage switch
        {
            PostgresReleaseStage.Stable => $"{Major}.{Minor}",
            PostgresReleaseStage.Beta => $"{Major}beta{StageNumber}",
            PostgresReleaseStage.ReleaseCandidate => $"{Major}rc{StageNumber}",
            _ => throw new UnreachableException(),
        };

    /// <summary>
    /// Determines whether two PostgreSQL versions are equal.
    /// </summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns><see langword="true"/> when both versions are equal.</returns>
    public static bool operator ==(PostgresVersion left, PostgresVersion right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// Determines whether two PostgreSQL versions differ.
    /// </summary>
    /// <param name="left">The first version.</param>
    /// <param name="right">The second version.</param>
    /// <returns><see langword="true"/> when the versions differ.</returns>
    public static bool operator !=(PostgresVersion left, PostgresVersion right)
    {
        return !left.Equals(right);
    }

    private static PostgresVersion ParsePrerelease(
        ReadOnlySpan<char> value,
        int marker,
        ReadOnlySpan<char> markerText,
        PostgresReleaseStage stage)
    {
        if (!int.TryParse(value[..marker], NumberStyles.None, CultureInfo.InvariantCulture, out int major) ||
            !int.TryParse(
                value[(marker + markerText.Length)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int stageNumber))
        {
            throw new FormatException($"Invalid PostgreSQL prerelease version: '{value}'.");
        }

        return new PostgresVersion(major, 0, stage, stageNumber);
    }
}
