using System;

namespace Ankus;

/// <summary>
/// Converts bytea, text and C-string values into checked borrowed views without changing type identity.
/// </summary>
internal static class PgBufferViews
{
    /// <summary>
    /// Determines whether the requested representation needs native buffer storage.
    /// </summary>
    /// <typeparam name="T">The declared managed result type.</typeparam>
    /// <returns>Whether the result is a concrete buffer view.</returns>
    internal static bool Is<T>() => typeof(T) == typeof(PgByteaView) || typeof(T) == typeof(PgTextView) || typeof(T) == typeof(PgCStringView);

    /// <summary>
    /// Reads a present buffer under its source lifetime, or returns null for SQL NULL.
    /// </summary>
    /// <typeparam name="T">The concrete buffer view type.</typeparam>
    /// <param name="value">The raw value with its original PostgreSQL type.</param>
    /// <returns>The checked view or null.</returns>
    internal static T Read<T>(PgDatum value)
    {
        value.Lifetime.Validate();
        if (typeof(T) == typeof(PgCStringView))
        {
            if (value.TypeOid != 2275)
            {
                throw new InvalidCastException("The datum is not a PostgreSQL C string.");
            }

            return value.IsNull || value.DangerousGetBits() == 0 ? default! : GenericValue.As<T>(new PgCStringView(value));
        }

        if (value.IsNull)
        {
            return default!;
        }

        object view = typeof(T) == typeof(PgByteaView) ? new PgByteaView(value) : new PgTextView(value);
        return (T)view;
    }
}
