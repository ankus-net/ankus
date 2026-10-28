using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Marshals exact worker declarations and binds nested transaction callbacks without runtime code generation.
/// </summary>
internal static unsafe class NativeBackgroundWorker
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    [ThreadStatic]
    private static TransactionFrame? s_transaction;

    /// <summary>
    /// Validates and registers a worker while retaining any dynamic handle before native registration.
    /// </summary>
    internal static PgBackgroundWorkerHandle? Register(PgBackgroundWorkerOptions options, bool dynamic)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative((int)options.StartTime, nameof(options.StartTime));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)options.StartTime, 2, nameof(options.StartTime));
        ArgumentOutOfRangeException.ThrowIfNegative(options.NotifyProcessId, nameof(options.NotifyProcessId));
        if (options.DatabaseAccess && options.StartTime == PgBackgroundWorkerStartTime.PostmasterStart)
        {
            throw new ArgumentException("A database worker cannot start before PostgreSQL reaches a consistent state.", nameof(options));
        }

        if (!dynamic && options.NotifyProcessId != 0)
        {
            throw new ArgumentException("Only a dynamic background worker can request process notifications.", nameof(options));
        }

        int restart = WholeUnits(options.RestartDelay, TimeSpan.TicksPerSecond, nameof(options.RestartDelay));
        byte[] name = Encode(options.Name, nameof(options.Name), ascii: dynamic);
        byte[] type = Encode(options.Type, nameof(options.Type), ascii: dynamic);
        byte[] library = Encode(options.Library, nameof(options.Library), ascii: dynamic);
        byte[] entry = Encode(options.EntryPoint, nameof(options.EntryPoint), ascii: dynamic);
        byte[] extra = Encode(options.Extra, nameof(options.Extra), empty: true);
        fixed (byte* nameAddress = name, typeAddress = type, libraryAddress = library, entryAddress = entry, extraAddress = extra)
        {
            NativeBackgroundWorkerDefinition definition = new()
            {
                _name = (nint)nameAddress,
                _type = (nint)typeAddress,
                _library = (nint)libraryAddress,
                _entryPoint = (nint)entryAddress,
                _extra = (nint)extraAddress,
                _argument = options.Argument,
                _startTime = (int)options.StartTime,
                _restartSeconds = restart,
                _databaseAccess = options.DatabaseAccess ? 1 : 0,
                _notifyProcessId = options.NotifyProcessId,
            };
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.BackgroundWorker,
                _flags = dynamic ? 1 : 0,
                _pointer = (nint)(&definition),
                _length = (nuint)sizeof(NativeBackgroundWorkerDefinition),
            };
            if (!dynamic)
            {
                NativeMemoryContext.Invoke(ref request, out _);
                return null;
            }

            NativeBorrowScope scope = NativeMemoryContext.BorrowScope;
            // codeql[cs/missed-using-statement] The returned handle owns the lease, so it is released here only when registration fails.
            var lease = new NativeBackgroundWorkerLease(scope, options.NotifyProcessId);
            var handle = new PgBackgroundWorkerHandle(lease);
            scope.Register(lease);
            bool registered = false;
            try
            {
                NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
                if (result._context > 0)
                {
                    lease.Registered(result._context);
                }

                if (result._value == 0 && result._context == 0)
                {
                    return null;
                }

                if (result._value != 1 || result._context <= 0)
                {
                    throw new InvalidOperationException("PostgreSQL returned an invalid background-worker registration outcome.");
                }

                registered = true;
                return handle;
            }
            finally
            {
                if (!registered)
                {
                    lease.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Dispatches one scalar worker operation through the current guarded provider.
    /// </summary>
    internal static NativeMemoryResult Invoke(int operation, nint identity = 0, nint value = 0, nint target = 0)
    {
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.BackgroundWorker,
            _flags = operation,
            _context = identity,
            _value = value,
            _pointer = target,
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        return result;
    }

    /// <summary>
    /// Checks for an exact native Boolean, zero or one, and rejects any other reply.
    /// </summary>
    internal static bool Boolean(int operation, nint value = 0)
    {
        nint result = Invoke(operation, value: value)._value;
        return result is 0 or 1 ? result == 1 :
            throw new InvalidOperationException("PostgreSQL returned an invalid background-worker Boolean.");
    }

    /// <summary>
    /// Copies bounded worker identity text into independently owned managed storage.
    /// </summary>
    internal static string ReadText(int field)
    {
        byte* bytes = stackalloc byte[4096];
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.BackgroundWorker,
            _flags = 7,
            _value = field,
            _data = (nint)bytes,
            _length = 4096,
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        if (result._length > request._length)
        {
            throw new InvalidOperationException("PostgreSQL returned an invalid background-worker text length.");
        }

        return s_utf8.GetString(new ReadOnlySpan<byte>(bytes, (int)result._length));
    }

    /// <summary>
    /// Validates signal selections and checks the native result contains only selected signals.
    /// </summary>
    internal static PgBackgroundWorkerSignals SignalOperation(int operation, PgBackgroundWorkerSignals signals)
    {
        if (((int)signals & ~15) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(signals));
        }

        nint result = Invoke(operation, value: (nint)signals)._value;
        if ((result & ~(nint)signals) != 0)
        {
            throw new InvalidOperationException("PostgreSQL returned unexpected background-worker signal flags.");
        }

        return (PgBackgroundWorkerSignals)result;
    }

    /// <summary>
    /// Preserves infinite, zero and exact whole-millisecond waits.
    /// </summary>
    internal static bool Wait(TimeSpan? timeout) => Boolean(10, WholeUnits(timeout, TimeSpan.TicksPerMillisecond, nameof(timeout)));

    /// <summary>
    /// Connects using distinct null and empty name representations.
    /// </summary>
    internal static void Connect(string? database, string? user)
    {
        byte[]? databaseBytes = database is null ? null : Encode(database, nameof(database), empty: true);
        byte[]? userBytes = user is null ? null : Encode(user, nameof(user), empty: true);
        fixed (byte* databaseAddress = databaseBytes, userAddress = userBytes)
        {
            _ = Invoke(11, (nint)databaseAddress, target: (nint)userAddress);
        }
    }

    /// <summary>
    /// Preserves complete unsigned database and role OIDs.
    /// </summary>
    internal static void Connect(uint databaseOid, uint userOid) => Invoke(12, (nint)databaseOid, (nint)userOid);

    /// <summary>
    /// Runs one synchronous callback after native transaction startup and restores its enclosing capabilities.
    /// </summary>
    internal static TResult RunTransaction<TResult>(Func<TResult> action) => RunTransaction(action, 13);

    /// <summary>
    /// Runs synchronous work with SPI in a new transaction of a backend connected to a database, or of the current worker.
    /// </summary>
    /// <typeparam name="TResult">The owned result.</typeparam>
    /// <param name="action">The synchronous transaction body.</param>
    /// <returns>The callback result after successful commit.</returns>
    internal static TResult RunBackendTransaction<TResult>(Func<TResult> action) => RunTransaction(action, 16);

    private static TResult RunTransaction<TResult>(Func<TResult> action, int operation)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (s_transaction is not null)
        {
            throw new InvalidOperationException("Background-worker transactions cannot be nested.");
        }

        var frame = new TransactionFrame<TResult>(action);
        s_transaction = frame;
        try
        {
            _ = Invoke(operation, target: (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&Transaction);
            frame.Failure?.Throw();
            if (!frame.Invoked)
            {
                throw new InvalidOperationException("PostgreSQL did not invoke the background-worker transaction callback.");
            }

            return frame.Result;
        }
        finally
        {
            s_transaction = null;
        }
    }

    /// <summary>
    /// Prevents managed exceptions from crossing the native transaction frame.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Transaction(nint execute, nint memory)
    {
        TransactionFrame? frame = s_transaction;
        if (frame is null)
        {
            return 1;
        }

        try
        {
            if (frame.Invoked || execute == 0 || memory == 0)
            {
                throw new InvalidOperationException("PostgreSQL supplied an invalid background-worker transaction callback.");
            }

            frame.Invoked = true;
            nint previousBackend = NativeBackend.Enter(execute);
            nint previousMemory = NativeMemoryContext.Enter(memory);
            try
            {
                frame.Invoke();
            }
            finally
            {
                try
                {
                    NativeMemoryContext.Exit(previousMemory);
                }
                finally
                {
                    NativeBackend.Exit(previousBackend);
                }
            }

            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] Native code calls this, so every exception is kept for the caller instead of unwinding through it.
        catch (Exception exception)
        {
            frame.Failure = ExceptionDispatchInfo.Capture(exception);
            return 1;
        }
    }

    /// <summary>
    /// Rejects fractional or overflowing intervals so native timing values are never rounded.
    /// </summary>
    private static int WholeUnits(TimeSpan? duration, long ticksPerUnit, string parameter)
    {
        if (duration is null)
        {
            return -1;
        }

        long ticks = duration.Value.Ticks;
        if (ticks < 0 || ticks % ticksPerUnit != 0 || ticks / ticksPerUnit > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(parameter, "The duration must contain an exact nonnegative number of native timing units within Int32 range.");
        }

        return (int)(ticks / ticksPerUnit);
    }

    /// <summary>
    /// Produces strict, terminated UTF-8 without discarding embedded zero characters or malformed UTF-16.
    /// </summary>
    private static byte[] Encode(string text, string parameter, bool empty = false, bool ascii = false)
    {
        ArgumentNullException.ThrowIfNull(text, parameter);
        if ((!empty && text.Length == 0) || text.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Worker text cannot be empty where required or contain a zero character.", parameter);
        }

        byte[] bytes = s_utf8.GetBytes(text + '\0');
        if (ascii && text.Any(static value => value is not (>= ' ' and <= '\x7F') and not ('\t' or '\r' or '\n')))
        {
            throw new ArgumentException("Dynamic worker metadata must use ASCII characters that PostgreSQL preserves unchanged.", parameter);
        }

        return bytes;
    }

    /// <summary>
    /// Holds one callback's result and exception independently of PostgreSQL transaction memory.
    /// </summary>
    private abstract class TransactionFrame
    {
        /// <summary>
        /// Gets or sets whether native code has invoked this callback once.
        /// </summary>
        internal bool Invoked
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the original exception for rethrow after native abort.
        /// </summary>
        internal ExceptionDispatchInfo? Failure
        {
            get;
            set;
        }

        /// <summary>
        /// Invokes the statically typed transaction body.
        /// </summary>
        internal abstract void Invoke();
    }

    /// <summary>
    /// Retains a strongly typed body and result without reflection or delegate marshalling.
    /// </summary>
    private sealed class TransactionFrame<TResult>(Func<TResult> action) : TransactionFrame
    {
        /// <summary>
        /// Gets the completed body's result.
        /// </summary>
        internal TResult Result
        {
            get;
            private set;
        } = default!;

        /// <inheritdoc/>
        internal override void Invoke() => Result = action();
    }
}

