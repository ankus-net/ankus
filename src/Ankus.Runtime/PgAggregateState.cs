using System;
using System.Threading;

namespace Ankus;

/// <summary>
/// Owns a nonnull managed aggregate payload until its native memory context resets.
/// New unattached wrappers support ordinary managed use; attached wrappers belong to their backend thread.
/// </summary>
/// <typeparam name="T">The managed payload type, optionally implementing IDisposable for exactly-once cleanup.</typeparam>
public sealed class PgAggregateState<T> : IAggregateState where T : notnull
{
    private T? _value;
    private nint _owner;
    private nint _pointer;
    private int _threadId;
    private bool _released;

    /// <summary>
    /// Takes ownership of a nonnull payload without attaching it to PostgreSQL until returned by an aggregate callback.
    /// </summary>
    /// <param name="value">The payload owned by this wrapper.</param>
    public PgAggregateState(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>
    /// Gets the payload while its state remains live, requiring the owning thread after native attachment.
    /// The payload is invalidated before disposal when the native owner resets, including error cleanup.
    /// </summary>
    public T Value
    {
        get
        {
            CheckAccess();
            return _value!;
        }
    }

    /// <inheritdoc />
    nint IAggregateState.Owner => _owner;

    /// <inheritdoc />
    nint IAggregateState.Pointer => _pointer;

    /// <inheritdoc />
    void IAggregateState.CheckAccess() => CheckAccess();

    /// <inheritdoc />
    void IAggregateState.BeginAttachment(nint owner)
    {
        CheckAccess();
        if (_owner != 0 || Interlocked.CompareExchange(ref _threadId, Environment.CurrentManagedThreadId, 0) != 0)
        {
            throw new InvalidOperationException("Aggregate state is already attached to a native owner.");
        }

        _owner = owner;
    }

    /// <inheritdoc />
    void IAggregateState.CompleteAttachment(nint pointer)
    {
        CheckAccess();
        _pointer = pointer;
    }

    /// <inheritdoc />
    void IAggregateState.Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        T value = _value!;
        _value = default;
        _owner = 0;
        _pointer = 0;
        if (value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private void CheckAccess()
    {
        ObjectDisposedException.ThrowIf(_released, this);
        if (_threadId != 0 && _threadId != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("Attached aggregate state belongs to its PostgreSQL backend thread.");
        }
    }
}
