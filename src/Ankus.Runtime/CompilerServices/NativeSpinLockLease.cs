using System.Runtime.CompilerServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Owns one stable native spinlock address until its checked acquisition is released.
/// </summary>
internal sealed unsafe class NativeSpinLockLease(nint address, nint data, nint frame, object? owner) : IDisposable
{
    /// <summary>
    /// Identifies initialized cells; the low bit denotes selected-header state-query support.
    /// </summary>
    internal const long Format = 0x414E4B5350494E00;

    [ThreadStatic]
    private static NativeSpinLockLease? s_active;

    private readonly NativeBorrowScope _scope = NativeMemoryContext.BorrowScope;
    private readonly int _process = Environment.ProcessId;
    private readonly nint _address = address;
    private readonly nint _frame = frame;
    private object? _owner = owner;
    private NativeSpinLockLease? _previous;
    private NativeSpinLockLease? _next;
    private delegate* unmanaged[Cdecl]<nint, void> _release;
    private bool _held;
    private int _readDepth;
    private bool _mutating;

    /// <summary>
    /// Initializes an unlocked cell using the selected PostgreSQL headers.
    /// </summary>
    internal static long Initialize(nint address)
    {
        CheckBackendAccess();
        NativeMemoryResult result = Invoke(address, 0);
        if (result._value is not (0 or 1))
        {
            throw new InvalidOperationException("PostgreSQL returned an invalid spinlock format.");
        }

        return Format | result._value;
    }

    /// <summary>
    /// Queries lock state through SpinLockFree, leaving the native lock bytes opaque.
    /// </summary>
    internal static bool Query(nint address) => Invoke(address, 2)._value != 0;

    /// <summary>
    /// Prevents PostgreSQL calls and interrupt checks within a spinlock critical section.
    /// </summary>
    internal static void CheckBackendAccess()
    {
        if (s_active is not null)
        {
            throw new InvalidOperationException("Release PostgreSQL spinlock guards before calling backend APIs.");
        }
    }

    /// <summary>
    /// Releases every guard borrowed from this read before its native admission expires.
    /// </summary>
    internal static void ReleaseFrame(nint frame)
    {
        NativeSpinLockLease? current = s_active;
        while (current is not null)
        {
            NativeSpinLockLease? previous = current._previous;
            if (current._frame == frame)
            {
                current.Expire();
            }

            current = previous;
        }
    }

    /// <summary>
    /// Prepares ownership and the release entry point before entering the native critical section.
    /// </summary>
    internal void Acquire()
    {
        for (NativeSpinLockLease? current = s_active; current is not null; current = current._previous)
        {
            if (current._address == _address)
            {
                throw new InvalidOperationException("Recursive acquisition of a PostgreSQL spinlock is not supported.");
            }
        }

        _scope.Register(this);
        try
        {
            NativeMemoryResult prepared = Invoke(_address, 3);
            if (prepared._pointer == 0)
            {
                throw new InvalidOperationException("PostgreSQL did not return the spinlock release entry point.");
            }

            _release = (delegate* unmanaged[Cdecl]<nint, void>)prepared._pointer;
            _ = Invoke(_address, 1);
            _held = true;
            Push(this);
        }
        catch
        {
            _scope.Unregister(this);
            throw;
        }
    }

    /// <summary>
    /// Copies the protected value without making a backend call while the lock is held.
    /// </summary>
    internal T Read<T>() where T : unmanaged
    {
        Validate();
        T value = Unsafe.ReadUnaligned<T>((void*)data);
        GC.KeepAlive(_owner);
        return value;
    }

