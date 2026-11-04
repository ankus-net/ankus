using System;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Validates the scalar configuration transport before consuming its integer or floating-point bits.
    /// </summary>
    internal readonly long ReadGucScalar()
    {
        if (IsNull != 0 || _data != null || _length != 0 || _auxiliary1 != 0 || _auxiliary2 != 0 || _temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native scalar configuration transport.");
        }

        return Integral;
    }

    /// <summary>
    /// Validates a borrowed string or extra-byte envelope and distinguishes absent from empty data.
    /// </summary>
    internal readonly ReadOnlySpan<byte> ReadGucBuffer(out bool isNull)
    {
        if (IsNull > 1 || Integral != 0 || _auxiliary1 != 0 || _auxiliary2 != 0 || _temporalInfinity != 0 || _length < 0 ||
            (IsNull == 1 ? _data != null || _length != 0 : _data == null))
        {
            throw new InvalidOperationException("Invalid native configuration buffer transport.");
        }

        isNull = IsNull == 1;
        return new ReadOnlySpan<byte>(_data, _length);
    }
}
