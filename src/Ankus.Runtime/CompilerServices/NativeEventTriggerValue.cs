using System;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Copies one nonnull, nonempty event name or command tag after validating its native scalar text header.
    /// </summary>
    internal readonly string ReadEventTriggerText()
    {
        if (_isNull != 0 || _integer != 0 || _data == null || _length <= 0 ||
            _auxiliary1 != 0 || _auxiliary2 != 0 || _temporalInfinity != 0)
        {
            throw new InvalidOperationException("Invalid native event trigger text envelope.");
        }

        string value = ReadString();
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Event trigger metadata cannot contain a zero character.");
        }

        return value;
    }
}
