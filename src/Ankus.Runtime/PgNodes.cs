using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Creates checked node views over generated PostgreSQL representations and explicit native lifetimes.
/// </summary>
public static class PgNodes
{
    /// <summary>
    /// Allocates a zeroed native node and writes its caller-supplied tag without invoking a constructor.
    /// </summary>
    /// <typeparam name="T">The complete generated native representation.</typeparam>
    /// <param name="tag">The exact tag appropriate for this complete representation.</param>
    /// <param name="context">The allocation owner, or null for the current context.</param>
    /// <returns>An individually owned box, which can transfer ownership to its context.</returns>
    /// <remarks>
    /// The caller must choose a tag whose complete native representation fits T and initialize all
    /// fields required by subsequent native operations. A base node's accepted cast tags do not
    /// make it large enough to allocate its descendants. Pointer fields retain native lifetime obligations.
    /// Native allocator restrictions apply to individual disposal and ownership transfer.
    /// </remarks>
    public static PgNativeBox<T> DangerousAllocate<T>(uint tag, PgMemoryContext? context = null) where T : unmanaged, IPgNativeNode
    {
        nuint alignment = NativeNode.Validate<T>();
        PgAllocation allocation = (context ?? PgMemoryContext.Current).AllocateZeroed<T>(alignment: alignment);
        try
        {
            allocation.Write(tag);
            return new PgNativeBox<T>(allocation);
        }
        catch (Exception primary)
        {
            try
            {
                allocation.Dispose();
            }
            // codeql[cs/catch-of-all-exceptions] A failed cleanup is reported together with the failure that required it.
            catch (Exception cleanup)
            {
                throw new AggregateException("Initializing a PostgreSQL node and releasing its allocation failed.", primary, cleanup);
            }

            throw;
        }
    }

    /// <summary>
    /// Borrows a node view while retaining the reference's original storage bounds and lifetime anchor.
    /// </summary>
    /// <typeparam name="T">The generated native node representation.</typeparam>
    /// <param name="reference">The initialized complete native value to view.</param>
    /// <returns>A node view without allocation or release rights.</returns>
    /// <remarks>
    /// The representation's complete size, alignment and selected-header ABI are checked. The caller
    /// remains responsible for the validity and lifetime of native pointer members. A tag does not
    /// prove that those members refer to valid objects.
    /// </remarks>
    public static PgNodeReference<T> Borrow<T>(PgNativeReference<T> reference) where T : unmanaged, IPgNativeNode
    {
        ArgumentNullException.ThrowIfNull(reference);
        var result = new PgNodeReference<T>(reference);
        result.Validate();
        return result;
    }
}
