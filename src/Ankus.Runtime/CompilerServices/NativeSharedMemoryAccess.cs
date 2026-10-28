using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ankus.CompilerServices;

/// <summary>
/// Matches the selected-header native address slot and its closed-bit reader admission counter.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 24)]
internal struct NativeSharedMemoryAccess
{
    /// <summary>
    /// Holds the current shared address, published before opening reader admission.
    /// </summary>
    internal long _address;

    /// <summary>
    /// Counts active operations in the low 31 bits; the high bit prevents new admissions.
    /// </summary>
    internal int _readers;

    /// <summary>
    /// Identifies the process whose shutdown callback protects this address.
    /// </summary>
    internal int _processId;

    /// <summary>
    /// Counts admissions held on the backend thread inside a native callback scope.
    /// </summary>
    /// <remarks>
    /// Only that thread changes or reads this count. A FATAL report from a PostgreSQL call inside such an operation
    /// exits without resuming it, so retirement during process exit cannot wait for these admissions.
    /// </remarks>
    internal int _backendReaders;
}

/// <summary>
/// Protects a shared address for one synchronous managed operation or reader callback.
/// </summary>
/// <param name="access">The process-local native admission slot.</param>
/// <param name="value">The admitted shared value address.</param>
/// <param name="backend">Whether the admission is counted as held on the backend thread inside a native callback scope.</param>
internal ref struct NativeSharedMemoryAccessLease(nint access, nint value, bool backend)
{
    private nint _access = access;
    private readonly bool _backend = backend;

    /// <summary>
    /// Gets the address held until this lease is disposed.
    /// </summary>
    internal nint Value { get; } = value;

    /// <summary>
    /// Gets a reference used only inside the current bounded operation.
    /// </summary>
    /// <typeparam name="T">The exactly registered unmanaged value type.</typeparam>
    /// <returns>The admitted native storage.</returns>
    internal readonly unsafe ref T GetReference<T>() where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_access == 0, typeof(NativeSharedMemoryAccessLease));
        return ref Unsafe.AsRef<T>((void*)Value);
    }

    /// <summary>
    /// Releases reader admission once, including when an operation throws.
    /// </summary>
    public unsafe void Dispose()
    {
        if (_access != 0)
        {
            var access = (NativeSharedMemoryAccess*)_access;
            _access = 0;
            if (_backend)
            {
                access->_backendReaders--;
            }

            Interlocked.Decrement(ref access->_readers);
        }
    }
}
