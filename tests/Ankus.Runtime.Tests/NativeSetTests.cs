using System.Collections;
using System.Runtime.CompilerServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks set iterator ownership, exact row values, failures, and managed rooting across native calls.
/// </summary>
[TestClass]
public sealed class NativeSetTests
{
    /// <summary>
    /// A null sequence has no owned iterator, and repeated reads and cleanup retain the empty handle.
    /// </summary>
    [TestMethod]
    public void NullSourceHasNoHandleAndReturnsDefault()
    {
        nint handle = NativeSet.Create<int?>(null);
        Assert.AreEqual(nint.Zero, handle);
        Assert.IsFalse(NativeSet.MoveNext(handle, out int? value));
        Assert.IsNull(value);
        NativeSet.Dispose(ref handle);
        NativeSet.Dispose(ref handle);
        Assert.AreEqual(nint.Zero, handle);
        Assert.IsFalse(NativeSet.MoveNext(handle, out int required));
        Assert.AreEqual(0, required);
    }

    /// <summary>
    /// Transferred arguments are released even when no usable iterator can be created.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void TransferredArgumentsReleaseOnCreationFailure(int failure)
    {
        var owner = new ObservedOwner();
        var sequence = new ObservedSequence<int>([42])
        {
            ReturnNullEnumerator = failure == 1,
            CreationError = failure == 2 ? new InvalidOperationException("factory") : null,
        };
        nint handle = 0;
        if (failure == 0)
        {
            handle = NativeSet.Create<int>(null, owner);
        }
        else
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => handle = NativeSet.Create(sequence, owner));
        }

        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, owner.Disposals);
        Assert.AreEqual(0, sequence.DisposeCount);
        Assert.AreEqual(0, sequence.MoveNextCount);
    }

    /// <summary>
    /// Arguments remain live during iterator disposal and are released even when the iterator's finally block throws.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TransferredArgumentsOutliveIteratorCleanup(bool fail)
    {
        var owner = new ObservedOwner();
        var sequence = new ObservedSequence<int>([42])
        {
            OnDispose = () => Assert.AreEqual(0, owner.Disposals),
            DisposeError = fail ? new InvalidOperationException("finally") : null,
        };
        nint handle = NativeSet.Create(sequence, owner);
        Assert.IsTrue(NativeSet.MoveNext(handle, out int value));
        Assert.AreEqual(42, value);
        Assert.AreEqual(0, owner.Disposals);
        if (fail)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSet.Dispose(ref handle));
        }
        else
        {
            NativeSet.Dispose(ref handle);
        }

        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, owner.Disposals);
        Assert.AreEqual(1, sequence.DisposeCount);
        NativeSet.Dispose(ref handle);
        Assert.AreEqual(1, owner.Disposals);
    }

    /// <summary>
    /// Records the lifetime of resources transferred into an iterator owner.
    /// </summary>
    private sealed class ObservedOwner : IDisposable
    {
        /// <summary>
        /// Gets the exact number of release calls.
        /// </summary>
        internal int Disposals
        {
            get;
            private set;
        }

        /// <inheritdoc />
        public void Dispose() => Disposals++;
    }

    /// <summary>
    /// Empty sequences still own an iterator, and neither creation nor exhaustion disposes it.
    /// </summary>
    [TestMethod]
    public void EmptySequenceOwnsAnIteratorUntilExplicitDisposal()
    {
        var sequence = new ObservedSequence<int>([]);
        nint handle = NativeSet.Create(sequence);
        try
        {
            Assert.AreNotEqual(nint.Zero, handle);
            Assert.AreEqual(1, sequence.EnumeratorCount);
            Assert.AreEqual(0, sequence.MoveNextCount);
            Assert.AreEqual(0, sequence.CurrentReadCount);
            Assert.IsFalse(NativeSet.MoveNext(handle, out int value));
            Assert.AreEqual(0, value);
            Assert.IsFalse(NativeSet.MoveNext(handle, out value));
            Assert.AreEqual(2, sequence.MoveNextCount);
            Assert.AreEqual(0, sequence.CurrentReadCount);
            Assert.AreEqual(0, sequence.DisposeCount);
            Assert.AreNotEqual(nint.Zero, handle);
        }
        finally
        {
            NativeSet.Dispose(ref handle);
        }

        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, sequence.DisposeCount);
        NativeSet.Dispose(ref handle);
        Assert.AreEqual(1, sequence.DisposeCount);
    }

    /// <summary>
    /// A singleton SQL NULL row is distinguishable from completion even though both output values are null.
    /// </summary>
    [TestMethod]
    public void SingletonNullIsARowBeforeCompletion()
    {
        var sequence = new ObservedSequence<int?>([null]);
        nint handle = NativeSet.Create(sequence);
        try
        {
            Assert.IsTrue(NativeSet.MoveNext(handle, out int? value));
            Assert.IsNull(value);
            Assert.AreEqual(1, sequence.MoveNextCount);
            Assert.AreEqual(1, sequence.CurrentReadCount);
            Assert.IsFalse(NativeSet.MoveNext(handle, out value));
            Assert.IsNull(value);
            Assert.AreEqual(2, sequence.MoveNextCount);
            Assert.AreEqual(1, sequence.CurrentReadCount);
            Assert.AreEqual(0, sequence.DisposeCount);
        }
        finally
        {
            NativeSet.Dispose(ref handle);
        }

        Assert.AreEqual(1, sequence.DisposeCount);
    }

    /// <summary>
    /// Each successful advance returns exactly one value, retaining order, zero, and nullable elements.
    /// </summary>
    [TestMethod]
    public void MultipleRowsPreserveValuesAndReadCurrentOncePerRow()
    {
        var sequence = new ObservedSequence<int?>([0, null, int.MinValue, int.MaxValue]);
        nint handle = NativeSet.Create(sequence);
        try
        {
            Assert.IsTrue(NativeSet.MoveNext(handle, out int? value));
            Assert.AreEqual(0, value);
            Assert.IsTrue(NativeSet.MoveNext(handle, out value));
            Assert.IsNull(value);
            Assert.IsTrue(NativeSet.MoveNext(handle, out value));
            Assert.AreEqual(int.MinValue, value);
            Assert.IsTrue(NativeSet.MoveNext(handle, out value));
            Assert.AreEqual(int.MaxValue, value);
            Assert.AreEqual(4, sequence.MoveNextCount);
            Assert.AreEqual(4, sequence.CurrentReadCount);
            Assert.IsFalse(NativeSet.MoveNext(handle, out value));
            Assert.IsNull(value);
            Assert.AreEqual(5, sequence.MoveNextCount);
            Assert.AreEqual(4, sequence.CurrentReadCount);
            Assert.AreEqual(0, sequence.DisposeCount);
        }
        finally
        {
            NativeSet.Dispose(ref handle);
        }

        Assert.AreEqual(1, sequence.DisposeCount);
    }

    /// <summary>
    /// An iterator factory failure preserves the original exception and creates no cleanup obligation.
    /// </summary>
    [TestMethod]
    public void GetEnumeratorFailurePropagatesWithoutAdvancingOrDisposing()
    {
        var expected = new InvalidOperationException("iterator factory failure");
        var sequence = new ObservedSequence<int>([1]) { CreationError = expected };
        nint handle = 0;
        InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() => handle = NativeSet.Create(sequence));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, sequence.EnumeratorCount);
        Assert.AreEqual(0, sequence.MoveNextCount);
        Assert.AreEqual(0, sequence.CurrentReadCount);
        Assert.AreEqual(0, sequence.DisposeCount);
    }

    /// <summary>
    /// A broken iterator factory cannot turn a null enumerator into an allocated native handle.
    /// </summary>
    [TestMethod]
    public void NullEnumeratorIsRejectedAtCreation()
    {
        var sequence = new ObservedSequence<int>([1]) { ReturnNullEnumerator = true };
        nint handle = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => handle = NativeSet.Create(sequence));
        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, sequence.EnumeratorCount);
        Assert.AreEqual(0, sequence.MoveNextCount);
        Assert.AreEqual(0, sequence.CurrentReadCount);
        Assert.AreEqual(0, sequence.DisposeCount);
    }

    /// <summary>
    /// Advancing failures preserve earlier rows and leave the native owner responsible for cleanup.
    /// </summary>
    [TestMethod]
    public void MoveNextFailurePreservesTheExceptionAndOwnedHandle()
    {
        var expected = new InvalidOperationException("advance failure");
        var sequence = new ObservedSequence<int>([41, 42]) { MoveNextError = expected, FailMoveNextOnCall = 2 };
        nint handle = NativeSet.Create(sequence);
        nint originalHandle = handle;
        try
        {
            Assert.IsTrue(NativeSet.MoveNext(handle, out int value));
            Assert.AreEqual(41, value);
            InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() => NativeSet.MoveNext<int>(handle, out _));
            Assert.AreSame(expected, actual);
            Assert.AreEqual(originalHandle, handle);
            Assert.AreEqual(2, sequence.MoveNextCount);
            Assert.AreEqual(1, sequence.CurrentReadCount);
            Assert.AreEqual(0, sequence.DisposeCount);
        }
        finally
        {
            NativeSet.Dispose(ref handle);
        }

        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, sequence.DisposeCount);
    }

    /// <summary>
    /// Current failures propagate after the successful advance without disposing the native owner's iterator.
    /// </summary>
    [TestMethod]
    public void CurrentFailurePreservesTheExceptionAndOwnedHandle()
    {
        var expected = new InvalidOperationException("current failure");
        var sequence = new ObservedSequence<int>([42]) { CurrentError = expected };
        nint handle = NativeSet.Create(sequence);
        nint originalHandle = handle;
        try
        {
            InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() => NativeSet.MoveNext<int>(handle, out _));
            Assert.AreSame(expected, actual);
            Assert.AreEqual(originalHandle, handle);
            Assert.AreEqual(1, sequence.MoveNextCount);
            Assert.AreEqual(1, sequence.CurrentReadCount);
            Assert.AreEqual(0, sequence.DisposeCount);
        }
        finally
        {
            NativeSet.Dispose(ref handle);
        }

        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, sequence.DisposeCount);
    }

    /// <summary>
    /// Early cleanup clears the caller's handle before invoking user disposal and remains idempotent.
    /// </summary>
    [TestMethod]
    public void EarlyDisposalClearsTheHandleBeforeCallingUserCode()
    {
        nint handle = 0;
        var sequence = new ObservedSequence<int>([1, 2])
        {
            OnDispose = () => Assert.AreEqual(nint.Zero, handle),
        };

        handle = NativeSet.Create(sequence);
        try
        {
            Assert.IsTrue(NativeSet.MoveNext(handle, out int value));
            Assert.AreEqual(1, value);
        }
        finally
        {
            NativeSet.Dispose(ref handle);
        }

        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, sequence.DisposeCount);
        NativeSet.Dispose(ref handle);
        Assert.AreEqual(1, sequence.DisposeCount);
        Assert.IsFalse(NativeSet.MoveNext(handle, out int afterDisposal));
        Assert.AreEqual(0, afterDisposal);
        Assert.AreEqual(1, sequence.MoveNextCount);
    }

    /// <summary>
    /// Throwing disposal retains its exact exception after clearing ownership, so cleanup cannot run twice.
    /// </summary>
    [TestMethod]
    public void DisposalFailureClearsTheHandleAndRunsExactlyOnce()
    {
        var expected = new InvalidOperationException("dispose failure");
        nint handle = 0;
        var sequence = new ObservedSequence<int>([1])
        {
            DisposeError = expected,
            OnDispose = () => Assert.AreEqual(nint.Zero, handle),
        };

        handle = NativeSet.Create(sequence);
        InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() => NativeSet.Dispose(ref handle));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(nint.Zero, handle);
        Assert.AreEqual(1, sequence.DisposeCount);
        Assert.AreEqual(0, sequence.MoveNextCount);
        NativeSet.Dispose(ref handle);
        Assert.AreEqual(1, sequence.DisposeCount);
    }

    /// <summary>
    /// A native handle roots iterator state through collection and releases it after ordinary cleanup.
    /// </summary>
    [TestMethod]
    public void HandleRootsIteratorValuesUntilDisposal()
    {
        (nint handle, WeakReference<RetainedValue> lifetime) = CreateRootedHandle(false);
        try
        {
            CollectUnreferencedValues();
            Assert.IsTrue(IsAlive(lifetime));
            Assert.AreEqual(42, ReadRetainedValue(handle));
            CollectUnreferencedValues();
            Assert.IsTrue(IsAlive(lifetime));
        }
        finally
        {
            NativeSet.Dispose(ref handle);
        }

        CollectUnreferencedValues();
        Assert.IsFalse(IsAlive(lifetime));
        Assert.AreEqual(nint.Zero, handle);
    }

    /// <summary>
    /// User disposal exceptions cannot leak the GC handle or keep iterator-owned objects alive.
    /// </summary>
    [TestMethod]
    public void DisposalFailureStillReleasesTheManagedRoot()
    {
        (nint handle, WeakReference<RetainedValue> lifetime) = CreateRootedHandle(true);
        CollectUnreferencedValues();
        Assert.IsTrue(IsAlive(lifetime));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeSet.Dispose(ref handle));
        Assert.AreEqual(nint.Zero, handle);
        CollectUnreferencedValues();
        Assert.IsFalse(IsAlive(lifetime));
        NativeSet.Dispose(ref handle);
    }

    /// <summary>
    /// Keeps factory locals outside the collecting caller's stack so only the native handle roots the value.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (nint Handle, WeakReference<RetainedValue> Lifetime) CreateRootedHandle(bool failDisposal)
    {
        var value = new RetainedValue(42);
        var sequence = new ObservedSequence<RetainedValue>([value])
        {
            DisposeError = failDisposal ? new InvalidOperationException("dispose failure") : null,
        };

        return (NativeSet.Create(sequence), new WeakReference<RetainedValue>(value));
    }

    /// <summary>
    /// Materializes only the payload integer, preventing test locals from extending the object's lifetime.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int ReadRetainedValue(nint handle)
    {
        Assert.IsTrue(NativeSet.MoveNext(handle, out RetainedValue value));
        return value.Value;
    }

    /// <summary>
    /// Observes liveness without retaining a strong target reference in the collecting test method.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<RetainedValue> reference) => reference.TryGetTarget(out _);

    /// <summary>
    /// Completes collection and finalization before testing handle-owned object reachability.
    /// </summary>
    private static void CollectUnreferencedValues()
    {
        GarbageCollection.Collect();
    }

    /// <summary>
    /// Supplies a reference payload whose lifetime can be observed independently of its value.
    /// </summary>
    private sealed class RetainedValue(int value)
    {
        /// <summary>
        /// Gets the payload expected after a collection between native calls.
        /// </summary>
        internal int Value { get; } = value;
    }

    /// <summary>
    /// Records iterator lifecycle calls and supplies failures at separately observable boundaries.
    /// </summary>
    /// <typeparam name="T">The row type.</typeparam>
    private sealed class ObservedSequence<T>(T[] values) : IEnumerable<T>
    {
        /// <summary>
        /// Gets the number of iterator factory calls.
        /// </summary>
        internal int EnumeratorCount
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the number of attempted advances.
        /// </summary>
        internal int MoveNextCount
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the number of attempted current-value reads.
        /// </summary>
        internal int CurrentReadCount
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the number of disposal calls, including calls that throw.
        /// </summary>
        internal int DisposeCount
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets an optional exception thrown while creating the enumerator.
        /// </summary>
        internal Exception? CreationError
        {
            get;
            init;
        }

        /// <summary>
        /// Gets whether the iterator factory violates its nonnull result contract.
        /// </summary>
        internal bool ReturnNullEnumerator
        {
            get;
            init;
        }

        /// <summary>
        /// Gets an optional exception thrown by the selected advance call.
        /// </summary>
        internal Exception? MoveNextError
        {
            get;
            init;
        }

        /// <summary>
        /// Gets the one-based advance call that throws the configured exception.
        /// </summary>
        internal int FailMoveNextOnCall
        {
            get;
            init;
        } = 1;

        /// <summary>
        /// Gets an optional exception thrown while reading the current row.
        /// </summary>
        internal Exception? CurrentError
        {
            get;
            init;
        }

        /// <summary>
        /// Gets an optional exception thrown after observing disposal.
        /// </summary>
        internal Exception? DisposeError
        {
            get;
            init;
        }

        /// <summary>
        /// Gets an observation of the caller's state from inside user disposal code.
        /// </summary>
        internal Action? OnDispose
        {
            get;
            init;
        }

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator()
        {
            EnumeratorCount++;
            if (CreationError is not null)
            {
                throw CreationError;
            }

            return ReturnNullEnumerator ? null! : new ObservedEnumerator(this, values);
        }

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// Retains rows and reports each user-code operation to its source sequence.
        /// </summary>
        private sealed class ObservedEnumerator(ObservedSequence<T> owner, T[] values) : IEnumerator<T>
        {
            private int _index = -1;

            /// <inheritdoc />
            public T Current
            {
                get
                {
                    owner.CurrentReadCount++;
                    if (owner.CurrentError is not null)
                    {
                        throw owner.CurrentError;
                    }

                    return values[_index];
                }
            }

            /// <inheritdoc />
            object? IEnumerator.Current => Current;

            /// <inheritdoc />
            public bool MoveNext()
            {
                owner.MoveNextCount++;
                if (owner.MoveNextError is not null && owner.MoveNextCount == owner.FailMoveNextOnCall)
                {
                    throw owner.MoveNextError;
                }

                return ++_index < values.Length;
            }

            /// <inheritdoc />
            public void Dispose()
            {
                owner.DisposeCount++;
                owner.OnDispose?.Invoke();
                if (owner.DisposeError is not null)
                {
                    throw owner.DisposeError;
                }
            }

            /// <inheritdoc />
            public void Reset() => throw new NotSupportedException();
        }
    }
}
