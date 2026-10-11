using System.Buffers;

namespace Ankus;

/// <summary>
/// Provides a closed scalar array view for native serialization and typed row conversion.
/// </summary>
internal interface IPgArray
{
    /// <summary>
    /// Gets the total row-major element count, including NULLs.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the PostgreSQL dimension lengths, empty for a rank-zero array.
    /// </summary>
    ReadOnlySpan<int> Lengths { get; }

    /// <summary>
    /// Gets one PostgreSQL lower bound per dimension.
    /// </summary>
    ReadOnlySpan<int> LowerBounds { get; }

    /// <summary>
    /// Gets the built-in scalar OID shared by every element.
    /// </summary>
    uint ElementOid { get; }

    /// <summary>
    /// Reads an element by its zero-based flat index.
    /// </summary>
    /// <param name="index">The row-major index.</param>
    /// <returns>The managed scalar, or null for a SQL NULL element.</returns>
    object? GetElement(int index);

    /// <summary>
    /// Writes the elements as the packed native array PostgreSQL stores for fixed-width primitives, when the element
    /// type is one and no element is NULL.
    /// </summary>
    /// <param name="buffer">The transport after its shape header.</param>
    /// <returns>Whether the elements were written.</returns>
    bool TryWritePacked(IBufferWriter<byte> buffer);
}
