using System;
using System.Buffers;

namespace Ankus;

/// <summary>
/// Defines the text and binary storage representations of a generated PostgreSQL base type.
/// </summary>
/// <remarks>
/// The generated mapping constructs one codec on first use inside the managed error boundary. Implementations must not retain borrowed
/// input buffers or PostgreSQL call state. Storage bytes exclude PostgreSQL's variable-length header.
/// Preserve the storage format across extension upgrades or provide an explicit data migration.
/// </remarks>
/// <typeparam name="T">The managed type carrying PgType.</typeparam>
public abstract class PgTypeCodec<T> : PgTypeTextCodec<T>
{
    /// <summary>
    /// Reads an independent managed value from the complete stored payload.
    /// </summary>
    /// <param name="payload">The borrowed payload, valid only during this call.</param>
    /// <returns>The decoded value.</returns>
    public abstract T Read(ReadOnlySpan<byte> payload);

    /// <summary>
    /// Writes the complete stored payload for a present managed value.
    /// </summary>
    /// <param name="value">The value to store.</param>
    /// <param name="destination">The output buffer; do not retain it after this call.</param>
    public abstract void Write(T value, IBufferWriter<byte> destination);
}
