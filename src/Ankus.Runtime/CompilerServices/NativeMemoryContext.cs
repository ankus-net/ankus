using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Binds PostgreSQL memory-context operations to one generated native callback.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class NativeMemoryContext
{
    [ThreadStatic]
    private static nint s_api;

    [ThreadStatic]
    private static int s_depth;

    [ThreadStatic]
    private static NativeBorrowScope? s_borrowScope;

    /// <summary>
    /// Enters the native memory capability supplied by a generated callback.
    /// </summary>
    /// <param name="api">The callback-scoped native memory envelope.</param>
    /// <returns>The enclosing envelope, restored by <see cref="Exit"/>.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use the matching dispatcher's live binding and restore the exact previous entry in reverse order.
    /// </remarks>
    [NativeUnsafeAccess]
    public static nint Enter(nint api)
    {
        nint previous = s_api;
        s_api = api;
        s_depth++;
        return previous;
    }

    /// <summary>
    /// Restores the enclosing native memory capability.
    /// </summary>
    /// <param name="previous">The value returned by <see cref="Enter"/>.</param>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use the matching dispatcher's live binding and restore the exact previous entry in reverse order.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void Exit(nint previous)
    {
        if (s_depth <= 0)
        {
            throw new InvalidOperationException("The PostgreSQL memory capability stack is unbalanced.");
        }

        if (s_borrowScope is { } scope && scope.Depth == s_depth)
        {
            scope.Expire();
            s_borrowScope = scope.Parent;
        }

        s_api = previous;
        s_depth--;
    }

    /// <summary>
    /// Gets the current native capability provider identity.
    /// </summary>
    internal static nint Provider
    {
        get
        {
            if (s_api == 0)
            {
                throw new InvalidOperationException("PostgreSQL memory contexts require an active backend callback.");
            }

            return ((NativeMemoryApi*)s_api)->_provider;
        }
    }

    /// <summary>
    /// Lazily creates a unique lease for buffers retained until this callback exits.
    /// </summary>
    internal static NativeBorrowScope BorrowScope
    {
        get
        {
            nint provider = Provider;
            if (s_borrowScope is null || s_borrowScope.Depth != s_depth)
            {
                s_borrowScope = new NativeBorrowScope(provider, s_depth, s_borrowScope);
            }

            return s_borrowScope;
        }
    }

    /// <summary>
    /// Gets whether the current callback runs in a database whose text needs no transcoding; false outside a callback.
    /// </summary>
    internal static bool IsUtf8Database => s_api != 0 && ((NativeMemoryApi*)s_api)->_directReads >= 2;

    /// <summary>
    /// Gets whether this thread has a native memory scope, as a background worker has between transactions.
    /// </summary>
    internal static bool IsActive => s_api != 0;

    /// <summary>
    /// Gets whether managed code may read flat values in place, and text as well as bytes.
    /// </summary>
    /// <param name="text">Whether the value is text.</param>
    /// <returns>Whether the value may be read without a native call.</returns>
    internal static bool CanReadDirectly(bool text)
    {
        _ = Provider;
        return ((NativeMemoryApi*)s_api)->_directReads >= (text ? 2 : 1);
    }

    /// <summary>
    /// Reads callback-scoped interrupt flags without entering PostgreSQL. On Windows, a queued, unblocked signal also
    /// counts, because PostgreSQL turns it into a pending interrupt at its next check.
    /// </summary>
    /// <returns>Whether an interrupt is pending.</returns>
    internal static bool IsInterruptPending()
    {
        _ = Provider;
        NativeMemoryApi* api = (NativeMemoryApi*)s_api;
        return (api->_interruptPending != null && Volatile.Read(ref *api->_interruptPending) != 0) ||
            (api->_signalQueue != null && (Volatile.Read(ref *api->_signalQueue) & ~Volatile.Read(ref *api->_signalMask)) != 0);
    }

    /// <summary>
    /// Polls callback-scoped flags before entering PostgreSQL's guarded interrupt handler.
    /// </summary>
    internal static void CheckInterrupts()
    {
        NativeBorrowScope.CheckBackendAccess();
        _ = Provider;
        NativeMemoryApi* api = (NativeMemoryApi*)s_api;
        if (api->_interruptPending != null && api->_failedFrames != null &&
            Volatile.Read(ref *api->_interruptPending) == 0 && Volatile.Read(ref *api->_failedFrames) == 0 &&
            (api->_signalQueue == null || (Volatile.Read(ref *api->_signalQueue) & ~Volatile.Read(ref *api->_signalMask)) == 0))
        {
            return;
        }

        NativeMemoryRequest request = new() { _operation = NativeMemoryOperation.CheckInterrupts };
        Invoke(ref request, out _);
    }

    /// <summary>
    /// Invokes one checked native memory operation.
    /// </summary>
    /// <param name="request">The operation request.</param>
    /// <param name="result">The native result.</param>
    internal static void Invoke(ref NativeMemoryRequest request, out NativeMemoryResult result)
    {
        if (request._operation is not (NativeMemoryOperation.SpinLock or NativeMemoryOperation.RecordTerminal) &&
            !(request._operation == NativeMemoryOperation.SharedMemory && request._flags is 6 or 7))
        {
            NativeBorrowScope.CheckBackendAccess();
        }

        _ = Provider;
        NativeMemoryApi* api = (NativeMemoryApi*)s_api;
        NativeCallError error = default;
        NativeMemoryRequest localRequest = request;
        NativeMemoryResult localResult = default;
        int status = api->_invoke(s_api, &localRequest, &localResult, &error);
        result = localResult;
        if (status == 0)
        {
            return;
        }

        Exception exception;
        try
        {
            exception = error.ToManagedException();
        }
        finally
        {
            error.Release();
        }

        throw exception;
    }

    /// <summary>
    /// Copies terminal intent into the native callback frame without reporting, acquiring resources or starting SQL.
    /// </summary>
    /// <param name="level">The validated FATAL or PANIC severity.</param>
    /// <param name="report">The borrowed transport copied by the native frame before this call returns.</param>
    internal static void RecordTerminal(PgLogLevel level, NativeCallError* report)
    {
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.RecordTerminal,
            _flags = (int)level,
            _pointer = (nint)report,
        };
        Invoke(ref request, out _);
    }

    /// <summary>
    /// Validates that a context or allocation belongs to this callback's provider.
    /// </summary>
    /// <param name="provider">The provider identity stored by the handle.</param>
    internal static void CheckProvider(nint provider)
    {
        if (provider == 0 || provider != Provider)
        {
            throw new InvalidOperationException("The PostgreSQL memory handle belongs to another extension provider or backend.");
        }
    }
}

