using System;

namespace Ankus;

/// <summary>
/// Applies PostgreSQL numeric precision and scale to a generated function parameter or return value.
/// Values are rounded with PostgreSQL's typmod rules, then checked for overflow.
/// </summary>
/// <remarks>
/// Use on <see cref="PgNumeric"/> or <see cref="decimal"/>, including nullable forms.
/// Constraints apply at the SQL boundary; direct C# calls bypass them. Negative scales and scales
/// above precision require PostgreSQL 15 or later. PostgreSQL function signatures themselves
/// do not retain numeric type modifiers, so the generated dispatcher enforces this contract.
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
public sealed class PgNumericPrecisionAttribute : Attribute
{
    /// <summary>
    /// Declares numeric precision and scale.
    /// </summary>
    /// <param name="precision">The maximum significant digits, one through 1000.</param>
    /// <param name="scale">The scale, -1000 through 1000. The default is zero.</param>
    public PgNumericPrecisionAttribute(int precision, int scale = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(precision, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, -1000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(scale, 1000);
        Precision = precision;
        Scale = scale;
    }

    /// <summary>
    /// Gets the maximum significant digits.
    /// </summary>
    public int Precision { get; }

    /// <summary>
    /// Gets the declared scale.
    /// </summary>
    public int Scale { get; }

    /// <summary>
    /// Gets or sets the exact C# tuple element to constrain within a typed aggregate argument group.
    /// Omit for a scalar parameter or return value.
    /// </summary>
    public string? Element
    {
        get;
        set;
    }
}
