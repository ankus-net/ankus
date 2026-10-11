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
        bool valid = IsDetachedDatum && _isNull == 0 && oid != 0 && unchecked((uint)_auxiliary2) == oid && _length >= 0 &&
            (_length == 0 || _data != null) && _temporalInfinity switch
            {
                1 => _length == 0,
                0 => _integer is -1 or -2 || (_integer is > 0 and <= short.MaxValue && _integer == _length),
                _ => false,
            };
        if (!valid)
        {
            throw new InvalidOperationException("Invalid detached PostgreSQL value transport.");
        }

        return _temporalInfinity == 1
            ? new DetachedDatum(oid, _integer)
            : new DetachedDatum(oid, (short)_integer, new ReadOnlySpan<byte>(_data, _length));
    }

    /// <summary>
    /// Copies a detached cell into owned native transport.
    /// </summary>
    /// <param name="value">The detached cell.</param>
    /// <returns>The owned transport envelope.</returns>
    internal static NativeValue FromDetachedDatum(DetachedDatum value)
    {
        NativeValue result = value.IsByValue ? new NativeValue { _temporalInfinity = 1 } : FromBytes(value.Payload);
        result._integer = value.Bits;
        result._auxiliary1 = -9;
        result._auxiliary2 = unchecked((int)value.TypeOid);
        return result;
    }
}
