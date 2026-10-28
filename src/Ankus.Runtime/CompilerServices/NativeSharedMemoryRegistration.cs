using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Ankus.CompilerServices;

/// <summary>
/// Roots one process-local initializer and identifies its native shared-memory descriptor.
/// </summary>
internal sealed unsafe class NativeSharedMemoryRegistration(string name, string identity, int size, NativeSharedMemoryKind kind = NativeSharedMemoryKind.Locked)
{
    private nint _provider;
    private nint _handle;
    private nint _access;

    /// <summary>
    /// Gets the provider whose native callback may acquire embedded backend locks.
    /// </summary>
    internal nint Provider => _provider;

    /// <summary>
    /// Registers the descriptor once; failure retains the ability to retry.
    /// </summary>
    /// <param name="initializer">Writes the initial unmanaged value at shared-memory startup.</param>
    internal void Register(Action<nint, nuint> initializer)
    {
        nint provider = NativeMemoryContext.Provider;
        if (_handle != 0)
        {
            NativeMemoryContext.CheckProvider(_provider);
            return;
        }

        byte[] encoded = new UTF8Encoding(false, true).GetBytes(name + '\0');
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        nint cookie = NativeSharedMemoryInitializer.Add(provider, initializer);
        try
        {
            fixed (byte* text = encoded, type = hash)
            {
                NativeSharedMemoryDefinition definition = new()
                {
                    _name = (nint)text,
                    _identity = (nint)type,
                    _size = (nuint)size,
                    _kind = (uint)kind,
                    _cookie = cookie,
                    _initialize = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nuint, NativeCallbackContext*, int>)&NativeSharedMemoryInitializer.Invoke,
                };
                NativeMemoryRequest request = new()
                {
                    _operation = NativeMemoryOperation.SharedMemory,
                    _data = (nint)(&definition),
                };
                NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
                if (result._value == 0)
                {
                    throw new InvalidOperationException("PostgreSQL did not return a shared-memory registration.");
                }

                if (kind != NativeSharedMemoryKind.Locked && (result._data == 0 || (nuint)result._data % sizeof(long) != 0 ||
                    result._length != (nuint)sizeof(NativeSharedMemoryAccess)))
                {
                    throw new InvalidOperationException("PostgreSQL returned invalid shared-memory access storage.");
                }

                _provider = provider;
                _handle = result._value;
                Volatile.Write(ref _access, result._data);
            }
        }
        catch
        {
            NativeSharedMemoryInitializer.Remove(cookie);
            throw;
        }
    }

    /// <summary>
    /// Borrows a published shared address without invoking PostgreSQL or requiring a backend thread.
    /// </summary>
    /// <returns>A bounded operation lease that prevents shared-memory retirement until disposal.</returns>
    internal NativeSharedMemoryAccessLease Open()
    {
        nint location = Volatile.Read(ref _access);
        if (location == 0)
        {
            throw new InvalidOperationException("Register the PostgreSQL shared descriptor during shared preload before accessing it.");
        }

        var access = (NativeSharedMemoryAccess*)location;
        if (Volatile.Read(ref access->_processId) != Environment.ProcessId)
        {
            throw new InvalidOperationException("PostgreSQL shared storage has not attached in this process.");
        }

        while (true)
        {
            int readers = Volatile.Read(ref access->_readers);
            if (readers < 0)
            {
                throw new InvalidOperationException("PostgreSQL shared storage is not initialized or is being retired.");
            }

            if (readers == int.MaxValue)
            {
                throw new InvalidOperationException("The PostgreSQL shared-memory reader limit has been reached.");
            }

            if (Interlocked.CompareExchange(ref access->_readers, readers + 1, readers) == readers)
            {
                break;
            }
        }

        nint value = (nint)Volatile.Read(ref access->_address);
        int alignment = kind == NativeSharedMemoryKind.Shared ? sizeof(long) : Math.Min(size, sizeof(long));
        if (value == 0 || (nuint)value % (nuint)alignment != 0)
        {
            Interlocked.Decrement(ref access->_readers);
            throw new InvalidOperationException("PostgreSQL shared storage has an invalid value address.");
        }

        // Only a callback-scoped backend operation can call PostgreSQL and be abandoned by FATAL.
        bool backend = NativeBackend.InCallback;
        if (backend)
        {
            access->_backendReaders++;
        }

        return new NativeSharedMemoryAccessLease(location, value, backend);
    }

    /// <summary>
    /// Acquires one checked native lock lease in the current callback.
    /// </summary>
    /// <param name="exclusive">Whether the lease permits writes.</param>
    /// <returns>The acquired lease, also registered for callback-exit cleanup.</returns>
    internal NativeSharedMemoryLease Acquire(bool exclusive)
    {
        if (_handle == 0)
        {
            throw new InvalidOperationException("Register the PostgreSQL shared-memory descriptor during shared preload before acquiring it.");
        }

        NativeMemoryContext.CheckProvider(_provider);
        NativeBorrowScope scope = NativeMemoryContext.BorrowScope;
        var lease = new NativeSharedMemoryLease(scope, _handle);
        scope.Register(lease);
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.SharedMemory,
            _flags = exclusive ? 2 : 1,
            _context = _handle,
        };
        try
        {
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            lease.Acquired(result._value);
            return lease;
        }
        catch
        {
            scope.Unregister(lease);
            throw;
        }
    }
}

