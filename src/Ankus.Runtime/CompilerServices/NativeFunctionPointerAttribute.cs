using System;
using System.ComponentModel;

namespace Ankus.CompilerServices;

/// <summary>
/// Identifies the canonical native prototype represented by a generated function-pointer value.
/// </summary>
/// <param name="signature">The nonnegative function type index within the complete measured binding.</param>
/// <remarks>
/// This is a generated-code contract. It does not establish ABI compatibility for user-defined types.
/// </remarks>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class NativeFunctionPointerAttribute(int signature) : Attribute
{
    /// <summary>
    /// Gets the canonical function type index retained by the native declaration companion.
    /// </summary>
    public int Signature { get; } = signature >= 0 ? signature : throw new ArgumentOutOfRangeException(nameof(signature));
}
