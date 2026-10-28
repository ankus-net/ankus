using System;

namespace Ankus;

/// <summary>
/// Declares a floating-point PostgreSQL configuration setting on a static partial getter-only property.
/// </summary>
/// <param name="name">The qualified configuration name, including its extension prefix.</param>
/// <param name="defaultValue">The compiled default value.</param>
/// <param name="shortDescription">The short description displayed in PostgreSQL configuration metadata.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PgGucRealAttribute(string name, double defaultValue, string shortDescription)
    : PgGucAttribute(name, shortDescription)
{
    /// <summary>
    /// Gets the compiled default value.
    /// </summary>
    public double DefaultValue { get; } = defaultValue;

    /// <summary>
    /// Gets or sets the inclusive minimum value.
    /// </summary>
    public double Minimum
    {
        get;
        set;
    } = double.MinValue;

    /// <summary>
    /// Gets or sets the inclusive maximum value.
    /// </summary>
    public double Maximum
    {
        get;
        set;
    } = double.MaxValue;
}