/// <summary>
/// Carries the process-local registration through the native memory guard.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeSharedMemoryDefinition
{
    /// <summary>
    /// Borrows the zero-terminated UTF-8 name for registration.
    /// </summary>
    internal nint _name;

    /// <summary>
    /// Borrows the 32-byte digest of the closed managed value identity.
    /// </summary>
    internal nint _identity;

    /// <summary>
    /// Gives the exact unmanaged value size.
    /// </summary>
    internal nuint _size;

    /// <summary>
    /// Selects a lightweight lock, atomic scalar or shared aggregate.
    /// </summary>
    internal uint _kind;

    /// <summary>
    /// Identifies the process-local rooted factory.
    /// </summary>
    internal nint _cookie;

    /// <summary>
    /// Addresses the statically compiled guarded managed dispatcher.
    /// </summary>
    internal nint _initialize;
}

/// <summary>
/// Dispatches statically compiled initializer delegates without storing managed references in shared memory.
/// </summary>
internal static unsafe class NativeSharedMemoryInitializer
{
    [ThreadStatic]
    private static Dictionary<nint, (nint Provider, Action<nint, nuint> Initialize)>? s_initializers;

    private static long s_nextId;

    /// <summary>
    /// Roots an initializer for the lifetime of its PostgreSQL process.
    /// </summary>
    /// <param name="provider">The registering extension's native capability identity.</param>
    /// <param name="initializer">The process-local value factory.</param>
    /// <returns>The unique cookie supplied to native registration.</returns>
    internal static nint Add(nint provider, Action<nint, nuint> initializer)
    {
        nint cookie = NativeAggregate.AllocateStateId(ref s_nextId);
        (s_initializers ??= []).Add(cookie, (provider, initializer));
        return cookie;
    }

    /// <summary>
    /// Releases the root of a registration that failed before publication.
    /// </summary>
    /// <param name="cookie">The unpublished registration cookie.</param>
    internal static void Remove(nint cookie) => s_initializers?.Remove(cookie);

    /// <summary>
    /// Runs the initializer inside scoped backend capabilities and transports exceptions back to native code.
    /// </summary>
    /// <param name="cookie">The process-local registration identity.</param>
    /// <param name="destination">The native initial-value destination.</param>
    /// <param name="length">The exact shared value size.</param>
    /// <param name="context">The native startup capabilities and owned error destination.</param>
    /// <returns>Zero on success; one when an owned diagnostic was written.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static int Invoke(nint cookie, nint destination, nuint length, NativeCallbackContext* context)
    {
        try
        {
            nint previousMemory = NativeMemoryContext.Enter(context->Memory);
            nint previousBackend = NativeBackend.Enter(context->Execute);
            nint previousRead = NativeGuc.Enter(context->Read);
            nint previousLog = NativeLog.Enter(context->Log);
            try
            {
                if (s_initializers is null || !s_initializers.TryGetValue(cookie, out (nint Provider, Action<nint, nuint> Initialize) entry))
                {
                    throw new InvalidOperationException("The PostgreSQL shared-memory initializer is stale or belongs to another backend thread.");
                }

                NativeMemoryContext.CheckProvider(entry.Provider);
                entry.Initialize(destination, length);
                return 0;
            }
            finally
            {
                NativeLog.Exit(previousLog);
                NativeGuc.Exit(previousRead);
                NativeBackend.Exit(previousBackend);
                NativeMemoryContext.Exit(previousMemory);
            }
        }
        // codeql[cs/catch-of-all-exceptions] Native code calls this, so every exception goes back to PostgreSQL as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, (NativeCallError*)context->Error);
            return 1;
        }
    }
}
