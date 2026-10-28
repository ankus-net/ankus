using System;
using System.ComponentModel;

namespace Ankus;

/// <summary>
/// Individually owns one unmanaged value in a checked PostgreSQL allocation.
/// </summary>
/// <typeparam name="T">The unmanaged representation copied to and from native storage.</typeparam>
/// <remarks>
/// Disposal frees the native allocation without invoking a constructor or destructor on its value.
/// There is no finalizer. The unmanaged constraint does not establish a C ABI layout or SQL type.
/// Native allocator restrictions apply: Bump storage rejects individual disposal and remains live
/// until context cleanup. Use context-owned values when individual release is unavailable.
/// </remarks>
public sealed unsafe class PgNativeBox<T> : IDisposable where T : unmanaged
{
    private PgAllocation? _allocation;

    /// <summary>
    /// Takes exclusive managed release rights for an existing checked allocation.
    /// </summary>
    /// <param name="allocation">The allocation containing the complete value.</param>
    internal PgNativeBox(PgAllocation allocation) => _allocation = allocation;

    /// <summary>
    /// Gets or sets a copied unmanaged value after validating the allocation's native lifetime.
    /// </summary>
    public T Value
    {
        get => Allocation.Read<T>();
        set => Allocation.Write(value);
    }

    /// <summary>
    /// Gets the allocation's actual native owner while the allocation remains live.
    /// </summary>
    public PgMemoryContext Context => Allocation.Context;

    /// <summary>
    /// Gets the original initialization and native size policies.
    /// </summary>
    public PgAllocationOptions Options => Allocation.Options;

    /// <summary>
    /// Gets the requested explicit alignment, or zero for the server default.
    /// </summary>
    public nuint Alignment => Allocation.Alignment;

    /// <summary>
    /// Borrows a checked typed view without transferring ownership.
    /// </summary>
    /// <returns>The view sharing this allocation's checked lifetime.</returns>
    public PgNativeReference<T> Borrow() => Allocation.Borrow<T>();

    /// <summary>
    /// Transfers individual release rights to the native context while retaining checked storage and existing views.
    /// </summary>
    /// <returns>The context-owned value; this owning wrapper becomes consumed.</returns>
    public PgContextValue<T> ReleaseToContext()
    {
        PgAllocation allocation = Allocation;
        allocation.ValidateAccess(0, (nuint)sizeof(T));
        var value = new PgContextValue<T>(allocation);
        _allocation = null;
        return value;
    }

    /// <summary>
    /// Copies all representation bytes into independent context-owned storage.
    /// </summary>
    /// <param name="context">The destination context, or null for the current context.</param>
    /// <returns>A shallow context-owned copy with default allocation policies.</returns>
    public PgContextValue<T> CloneInto(PgMemoryContext? context = null) => Borrow().CloneInto(context);

    /// <summary>
    /// Copies all representation bytes into independent individually owned storage.
    /// </summary>
    /// <param name="context">The destination context, or null for the current context.</param>
    /// <returns>A shallow individually owned copy with default allocation policies.</returns>
    public PgNativeBox<T> CloneOwnedInto(PgMemoryContext? context = null) => Borrow().CloneOwnedInto(context);

    /// <summary>
    /// Returns the current native address for explicitly unsafe interop.
    /// </summary>
    /// <returns>The borrowed address, invalid after native reclamation or ownership transfer.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousGetPointer() => Allocation.DangerousGetPointer();

    /// <summary>
    /// Transfers the native pointer without freeing it and invalidates all shared checked views.
    /// </summary>
    /// <returns>The native pointer now owned by its recipient or native context.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousDetach()
    {
        void* address = Allocation.DangerousDetach();
        _allocation = null;
        return address;
    }

    /// <summary>
    /// Releases the native storage once; a failed native free retains release rights for retry.
    /// </summary>
    public void Dispose()
    {
        if (_allocation is null)
        {
            return;
        }

        _allocation.Dispose();
        _allocation = null;
    }

    private PgAllocation Allocation
    {
        get
        {
            ObjectDisposedException.ThrowIf(_allocation is null, nameof(PgNativeBox<>));
            return _allocation;
        }
    }
}
