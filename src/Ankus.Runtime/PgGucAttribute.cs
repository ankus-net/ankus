using System;

namespace Ankus;

/// <summary>
/// Shares native registration metadata and typed hook names among PostgreSQL configuration declarations.
/// </summary>
/// <param name="name">The qualified configuration name.</param>
/// <param name="shortDescription">The short configuration description.</param>
[AttributeUsage(AttributeTargets.Property)]
public abstract class PgGucAttribute(string name, string shortDescription) : Attribute
{
    /// <summary>
    /// Gets the qualified PostgreSQL configuration name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the short configuration description.
    /// </summary>
    public string ShortDescription { get; } = shortDescription;

    /// <summary>
    /// Gets or sets the optional extended description.
    /// </summary>
    public string? LongDescription
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets when PostgreSQL permits changes. The default allows session changes by any user.
    /// </summary>
    public PgGucContext Context
    {
        get;
        set;
    } = PgGucContext.UserSet;

    /// <summary>
    /// Gets or sets additional PostgreSQL configuration behaviors.
    /// </summary>
    public PgGucOptions Flags
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the base parsing and display unit for an integer or real setting.
    /// </summary>
    public PgGucUnit Unit
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the static check method that accepts a proposed value and source and returns a typed check result.
    /// </summary>
    public string? Check
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the static assignment method that receives the accepted value and optional owned extra bytes.
    /// </summary>
    public string? Assign
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the static display method that receives the current value and optional owned extra bytes.
    /// </summary>
    public string? Show
    {
        get;
        set;
    }
}
