using System;

namespace Ankus;

/// <summary>
/// Selects the PostgreSQL configuration label for a C# enum member without creating a PostgreSQL enum type.
/// </summary>
/// <param name="name">The configuration label, compared with PostgreSQL's case-insensitive ASCII rules.</param>
[AttributeUsage(AttributeTargets.Field)]
public sealed class PgGucLabelAttribute(string name) : Attribute
{
    /// <summary>
    /// Gets the configuration label.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets or sets whether the label remains accepted while omitted from lists of available values.
    /// </summary>
    public bool Hidden
    {
        get;
        set;
    }
}
