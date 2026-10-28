using System;

namespace Ankus;

/// <summary>
/// Declares an integer PostgreSQL configuration setting on a static partial getter-only property.
/// </summary>
/// <param name="name">The qualified configuration name, including its extension prefix.</param>
/// <param name="defaultValue">The compiled default value.</param>
/// <param name="shortDescription">The short description displayed in PostgreSQL configuration metadata.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PgGucIntAttribute(string name, int defaultValue, string shortDescription)
    : PgGucAttribute(name, shortDescription)
{
    /// <summary>
    /// Gets the compiled default value.
    /// </summary>
    public int DefaultValue { get; } = defaultValue;

    /// <summary>
    /// Gets or sets the inclusive minimum value.
    /// </summary>
    public int Minimum
    {
        get;
        set;
    } = int.MinValue;

    /// <summary>
    /// Gets or sets the inclusive maximum value.
    /// </summary>
    public int Maximum
    {
        get;
        set;
    } = int.MaxValue;
}