/// <summary>
/// Identifies one operation in the generated native memory envelope.
/// </summary>
internal enum NativeMemoryOperation
{
    /// <summary>
    /// Resolves the current native context.
    /// </summary>
    Current = 1,
    /// <summary>
    /// Resolves a predefined native context.
    /// </summary>
    Predefined = 2,
    /// <summary>
    /// Creates a child AllocSet context.
    /// </summary>
    Create = 3,
    /// <summary>
    /// Resolves the context's parent.
    /// </summary>
    Parent = 4,
    /// <summary>
    /// Reads a live context's identifier.
    /// </summary>
    Name = 5,
    /// <summary>
    /// Resets a context and deletes its descendants.
    /// </summary>
    Reset = 6,
    /// <summary>
    /// Resets only the selected context.
    /// </summary>
    ResetOnly = 7,
    /// <summary>
    /// Resets descendants while retaining their contexts.
    /// </summary>
    ResetChildren = 8,
    /// <summary>
    /// Deletes an owned context if it remains live.
    /// </summary>
    Delete = 9,
    /// <summary>
    /// Switches the current context and returns its predecessor.
    /// </summary>
    Switch = 10,
    /// <summary>
    /// Allocates a checked palloc chunk.
    /// </summary>
    Allocate = 11,
    /// <summary>
    /// Resizes a checked palloc chunk.
    /// </summary>
    Reallocate = 12,
    /// <summary>
    /// Frees a live allocation immediately.
    /// </summary>
    Free = 13,
    /// <summary>
    /// Copies bytes from a live allocation.
    /// </summary>
    Read = 14,
    /// <summary>
    /// Copies bytes into a live allocation.
    /// </summary>
    Write = 15,
    /// <summary>
    /// Clears a checked range of an allocation.
    /// </summary>
    Clear = 16,
    /// <summary>
    /// Resolves an allocation's actual native owner.
    /// </summary>
    Owner = 17,
    /// <summary>
    /// Queries PostgreSQL's context emptiness flag.
    /// </summary>
    IsEmpty = 18,
    /// <summary>
    /// Queries native context allocation statistics.
    /// </summary>
    Statistics = 19,
    /// <summary>
    /// Registers a one-shot managed reset callback.
    /// </summary>
    RegisterCallback = 20,
    /// <summary>
    /// Cancels a callback without unlinking PostgreSQL's pending native record.
    /// </summary>
    CancelCallback = 21,
    /// <summary>
    /// Transfers a tracked allocation back to raw native ownership without freeing it.
    /// </summary>
    Detach = 22,
    /// <summary>
    /// Adopts exclusive ownership of a live palloc-compatible pointer.
    /// </summary>
    Adopt = 23,
    /// <summary>
    /// Captures a live context's reset generation for a borrowed raw reference.
    /// </summary>
    CaptureGeneration = 24,
    /// <summary>
    /// Copies a borrowed raw reference after validating its context identity and reset generation.
    /// </summary>
    ReadReference = 25,
    /// <summary>
    /// Writes a borrowed raw reference after validating its context identity and reset generation.
    /// </summary>
    WriteReference = 26,
    /// <summary>
    /// Resolves the callback's native result owner, spanning all advances for set iterators.
    /// </summary>
    Callback = 27,
    /// <summary>
    /// Allocates a zeroed native varlena with a short or ordinary header and checked ownership.
    /// </summary>
    AllocateVarlena = 28,
    /// <summary>
    /// Operates on an owned or explicitly anchored PostgreSQL StringInfo buffer.
    /// </summary>
    StringInfo = 29,
    /// <summary>
    /// Operates on an owned or exclusively borrowed typed PostgreSQL list.
    /// </summary>
    List = 30,
    /// <summary>
    /// Allocates or accesses a selected-header native item pointer through checked ownership or an external anchor.
    /// </summary>
    ItemPointer = 31,
    /// <summary>
    /// Validates a generated native binding against the active extension's selected-header ABI.
    /// </summary>
    NativeBinding = 32,
    /// <summary>
    /// Formats a complete native node through its original checked allocation or raw lifetime anchor.
    /// </summary>
    FormatNode = 33,
    /// <summary>
    /// Executes a generated C call body beneath the callback's native error guard.
    /// </summary>
    NativeCall = 34,
    /// <summary>
    /// Registers, attaches or accesses a PostgreSQL shared-memory value and its lock.
    /// </summary>
    SharedMemory = 35,
    /// <summary>
    /// Initializes, prepares, acquires or queries selected-header PostgreSQL spinlock storage.
    /// </summary>
    SpinLock = 36,
    /// <summary>
    /// Registers or controls native background workers and their guarded worker operations.
    /// </summary>
    BackgroundWorker = 37,
    /// <summary>
    /// Processes PostgreSQL interrupts, including queued Windows signals, beneath the native error guard.
    /// </summary>
    CheckInterrupts = 38,
    /// <summary>
    /// Retains terminal diagnostics in the current native callback frame without entering PostgreSQL reporting.
    /// </summary>
    RecordTerminal = 39,
    /// <summary>
    /// Registers a caller-supplied native MemoryContext address.
    /// </summary>
    BorrowNative = 40,
    /// <summary>
    /// Registers the context that owns a caller-supplied allocation.
    /// </summary>
    ChunkOwner = 41,
    /// <summary>
    /// Gets a registered context's native MemoryContext address.
    /// </summary>
    NativePointer = 42,
    /// <summary>
    /// Runs one operation on a replication connection.
    /// </summary>
    Replication = 43,
    /// <summary>
    /// Runs one operation on a relation writer.
    /// </summary>
    RelationWriter = 44,
    /// <summary>
    /// Runs one replication origin operation.
    /// </summary>
    ReplicationOrigin = 45,
    /// <summary>
    /// Quotes an identifier or literal outside a SQL callback.
    /// </summary>
    Quote = 46,
    /// <summary>
    /// Pushes, updates or pops a managed error context.
    /// </summary>
    ErrorContext = 47,
    /// <summary>
    /// Registers the process-exit dispatcher.
    /// </summary>
    ProcessExit = 48,
}

