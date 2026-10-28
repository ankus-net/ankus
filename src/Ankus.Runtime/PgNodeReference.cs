using System.ComponentModel;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Borrows a generated PostgreSQL node with checked bounds, ABI identity and native lifetime.
/// </summary>
/// <typeparam name="T">The generated unmanaged node view.</typeparam>
/// <remarks>
/// Casts share the original allocation or raw-reference lifetime anchor and never acquire ownership.
/// Pointer members retain their native safety obligations. Writing a tag or a pointer field does not
/// establish that a complete, traversable native object exists at that address.
/// </remarks>
public sealed unsafe class PgNodeReference<T> where T : unmanaged, IPgNativeNode
{
    private readonly PgNativeReference<T> _reference;

    /// <summary>
    /// Retains the original checked representation without recapturing its native lifetime.
    /// </summary>
    /// <param name="reference">The original bounded storage view.</param>
    internal PgNodeReference(PgNativeReference<T> reference) => _reference = reference;

    /// <summary>
    /// Gets or sets a copied complete value after validating this view's ABI, alignment, bounds and lifetime.
    /// </summary>
    public T Value
    {
        get
        {
            Validate();
            return _reference.Value;
        }

        set
        {
            Validate();
            _reference.Value = value;
        }
    }

    /// <summary>
    /// Gets the exact native NodeTag bits without changing the stored value.
    /// </summary>
    public uint Tag
    {
        get
        {
            Validate();
            return _reference.Reinterpret<uint>().Value;
        }
    }

    /// <summary>
    /// Gets the original allocation owner or explicitly supplied raw-reference lifetime anchor.
    /// </summary>
    public PgMemoryContext LifetimeContext
    {
        get
        {
            Validate();
            return _reference.LifetimeContext;
        }
    }

    /// <summary>
    /// Tests the exact native tag, independently of inheritance-based cast acceptance.
    /// </summary>
    /// <param name="tag">The native NodeTag value to compare.</param>
    /// <returns>Whether the stored tag is exactly equal.</returns>
    public bool IsA(uint tag) => Tag == tag;

    /// <summary>
    /// Copies PostgreSQL's native nodeToString output into an independently owned managed string.
    /// </summary>
    /// <returns>The exact native representation converted from the server encoding to Unicode.</returns>
    /// <remarks>
    /// The root's ABI, concrete tag bounds, alignment and lifetime are checked. The caller must
    /// guarantee that all pointer members and variable-length tails form a valid native graph and
    /// remain alive and unchanged throughout native formatting, including reentrant callbacks.
    /// PostgreSQL errors return through the guarded boundary after native temporary storage is reclaimed.
    /// </remarks>
    public string DangerousToNativeString()
    {
        Validate();
        NativeMemoryRequest request = _reference.CreateNodeRequest();
        request._operation = NativeMemoryOperation.FormatNode;
        NativeValue output = default;
        request._data = (nint)(&output);
        try
        {
            NativeMemoryContext.Invoke(ref request, out _);
            return output.ReadString();
        }
        finally
        {
            output.Release();
        }
    }

    /// <summary>
    /// Tries a generated node cast while retaining the complete original storage extent and lifetime.
    /// </summary>
    /// <typeparam name="TTarget">The generated target node representation.</typeparam>
    /// <returns>A shared view, or null when the target's native cast rules reject the stored tag.</returns>
    /// <remarks>
    /// Invalid storage, incompatible ABIs and insufficient target bounds throw; only a rejected tag
    /// returns null. Existing views observe allocation resizing, reset and deletion on every access.
    /// </remarks>
    public PgNodeReference<TTarget>? TryCast<TTarget>() where TTarget : unmanaged, IPgNativeNode
    {
        uint tag = Tag;
        _ = NativeNode.Validate<TTarget>();
        if (!TTarget.AcceptsTag(tag))
        {
            return null;
        }

        return PgNodes.Borrow(_reference.Reinterpret<TTarget>());
    }

    /// <summary>
    /// Returns the current checked address without extending the native storage or pointer-member lifetimes.
    /// </summary>
    /// <returns>The original node address, reflecting any checked allocation resize.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousGetPointer()
    {
        Validate();
        return _reference.DangerousGetPointer();
    }

    /// <summary>
    /// Checks the current provider's ABI and this reference's complete storage and alignment.
    /// </summary>
    internal void Validate()
    {
        nuint alignment = NativeNode.Validate<T>();
        NativeNode.CheckAlignment(_reference.DangerousGetPointer(), alignment);
    }
}
