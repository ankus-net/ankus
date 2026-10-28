using System;

namespace Ankus.PgConfig;

/// <summary>
/// Selects persistent port bases while leaving unspecified settings unchanged.
/// Each base reserves room for every supported PostgreSQL major, from 13 through 19.
/// </summary>
/// <param name="basePort">The development port base, from 0 through 65516, or null to preserve it.</param>
/// <param name="baseTestingPort">The test port base, from 0 through 65516, or null to preserve it.</param>
public sealed class PostgresPortOptions(int? basePort = null, int? baseTestingPort = null)
{
    /// <summary>
    /// Gets the development port base to persist, or null to preserve the existing setting.
    /// </summary>
    public int? BasePort { get; } = Validate(basePort, nameof(basePort));

    /// <summary>
    /// Gets the test port base to persist, or null to preserve the existing setting.
    /// </summary>
    public int? BaseTestingPort { get; } = Validate(baseTestingPort, nameof(baseTestingPort));

    private static int? Validate(int? value, string name)
    {
        if (value is < 0 or > 65516)
        {
            throw new ArgumentOutOfRangeException(name, value, "Port bases must be between 0 and 65516.");
        }

        return value;
    }
}