    /// <summary>
    /// Reads original protected storage while preventing replacement or release through guard aliases.
    /// </summary>
    internal TResult Read<T, TResult>(PgSharedReader<T, TResult> reader) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(reader);
        Validate();
        CheckNoMutation();
        _readDepth = checked(_readDepth + 1);
        NativeSharedReadScope frame = default;
        NativeSharedReadScope.Push(&frame, NativeMemoryContext.Provider, data, (nuint)sizeof(T));
        try
        {
            return reader(in Unsafe.AsRef<T>((void*)data));
        }
        finally
        {
            try
            {
                NativeSharedReadScope.Pop(&frame);
            }
            finally
            {
                _readDepth--;
                GC.KeepAlive(_owner);
            }
        }
    }

    /// <summary>
    /// Mutates original protected storage without permitting aliases or nested locks to retain conflicting access.
    /// </summary>
    internal TResult Mutate<T, TResult>(PgSharedMutator<T, TResult> mutator) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(mutator);
        Validate();
        CheckNoBorrowers();
        _mutating = true;
        NativeSharedReadScope frame = default;
        NativeSharedReadScope.PushMutation(&frame, NativeMemoryContext.Provider, data, (nuint)sizeof(T));
        try
        {
            return mutator(ref Unsafe.AsRef<T>((void*)data));
        }
        finally
        {
            try
            {
                NativeSharedReadScope.Pop(&frame);
            }
            finally
            {
                _mutating = false;
                GC.KeepAlive(_owner);
            }
        }
    }

    /// <summary>
    /// Immediately publishes the protected value without allocation or backend entry.
    /// </summary>
    internal void Write<T>(T value) where T : unmanaged
    {
        Validate();
        CheckNoBorrowers();
        Unsafe.WriteUnaligned((void*)data, value);
        GC.KeepAlive(_owner);
    }

    /// <summary>
    /// Releases once through the prepared nonthrowing native function before dropping owned roots.
    /// </summary>
    public void Dispose()
    {
        if (!_held)
        {
            return;
        }

        Validate();
        CheckNoBorrowers();
        Expire();
    }

    /// <summary>
    /// Preserves original storage and lock ownership until every active borrower has returned.
    /// </summary>
    private void CheckNoBorrowers()
    {
        CheckNoMutation();
        if (_readDepth != 0)
        {
            throw new InvalidOperationException("A PostgreSQL spinlock guard cannot replace or release its value during a scoped read.");
        }
    }

    /// <summary>
    /// Rejects overlapping guard operations while a callback can mutate original storage.
    /// </summary>
    private void CheckNoMutation()
    {
        if (_mutating)
        {
            throw new InvalidOperationException("Finish the PostgreSQL spinlock mutation before borrowing, replacing or releasing its value.");
        }
    }

    /// <summary>
    /// Makes a newly held lease the newest on this thread. The thread's held leases form one list, so it changes only in
    /// static methods.
    /// </summary>
    private static void Push(NativeSpinLockLease lease)
    {
        lease._previous = s_active;
        if (s_active is not null)
        {
            s_active._next = lease;
        }

        s_active = lease;
    }

    /// <summary>
    /// Makes <paramref name="lease"/> the newest lease this thread holds, or records that it holds none.
    /// </summary>
    private static void SetActive(NativeSpinLockLease? lease) => s_active = lease;

    /// <summary>
    /// Releases trusted scope-owned storage even if its callback is restoring a nested capability.
    /// </summary>
    internal void Expire()
    {
        if (!_held)
        {
            return;
        }

        _release(_address);
        GC.KeepAlive(_owner);
        _held = false;
        _owner = null;
        if (_next is null)
        {
            SetActive(_previous);
        }
        else
        {
            _next._previous = _previous;
        }

        if (_previous is not null)
        {
            _previous._next = _next;
        }

        _previous = null;
        _next = null;
        _scope.Unregister(this);
    }

    /// <summary>
    /// Rejects stale, foreign-thread, foreign-provider and inherited-process guard access.
    /// </summary>
    private void Validate()
    {
        ObjectDisposedException.ThrowIf(!_held, this);
        _scope.Validate();
        if (_process != Environment.ProcessId)
        {
            throw new InvalidOperationException("A PostgreSQL spinlock guard belongs to its acquiring backend process.");
        }
    }

    /// <summary>
    /// Calls the guarded native protocol only for operations on validated stable storage.
    /// </summary>
    private static NativeMemoryResult Invoke(nint address, int operation)
    {
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.SpinLock,
            _flags = operation,
            _pointer = address,
            _length = sizeof(long),
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        return result;
    }
}
