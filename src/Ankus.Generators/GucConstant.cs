using System;

namespace Ankus.Generators;

/// <summary>
/// Retains validated immutable primitive constants with exact floating-point identity at cache boundaries.
/// </summary>
/// <param name="value">The validated boot value or bound, including null and signed real zero.</param>
internal readonly struct GucConstant(object? value) : IEquatable<GucConstant>
{
    /// <summary>
    /// Gets the original primitive value for native literal rendering.
    /// </summary>
    internal object? Value { get; } = value;

    /// <summary>
    /// Compares real constants by their bits and other validated primitives by their value and type.
    /// </summary>
    /// <param name="other">The other detached constant.</param>
    /// <returns>Whether native literal rendering can reuse this constant's value.</returns>
    public bool Equals(GucConstant other)
        => Value is double first && other.Value is double second
            ? BitConverter.DoubleToInt64Bits(first) == BitConverter.DoubleToInt64Bits(second)
            : object.Equals(Value, other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GucConstant other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => Value is double real ? BitConverter.DoubleToInt64Bits(real).GetHashCode() : Value?.GetHashCode() ?? 0;

    /// <summary>
    /// Compares two constants using their exact detached identities.
    /// </summary>
    /// <param name="left">The first constant.</param>
    /// <param name="right">The second constant.</param>
    /// <returns>Whether the constants preserve the same native value.</returns>
    public static bool operator ==(GucConstant left, GucConstant right) => left.Equals(right);

    /// <summary>
    /// Detects a changed primitive identity, including a changed real sign bit.
    /// </summary>
    /// <param name="left">The first constant.</param>
    /// <param name="right">The second constant.</param>
    /// <returns>Whether the constants require distinct rendering.</returns>
    public static bool operator !=(GucConstant left, GucConstant right) => !left.Equals(right);
}
