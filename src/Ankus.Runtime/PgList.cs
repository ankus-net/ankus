using System;
using System.Diagnostics.CodeAnalysis;

namespace Ankus;

/// <summary>
/// Creates typed PostgreSQL lists and explicitly borrows existing native containers.
/// </summary>
public static class PgList
{
    /// <summary>
    /// Allocates a managed wrapper before parsing trusted catalog defaults into its explicit native owner.
    /// </summary>
    internal static PgList<nint> ParseDefaults(string source, int count, PgMemoryContext owner)
    {
        var result = new PgList<nint>();
        result.InitializeDefaults(source, count, owner);
        return result;
    }

    /// <summary>
    /// Creates an empty list bound to the selected context's current lifetime.
    /// </summary>
    /// <typeparam name="T">Exactly int, uint (OID), PgTransactionId, or nint (opaque pointer).</typeparam>
    /// <param name="context">The native owner, or null for the current context.</param>
    /// <returns>An owned NIL list; transaction-ID lists require PostgreSQL 16 or later.</returns>
    public static PgList<T> Create<T>(PgMemoryContext? context = null) where T : unmanaged => Create<T>([], context);

    /// <summary>
    /// Creates a native list containing an independent copy of the supplied cell values.
    /// </summary>
    /// <typeparam name="T">Exactly int, uint (OID), PgTransactionId, or nint (opaque pointer).</typeparam>
    /// <param name="values">The initial values, including zero and all raw bit patterns.</param>
    /// <param name="context">The native owner, or null for the current context.</param>
    /// <returns>An owned container; pointer elements remain owned by their original owners.</returns>
    public static PgList<T> Create<T>(ReadOnlySpan<T> values, PgMemoryContext? context = null) where T : unmanaged
    {
        var list = new PgList<T>();
        list.Initialize(values, context ?? PgMemoryContext.Current, 0, borrow: false);
        return list;
    }

    /// <summary>
    /// Borrows an exclusively accessible palloc-compatible list after validating its native cell tag.
    /// </summary>
    /// <typeparam name="T">The expected cell type.</typeparam>
    /// <param name="address">The native List pointer; null represents a valid empty list.</param>
    /// <param name="owner">The exact allocator context for both the header and any separate cell buffer.</param>
    /// <returns>A borrowed list whose disposal leaves native storage intact.</returns>
    /// <exception cref="ArgumentException">The native list has a different cell tag.</exception>
    /// <remarks>
    /// The caller proves valid storage and exclusive access until disposal or detach. An owner
    /// anchor cannot detect external frees. Mutations may free or replace the native container;
    /// obtain its current pointer after structural changes. Pointer elements are never freed.
    /// </remarks>
    public static unsafe PgList<T> DangerousBorrow<T>(void* address, PgMemoryContext owner) where T : unmanaged
    {
        if (!DangerousTryBorrow(address, owner, out PgList<T>? list))
        {
            throw new ArgumentException("The native PostgreSQL list has a different cell type.", nameof(address));
        }

        return list;
    }

    /// <summary>
    /// Attempts an exclusive typed borrow, returning false only for a mismatched native tag.
    /// </summary>
    /// <typeparam name="T">The expected cell type.</typeparam>
    /// <param name="address">The native List pointer, including null for NIL.</param>
    /// <param name="owner">The exact native allocator context and reset-sensitive lifetime anchor.</param>
    /// <param name="list">The borrowed wrapper on success, otherwise null.</param>
    /// <returns>Whether the native tag matches; NIL matches every supported cell type.</returns>
    /// <remarks>
    /// The same allocator, exclusive-access and lifetime requirements as DangerousBorrow apply.
    /// Unsupported server versions, invalid metadata and dead contexts still throw.
    /// </remarks>
    public static unsafe bool DangerousTryBorrow<T>(void* address, PgMemoryContext owner, [NotNullWhen(true)] out PgList<T>? list) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(owner);
        var candidate = new PgList<T>();
        bool accepted = candidate.Initialize([], owner, (nint)address, borrow: true);
        list = accepted ? candidate : null;
        return accepted;
    }
}
