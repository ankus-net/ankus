using System;
using System.Collections.Generic;
using System.Linq;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Converts and enumerates pgrx built-in OID constants using an explicit or active PostgreSQL major version.
/// </summary>
/// <remarks>
/// Explicit-version overloads work outside PostgreSQL. Other overloads require the calling backend thread
/// and read the selected native headers' major version. Versions 13 through 19 are supported.
/// Membership is a constant classification. It does not prove catalog existence, permissions, or object type.
/// </remarks>
public static partial class PgBuiltInOids
{
    /// <summary>
    /// Converts an unsigned datum-sized value without truncation using the active backend version.
    /// </summary>
    /// <param name="value">The unsigned input.</param>
    /// <param name="builtIn">The recognized numeric member, or zero on failure.</param>
    /// <param name="error">The specific conversion outcome.</param>
    /// <returns>Whether the selected version recognizes the value.</returns>
    public static bool TryFromValue(ulong value, out PgBuiltInOid builtIn, out PgOidLookupError error)
        => TryFromValue(value, NativeBackend.GetPostgresMajor(), out builtIn, out error);

    /// <summary>
    /// Converts an unsigned value using a supported major version without requiring a backend.
    /// </summary>
    /// <param name="value">The unsigned input; values above uint.MaxValue are rejected without truncation.</param>
    /// <param name="postgresMajor">The PostgreSQL major version, from 13 through 19.</param>
    /// <param name="builtIn">The recognized numeric member, or zero on failure.</param>
    /// <param name="error">None, Invalid, Ambiguous, or TooBig.</param>
    /// <returns>Whether conversion succeeded.</returns>
    public static bool TryFromValue(ulong value, int postgresMajor, out PgBuiltInOid builtIn, out PgOidLookupError error)
    {
        ValidateMajor(postgresMajor);
        builtIn = default;
        error = value switch
        {
            0 => PgOidLookupError.Invalid,
            > uint.MaxValue => PgOidLookupError.TooBig,
            _ when LookupName((uint)value, postgresMajor) is null => PgOidLookupError.Ambiguous,
            _ => PgOidLookupError.None,
        };
        if (error != PgOidLookupError.None)
        {
            return false;
        }

        builtIn = (PgBuiltInOid)value;
        return true;
    }

    /// <summary>
    /// Gets the exact native symbol for a recognized value in the active backend version.
    /// </summary>
    /// <param name="value">The numeric built-in candidate.</param>
    /// <returns>The native name, or null when this version does not recognize the value.</returns>
    public static string? GetNativeName(PgBuiltInOid value) => GetNativeName(value, NativeBackend.GetPostgresMajor());

    /// <summary>
    /// Gets the exact selected-version native symbol, including historical renames.
    /// </summary>
    /// <param name="value">The numeric built-in candidate, including an explicitly cast unknown value.</param>
    /// <param name="postgresMajor">The PostgreSQL major version, from 13 through 19.</param>
    /// <returns>The native name, or null when this version does not recognize the value.</returns>
    public static string? GetNativeName(PgBuiltInOid value, int postgresMajor)
    {
        ValidateMajor(postgresMajor);
        return LookupName((uint)value, postgresMajor);
    }

    /// <summary>
    /// Gets an immutable snapshot of the active version's recognized values in numeric order.
    /// </summary>
    /// <returns>The selected catalog members.</returns>
    public static IReadOnlyList<PgBuiltInOid> GetValues() => GetValues(NativeBackend.GetPostgresMajor());

    /// <summary>
    /// Gets an immutable snapshot of one version's recognized values in numeric order.
    /// </summary>
    /// <param name="postgresMajor">The PostgreSQL major version, from 13 through 19.</param>
    /// <returns>Every recognized member exactly once.</returns>
    public static IReadOnlyList<PgBuiltInOid> GetValues(int postgresMajor)
    {
        ValidateMajor(postgresMajor);
        PgBuiltInOid[] values = [.. Enum.GetValues<PgBuiltInOid>().Where(value => LookupName((uint)value, postgresMajor) is not null)];
        return Array.AsReadOnly(values);
    }

    private static void ValidateMajor(int postgresMajor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(postgresMajor, 13);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(postgresMajor, 19);
    }
}
