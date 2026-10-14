using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Scripts native spinlock protocol responses and observes ownership without modeling PostgreSQL locking.
/// </summary>
internal sealed unsafe class NativeSpinLockTestFixture : IDisposable
{
    [ThreadStatic]
    private static NativeSpinLockTestFixture? s_current;

    /// <summary>
    /// Makes <paramref name="current"/> the NativeSpinLockTestFixture this thread's native callbacks use, or restores the enclosing one.
    /// </summary>
    private static void Install(NativeSpinLockTestFixture? current) => s_current = current;

    private readonly NativeSpinLockTestFixture? _previous = s_current;

    /// <summary>
    /// Installs this test's callback and protocol response script.
    /// </summary>
    internal NativeSpinLockTestFixture()
    {
        Install(this);
        Scope = MemoryContextTestFixture.Enter();
        Memory.Handler = Respond;
    }

    /// <summary>
    /// Gets the generic memory boundary recorder.
    /// </summary>
    internal MemoryContextTestFixture Memory { get; } = new();

    /// <summary>
    /// Gets the owning native callback scope.
    /// </summary>
    internal MemoryContextTestFixture.Scope Scope { get; }

    /// <summary>
    /// Gets held addresses, recorded independently of the opaque storage bytes.
    /// </summary>
    internal HashSet<nint> Held { get; } = [];

    /// <summary>
    /// Gets the released addresses in exact release order.
    /// </summary>
    internal List<nint> Released { get; } = [];

    /// <summary>
    /// Gets or sets whether selected headers expose SpinLockFree.
    /// </summary>
    internal bool SupportsQuery
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Gets or sets a missing release binding response.
    /// </summary>
    internal bool MissingRelease
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets an acquisition failure supplied through the owned error boundary.
    /// </summary>
    internal Exception? AcquireFailure
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets an observer called immediately before the scripted native release.
    /// </summary>
    internal Action? BeforeRelease
    {
        get;
        set;
    }

    /// <summary>
    /// Gets any release protocol violation without throwing across an unmanaged frame.
    /// </summary>
    internal Exception? ReleaseFailure
    {
        get;
        private set;
    }

    /// <summary>
    /// Supplies selected responses without interpreting PostgreSQL's real lock representation.
    /// </summary>
    internal NativeMemoryResult Respond(NativeMemoryRequest request)
    {
        if (request._operation != NativeMemoryOperation.SpinLock)
        {
            return Memory.Respond(request);
        }

        Assert.AreEqual((nuint)8, request._length);
        Assert.AreEqual((nuint)0, (nuint)request._pointer % 8);
        switch (request._flags)
        {
            case 0:
                *(long*)request._pointer = 0x17395B7D12345678;
                return new NativeMemoryResult { _value = SupportsQuery ? 1 : 0 };
            case 1:
                if (AcquireFailure is { } failure)
                {
                    throw failure;
                }

                Assert.IsTrue(Held.Add(request._pointer));
                return default;
            case 2:
                Assert.IsTrue(SupportsQuery);
                return new NativeMemoryResult { _value = Held.Contains(request._pointer) ? 1 : 0 };
            case 3:
                return new NativeMemoryResult { _pointer = MissingRelease ? 0 : (nint)(delegate* unmanaged[Cdecl]<nint, void>)&Release };
            default:
                throw new InvalidOperationException("Unexpected spinlock request.");
        }
    }

    /// <summary>
    /// Expires outstanding guards before restoring the enclosing native test capability.
    /// </summary>
    public void Dispose()
    {
        Scope.Dispose();
        Memory.Dispose();
        Install(_previous);
        Assert.IsEmpty(Held);
        Assert.IsNull(ReleaseFailure);
    }

    /// <summary>
    /// Observes nonthrowing release, retaining assertion failures for the managed test.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Release(nint address)
    {
        NativeSpinLockTestFixture fixture = s_current!;
        try
        {
            fixture.BeforeRelease?.Invoke();
            Assert.IsTrue(fixture.Held.Remove(address));
            fixture.Released.Add(address);
        }
        // codeql[cs/catch-of-all-exceptions] This stands in for native code, so the failure is kept for the test instead of unwinding through it.
        catch (Exception failure)
        {
            fixture.ReleaseFailure = failure;
        }
    }
}