/// <summary>
/// Borrows explicit AllocSet block sizes during native context creation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMemoryContextSizes
{
    /// <summary>
    /// Carries the retained first-block size or zero for the initial block size.
    /// </summary>
    internal nuint _minimumContextSize;
    /// <summary>
    /// Carries the first ordinary allocation block size.
    /// </summary>
    internal nuint _initialBlockSize;
    /// <summary>
    /// Carries the maximum ordinary allocation block size.
    /// </summary>
    internal nuint _maximumBlockSize;
}

/// <summary>
/// Carries one memory operation request across the native ABI.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMemoryRequest
{
    /// <summary>
    /// Selects the native operation.
    /// </summary>
    internal NativeMemoryOperation _operation;
    /// <summary>
    /// Carries operation-specific allocation, ownership or terminal severity flags.
    /// </summary>
    internal int _flags;
    /// <summary>
    /// Carries the operation's context or allocation identity.
    /// </summary>
    internal nint _context;
    /// <summary>
    /// Reserves a secondary native identity.
    /// </summary>
    internal nint _other;
    /// <summary>
    /// Borrows an operation-specific native pointer or context sizing payload.
    /// </summary>
    internal nint _pointer;
    /// <summary>
    /// Borrows the pinned managed input or output buffer.
    /// </summary>
    internal nint _data;
    /// <summary>
    /// Carries the byte length of the requested operation.
    /// </summary>
    internal nuint _length;
    /// <summary>
    /// Carries the requested native allocation alignment, or zero for the server default.
    /// </summary>
    internal nuint _alignment;
    /// <summary>
    /// Carries an offset or predefined-context discriminator.
    /// </summary>
    internal nint _value;
}

