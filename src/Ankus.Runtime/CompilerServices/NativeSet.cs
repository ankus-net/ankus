using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Roots generated function iterators across PostgreSQL calls and releases their managed ownership.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class NativeSet
{
    /// <summary>
    /// Acquires an iterator and roots it until native execution disposes the handle.
    /// </summary>
    /// <typeparam name="T">The managed row type.</typeparam>
    /// <param name="source">The sequence, or null for an empty set.</param>
    /// <returns>An owned iterator handle, or zero for a null sequence.</returns>
    public static nint Create<T>(IEnumerable<T>? source)
    {
        if (source is null)
        {
            return 0;
        }

        IEnumerator<T> iterator = source.GetEnumerator()
            ?? throw new InvalidOperationException("A set-returning sequence returned a null iterator.");
        try
        {
            return GCHandle.ToIntPtr(GCHandle.Alloc(iterator));
        }
        catch
        {
            iterator.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Acquires an iterator together with converted arguments that must remain live until iterator disposal.
    /// </summary>
    /// <typeparam name="T">The managed row type.</typeparam>
    /// <param name="source">The sequence, or null for an empty set.</param>
    /// <param name="arguments">The transferred argument ownership, released on every failed or completed creation path.</param>
    /// <returns>The rooted iterator, or zero after releasing arguments for a null sequence.</returns>
    public static nint Create<T>(IEnumerable<T>? source, IDisposable arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (source is null)
        {
            arguments.Dispose();
            return 0;
        }

        IEnumerator<T>? iterator = null;
        try
        {
            iterator = source.GetEnumerator()
                ?? throw new InvalidOperationException("A set-returning sequence returned a null iterator.");
            var owned = new OwnedIterator<T>(iterator, arguments);
            return GCHandle.ToIntPtr(GCHandle.Alloc(owned));
        }
        catch
        {
            using (arguments)
            {
                iterator?.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Advances a rooted iterator and reads its current row without changing ownership.
    /// </summary>
    /// <typeparam name="T">The managed row type.</typeparam>
    /// <param name="handle">The iterator handle.</param>
    /// <param name="value">The next row, or the default value after completion.</param>
    /// <returns>Whether a row was produced.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// A nonzero handle must be the live iterator root returned by Create; do not reuse it after disposal.
    /// </remarks>
    [NativeUnsafeAccess]
    public static bool MoveNext<T>(nint handle, out T value)
    {
        if (handle != 0 && GCHandle.FromIntPtr(handle).Target is IEnumerator<T> iterator && iterator.MoveNext())
        {
            value = iterator.Current;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Clears and frees a handle before disposing its iterator, including when disposal throws.
    /// </summary>
    /// <param name="handle">The owned handle, cleared before invoking user cleanup.</param>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// A nonzero handle must be the live iterator root returned by Create; do not reuse it after disposal.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void Dispose(ref nint handle)
    {
        if (handle == 0)
        {
            return;
        }

        GCHandle owner = GCHandle.FromIntPtr(handle);
        handle = 0;
        IDisposable iterator = (IDisposable)owner.Target!;
        owner.Free();
        iterator.Dispose();
    }

    /// <summary>
    /// Finds the finite relation argument scope retained by a generated iterator without examining user iterator fields.
    /// </summary>
    internal static NativeRelationScope? RelationArguments(nint handle)
        => handle != 0 && GCHandle.FromIntPtr(handle).Target is IOwnedIterator iterator
            ? iterator.Arguments as NativeRelationScope : null;

    /// <summary>
    /// Exposes explicit argument ownership independently of the closed iterator element type.
    /// </summary>
    private interface IOwnedIterator
    {
        /// <summary>
        /// Gets the transferred argument owner.
        /// </summary>
        IDisposable Arguments { get; }
    }

    /// <summary>
    /// Retains argument ownership until after user iterator finally blocks, including when those blocks throw.
    /// </summary>
    private sealed class OwnedIterator<T>(IEnumerator<T> iterator, IDisposable arguments) : IEnumerator<T>, IOwnedIterator
    {
        /// <summary>
        /// Gets the resources retained until iterator disposal.
        /// </summary>
        public IDisposable Arguments => arguments;

        /// <summary>
        /// Gets the current user value without changing ownership.
        /// </summary>
        public T Current => iterator.Current;

        object? System.Collections.IEnumerator.Current => Current;

        /// <summary>
        /// Advances the user iterator.
        /// </summary>
        public bool MoveNext() => iterator.MoveNext();

        /// <summary>
        /// Delegates reset to the user iterator.
        /// </summary>
        public void Reset() => iterator.Reset();

        /// <summary>
        /// Disposes the iterator before releasing its arguments.
        /// </summary>
        public void Dispose()
        {
            using (arguments)
            {
                iterator.Dispose();
            }
        }
    }
}
