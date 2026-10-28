using System;
using System.Text;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Borrows a strictly validated UTF-8 representation of PostgreSQL text.
/// </summary>
/// <remarks>
/// UTF-8 databases retain flat or short native bytes. Other database encodings require conversion
/// into a private child context; the original server representation remains available through Datum.
/// The source owner must remain alive and direct scalar inputs expire at callback exit.
/// Copied byte length and type metadata remain readable after disposal.
/// Dispose explicitly created views while their backend is active.
/// </remarks>
/// <param name="value">Present text, varchar or bpchar, optionally wrapped in a domain.</param>
public sealed class PgTextView(PgDatum value) : IDisposable
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private readonly NativeBorrowedBuffer _buffer = new(value, NativeBufferKind.Text);

    /// <summary>
    /// Gets the UTF-8 byte length, which may differ from the character count or server byte length.
    /// </summary>
    public int Utf8Length => _buffer.Length;

    /// <summary>
    /// Gets the original PostgreSQL type OID, preserving varchar, bpchar and domain identities.
    /// </summary>
    public uint TypeOid => _buffer.TypeOid;

    /// <summary>
    /// Gets the checked original server-encoded datum, valid until this view or its source expires.
    /// </summary>
    public PgDatum Datum => _buffer.Datum;

    /// <summary>
    /// Reads one UTF-8 byte after checking the native lifetime and zero-based byte index.
    /// </summary>
    /// <param name="index">A byte offset, independent of Unicode character boundaries.</param>
    /// <returns>The byte at the requested position.</returns>
    public byte GetUtf8Byte(int index)
    {
        ReadOnlySpan<byte> bytes = _buffer.GetSpan();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, bytes.Length);
        return bytes[index];
    }

    /// <summary>
    /// Copies the entire UTF-8 representation after validating its lifetime and destination capacity.
    /// </summary>
    /// <param name="destination">Storage large enough for Utf8Length bytes.</param>
    public void CopyUtf8To(Span<byte> destination) => _buffer.GetSpan().CopyTo(destination);

    /// <summary>
    /// Decodes the checked UTF-8 bytes into an independent managed string without lossy replacement.
    /// </summary>
    /// <returns>The exact text, including trailing bpchar spaces when present.</returns>
    public override string ToString() => s_utf8.GetString(_buffer.GetSpan());

    /// <summary>
    /// Checks the lifetime once and exposes the UTF-8 bytes without a managed copy.
    /// </summary>
    /// <returns>A span whose validity is the caller's responsibility after this method returns.</returns>
    /// <remarks>
    /// Never reset or delete the source owner, dispose this view, or exit its callback while using
    /// the span. Keep it on the originating backend thread and do not retain it across backend calls.
    /// The span cannot check subsequent native lifetime changes; prefer checked reads or copies.
    /// </remarks>
    public ReadOnlySpan<byte> DangerousGetUtf8Span() => _buffer.GetSpan();

    /// <summary>
    /// Releases private detoast and encoding allocations and invalidates native access.
    /// </summary>
    public void Dispose() => _buffer.Dispose();
}
