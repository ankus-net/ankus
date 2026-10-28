using System;

namespace Ankus;

/// <summary>
/// Declares a nullable string PostgreSQL configuration setting on a static partial getter-only property.
/// </summary>
/// <param name="name">The qualified configuration name, including its extension prefix.</param>
/// <param name="defaultValue">The compiled default value.</param>
/// <param name="shortDescription">The short description displayed in PostgreSQL configuration metadata.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PgGucStringAttribute(string name, string? defaultValue, string shortDescription)
    : PgGucAttribute(name, shortDescription)
{
    /// <summary>
    /// Gets the compiled default value.
    /// </summary>
    public string? DefaultValue { get; } = defaultValue;
}
