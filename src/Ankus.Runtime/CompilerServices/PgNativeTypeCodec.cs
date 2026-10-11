using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Implements statically validated native-layout custom-type storage without structural serialization.
/// </summary>
/// <typeparam name="T">The generated unmanaged native layout.</typeparam>
/// <param name="expectedSize">The complete size computed by the generator.</param>
/// <param name="padding">Offset and length pairs of the layout's padding, which every write sets to zero.</param>
/// <param name="createTextCodec">The lazy factory for SQL text conversion.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PgNativeTypeCodec<T>(int expectedSize, int[] padding, Func<PgTypeTextCodec<T>> createTextCodec) : PgTypeCodec<T>
    where T : unmanaged
{
    private readonly int _size = expectedSize == Unsafe.SizeOf<T>() ? expectedSize :
        throw new ArgumentException("The managed native layout does not match the generated size.", nameof(expectedSize));
    private readonly int[] _padding = NativeLayoutPadding.Validate(padding, expectedSize);
    private readonly Func<PgTypeTextCodec<T>> _createTextCodec = createTextCodec ?? throw new ArgumentNullException(nameof(createTextCodec));
    private Lazy<PgTypeTextCodec<T>>? _textCodec;

    /// <summary>
    /// Gets the shared text adapter without constructing it during binary storage operations.
    /// </summary>
    private PgTypeTextCodec<T> TextCodec => LazyInitializer.EnsureInitialized(ref _textCodec,
        () => new Lazy<PgTypeTextCodec<T>>(() => _createTextCodec() ??
            throw new InvalidOperationException("A custom text codec factory returned null."))).Value;

    /// <inheritdoc />
    public override T Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TextCodec.Parse(text);
    }

    /// <inheritdoc />
    public override string Format(T value) => TextCodec.Format(value) ??
        throw new InvalidOperationException("A custom text codec returned null text for a present value.");

    /// <inheritdoc />
    public override T Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != _size)
        {
            throw new PgException("22P03", "Invalid native-layout custom-type value.",
                detail: $"Expected {_size} payload bytes; received {payload.Length}.");
        }

        return MemoryMarshal.Read<T>(payload);
    }

    /// <inheritdoc />
    public override void Write(T value, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Span<byte> payload = destination.GetSpan(_size)[.._size];
        MemoryMarshal.Write(payload, in value);
        NativeLayoutPadding.Clear(payload, _padding);
        destination.Advance(_size);
    }
}
