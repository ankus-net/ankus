using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Mirrors the native run-time configuration definition request.
/// </summary>
/// <remarks>
/// Text fields point to terminated UTF-8 owned by the caller for the duration of the call; PostgreSQL's
/// registration copies everything it retains.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeGucDefinition
{
    /// <summary>
    /// The short description.
    /// </summary>
    internal byte* _shortDescription;

    /// <summary>
    /// The optional long description.
    /// </summary>
    internal byte* _longDescription;

    /// <summary>
    /// The string default, unused unless <see cref="_kind"/> is 3.
    /// </summary>
    internal byte* _bootString;

    /// <summary>
    /// The enumerated labels.
    /// </summary>
    internal byte** _optionNames;

    /// <summary>
    /// The ordinals of the enumerated labels.
    /// </summary>
    internal int* _optionValues;

    /// <summary>
    /// One byte per label, nonzero for hidden labels.
    /// </summary>
    internal byte* _optionHidden;

    /// <summary>
    /// The real default.
    /// </summary>
    internal double _bootReal;

    /// <summary>
    /// The real minimum.
    /// </summary>
    internal double _minimumReal;

    /// <summary>
    /// The real maximum.
    /// </summary>
    internal double _maximumReal;

    /// <summary>
    /// The integer or enumerated default.
    /// </summary>
    internal int _bootInteger;

    /// <summary>
    /// The integer minimum.
    /// </summary>
    internal int _minimumInteger;

    /// <summary>
    /// The integer maximum.
    /// </summary>
    internal int _maximumInteger;

    /// <summary>
    /// The number of enumerated labels.
    /// </summary>
    internal int _optionCount;

    /// <summary>
    /// The native kind: 0 Boolean, 1 integer, 2 real, 3 string, 4 enumerated.
    /// </summary>
    internal int _kind;

    /// <summary>
    /// The <see cref="PgGucContext"/> value.
    /// </summary>
    internal int _context;

    /// <summary>
    /// The <see cref="PgGucUnit"/> value.
    /// </summary>
    internal int _unit;

    /// <summary>
    /// The <see cref="PgGucOptions"/> bits.
    /// </summary>
    internal uint _flags;

    /// <summary>
    /// The Boolean default.
    /// </summary>
    internal byte _bootBoolean;

    /// <summary>
    /// Nonzero when the string default is absent.
    /// </summary>
    internal byte _bootNull;

    /// <summary>
    /// The hook bits: 1 for check, 2 for assign and 4 for show.
    /// </summary>
    internal int _hooks;

    /// <summary>
    /// The hook slot, or zero without hooks.
    /// </summary>
    internal int _slot;
}
