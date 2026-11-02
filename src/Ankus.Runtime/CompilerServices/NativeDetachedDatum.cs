using System;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Gets whether this value carries an exact copy of a cell that has no managed conversion.
    /// </summary>
    internal readonly bool IsDetachedDatum => _auxiliary1 == -9;

    /// <summary>
    /// Copies a detached cell after checking its type and storage shape.
    /// </summary>
    /// <param name="oid">The expected PostgreSQL base type OID.</param>
    /// <returns>The managed copy.</returns>
    internal readonly DetachedDatum ReadDetachedDatum(uint oid)
    {
        // The low bit marks a by-value word, and the next marks text that is not valid UTF-8.
        bool untranslatable = (_temporalInfinity & 2) != 0;
        bool valid = IsDetachedDatum && _isNull == 0 && oid != 0 && unchecked((uint)_auxiliary2) == oid && _length >= 0 &&
            (_length == 0 || _data != null) && (_temporalInfinity & ~2) switch
            {
                1 => _length == 0,
                0 => _integer is -1 or -2 || (_integer is > 0 and <= short.MaxValue && _integer == _length),
                _ => false,
            };
        if (!valid)
        {
            throw new InvalidOperationException("Invalid detached PostgreSQL value transport.");
        }

        return (_temporalInfinity & 1) != 0
            ? new DetachedDatum(oid, _integer, untranslatable)
            : new DetachedDatum(oid, (short)_integer, new ReadOnlySpan<byte>(_data, _length), untranslatable);
    }

    /// <summary>
    /// Copies a detached cell into owned native transport.
    /// </summary>
    /// <param name="value">The detached cell.</param>
    /// <returns>The owned transport envelope.</returns>
    internal static NativeValue FromDetachedDatum(DetachedDatum value)
    {
        NativeValue result = value.IsByValue ? new NativeValue { _temporalInfinity = 1 } : FromBytes(value.Payload);
        if (value.IsUntranslatable)
        {
            result._temporalInfinity |= 2;
        }

        result._integer = value.Bits;
        result._auxiliary1 = -9;
        result._auxiliary2 = unchecked((int)value.TypeOid);
        return result;
    }
}
