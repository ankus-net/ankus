using System;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Reads plain aggregate metadata or a rooted internal-state ID, rejecting borrowed buffers and conflicting scalar tags.
    /// </summary>
    internal readonly long ReadAggregateScalar(bool allowNull = false)
    {
        if (_isNull > (allowNull ? 1 : 0) || (_isNull != 0 && _integer != 0) || _data != null || _length != 0 ||
            _auxiliary1 != 0 || _auxiliary2 != 0 || _temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native aggregate scalar envelope.");
        }

        return _integer;
    }
}