/// <summary>
/// Carries one memory operation result across the native ABI.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMemoryResult
{
    /// <summary>
    /// Returns a context identity or a resized allocation identity.
    /// </summary>
    internal nint _context;
    /// <summary>
    /// Returns a new allocation identity or an explicitly requested raw pointer.
    /// </summary>
    internal nint _pointer;
    /// <summary>
    /// Reserves a native buffer pointer for operation-specific transport.
    /// </summary>
    internal nint _data;
    /// <summary>
    /// Returns the byte length of a name, allocation, or native statistic.
    /// </summary>
    internal nuint _length;
    /// <summary>
    /// Returns a scalar operation result.
    /// </summary>
    internal nint _value;
}

/// <summary>
/// Describes the native operation function supplied by a generated callback.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeMemoryApi
{
    /// <summary>
    /// Identifies the native extension registry independently of a callback's stack address.
    /// </summary>
    internal nint _provider;
    /// <summary>
    /// Protects the native callback's initial memory owner from destructive operations.
    /// </summary>
    internal nint _current;
    /// <summary>
    /// Invokes PostgreSQL with a native error guard beneath the managed frame.
    /// </summary>
    internal delegate* unmanaged[Cdecl]<nint, NativeMemoryRequest*, NativeMemoryResult*, NativeCallError*, int> _invoke;
    /// <summary>
    /// Selects the native result owner, which spans all advances for a set iterator.
    /// </summary>
    internal nint _resultContext;
    /// <summary>
    /// Borrows PostgreSQL's header-verified 32-bit interrupt indicator for this callback.
    /// </summary>
    internal int* _interruptPending;
    /// <summary>
    /// Borrows the number of active native callback frames retaining a mandatory failure.
    /// </summary>
    internal int* _failedFrames;
    /// <summary>
    /// Borrows the Windows pending signal bits, or remains null on Unix.
    /// </summary>
    internal int* _signalQueue;
    /// <summary>
    /// Borrows the Windows blocked signal bits when a signal queue is present.
    /// </summary>
    internal int* _signalMask;
    /// <summary>
    /// Records what managed code may read in place: zero for nothing, one for bytes and two for bytes and text, when the
    /// database encoding needs no conversion to UTF-8.
    /// </summary>
    internal int _directReads;
}
