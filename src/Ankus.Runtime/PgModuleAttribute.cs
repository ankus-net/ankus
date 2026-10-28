using System;

namespace Ankus;

/// <summary>
/// Customizes the native module identity reported by PostgreSQL 18 and later.
/// </summary>
/// <remarks>
/// Omitted values use the project's assembly name and version. This identity is
/// independent of the SQL extension name and version in its control file.
/// Earlier PostgreSQL versions retain their ordinary module compatibility block.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class PgModuleAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the module name, or null to use the managed assembly name.
    /// </summary>
    public string? Name
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the module version, or null to use the evaluated project version.
    /// </summary>
    public string? Version
    {
        get;
        set;
    }
}
