using System;

namespace Ankus;

/// <summary>
/// Assigns an exact, case-sensitive PostgreSQL label to a PgEnum member.
/// </summary>
/// <remarks>
/// Specifies a label of at most 63 UTF-8 bytes, including an empty label.
/// </remarks>
/// <param name="label">The label, without zero characters.</param>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class PgEnumLabelAttribute(string label) : Attribute
{
    /// <summary>
    /// Gets the exact PostgreSQL label.
    /// </summary>
    public string Label { get; } = label;
}