/// <summary>
/// Borrows the complete registration payload while native code copies its selected-header representation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeBackgroundWorkerDefinition
{
    /// <summary>
    /// Borrows the terminated UTF-8 display name.
    /// </summary>
    internal nint _name;
    /// <summary>
    /// Borrows the terminated UTF-8 worker type.
    /// </summary>
    internal nint _type;
    /// <summary>
    /// Borrows the terminated UTF-8 library name.
    /// </summary>
    internal nint _library;
    /// <summary>
    /// Borrows the terminated UTF-8 native entry symbol.
    /// </summary>
    internal nint _entryPoint;
    /// <summary>
    /// Borrows the terminated UTF-8 extra text.
    /// </summary>
    internal nint _extra;
    /// <summary>
    /// Carries the exact by-value Datum word.
    /// </summary>
    internal nuint _argument;
    /// <summary>
    /// Carries the stable startup phase discriminator.
    /// </summary>
    internal int _startTime;
    /// <summary>
    /// Carries whole seconds or minus one for no restart.
    /// </summary>
    internal int _restartSeconds;
    /// <summary>
    /// Carries whether database initialization is allowed.
    /// </summary>
    internal int _databaseAccess;
    /// <summary>
    /// Carries the requested notification process identifier.
    /// </summary>
    internal int _notifyProcessId;
}
