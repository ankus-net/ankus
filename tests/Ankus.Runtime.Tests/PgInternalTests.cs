using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks exact internal identities, managed roots, lifetime validation, and failure cleanup.
/// </summary>
[TestClass]
public sealed class PgInternalTests
{
    /// <summary>
    /// Keeps SQL NULL separate from a present zero pointer and transports every native word bit.
    /// </summary>
    [TestMethod]
    public void NativeWordsRetainNullTypeAndLifetimeContracts()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext owner = PgMemoryContext.Current;
        PgInternal zero = PgInternal.DangerousCreate(0, owner);
        Assert.IsFalse(zero.IsManaged);
        Assert.AreEqual((nuint)0, zero.DangerousGetBits());
        Assert.IsNull(zero.DangerousBorrow<int>());
        Assert.ThrowsExactly<InvalidOperationException>(() => zero.Get<int>());
        Assert.IsNull(PgDatum.DangerousCreate(0, 2281, owner, isNull: true).Read<PgInternal?>());
        Assert.ThrowsExactly<ArgumentException>(() => new PgInternal(PgDatum.DangerousCreate(0, 2281, owner, isNull: true)));
        Assert.ThrowsExactly<ArgumentException>(() => new PgInternal(PgDatum.DangerousCreate(0, 23, owner)));
        Assert.ThrowsExactly<InvalidCastException>(() => PgDatum.DangerousCreate(0, 23, owner).Read<PgInternal>());
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgInternal(null!));
        PgInternal value = PgInternal.DangerousCreate(nuint.MaxValue, owner);
        Assert.AreEqual(nuint.MaxValue, value.DangerousGetBits());
        Assert.AreEqual(2281U, SpiParameter.Create(value).TypeOid);
        Assert.AreSame(value, SpiParameter.Create(value).Value);
        Assert.AreEqual(2281U, PgFunctionArgument.Create<PgInternal?>(null).TypeOid);
        NativeValue transport = NativeValue.FromInternal(value);
        try
        {
            NativeDatumReference reference = MemoryMarshal.Read<NativeDatumReference>(transport.ReadBytes());
            Assert.AreEqual(nuint.MaxValue, reference._bits);
            Assert.AreEqual(owner.Id, reference._context);
            Assert.AreEqual((nuint)901, reference._generation);
            Assert.AreEqual((byte)0, transport.IsNull);
        }
        finally
        {
            transport.Release();
        }

        fixture.Handler = static _ => new NativeMemoryResult { _value = 902 };
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.DangerousGetBits());
        Assert.ThrowsExactly<ObjectDisposedException>(value.DangerousBorrow<int>);
        Assert.ThrowsExactly<ObjectDisposedException>(() => NativeValue.FromInternal(value));
    }

    /// <summary>
    /// Received aliases recover the original managed owner and share exactly-once invalidation before disposal.
    /// </summary>
    [TestMethod]
    public void ManagedAliasesRetainExactPayloadAndInvalidateBeforeDisposal()
    {
        using var fixture = new InternalFixture();
        var payload = new Payload();
        PgInternal state = PgInternal.Create(payload);
        PgDatum raw = state.Datum;
        SpiParameter parameter = SpiParameter.Create(state);
        Assert.IsTrue(state.IsManaged);
        Assert.AreSame(payload, state.Get<Payload>());
        Assert.ThrowsExactly<InvalidCastException>(() => state.Get<object>());
        Assert.ThrowsExactly<InvalidOperationException>(state.DangerousBorrow<int>);
        fixture.Memory.Current = 202;
        var alias = new PgInternal(PgDatum.DangerousCreate(state.DangerousGetBits(), 2281, PgMemoryContext.Current));
        Assert.AreSame(state.Datum, alias.Datum);
        Assert.AreSame(payload, alias.Get<Payload>());
        Assert.AreEqual(101, alias.Datum.Lifetime.ContextId);
        payload.OnDispose = () => Assert.ThrowsExactly<ObjectDisposedException>(() => alias.Get<Payload>());
        Assert.IsNull(fixture.Invoke());
        Assert.AreEqual(1, payload.Disposals);
        Assert.ThrowsExactly<ObjectDisposedException>(state.Get<Payload>);
        Assert.ThrowsExactly<ObjectDisposedException>(alias.Get<Payload>);
        Assert.ThrowsExactly<ObjectDisposedException>(() => state.Datum);
        Assert.ThrowsExactly<ObjectDisposedException>(() => state.DangerousGetBits());
        Assert.ThrowsExactly<ObjectDisposedException>(alias.DangerousBorrow<int>);
        Assert.ThrowsExactly<ObjectDisposedException>(() => NativeValue.FromInternal(state));
        Assert.ThrowsExactly<ObjectDisposedException>(() => SpiType.ToNative(parameter.Value));
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgInternal(raw).Get<Payload>());
    }

    /// <summary>
    /// A failed native registration frees its identity without adopting or disposing the caller's payload.
    /// </summary>
    [TestMethod]
    public void FailedRegistrationReleasesIdentityAndPermitsRetry()
    {
        using var fixture = new InternalFixture { FailRegistration = true };
        var payload = new Payload();
        PgException error = Assert.ThrowsExactly<PgException>(() => PgInternal.Create(payload));
        Assert.AreEqual("53200", error.SqlState);
        Assert.AreEqual(0, payload.Disposals);
        Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Memory.Requests);
        Assert.IsFalse(PgInternal.DangerousCreate(4096, PgMemoryContext.Current).IsManaged);
        fixture.FailRegistration = false;
        PgInternal state = PgInternal.Create(payload);
        Assert.AreSame(payload, state.Get<Payload>());
        Assert.IsNull(fixture.Invoke());
        Assert.AreEqual(1, payload.Disposals);
    }

    /// <summary>
    /// State remains rooted without wrappers and is released even when user disposal throws.
    /// </summary>
    [TestMethod]
    public void NativeOwnerRootsPayloadAndReleasesItOnThrowingCleanup()
    {
        using var fixture = new InternalFixture();
        WeakReference<Payload> weak = CreateRetainedPayload();
        GarbageCollection.Collect();
        Assert.IsTrue(IsAlive(weak));
        PgException error = Assert.IsInstanceOfType<PgException>(fixture.Invoke());
        Assert.AreEqual("38000", error.SqlState);
        Assert.Contains("cleanup probe", error.Message);
        GarbageCollection.Collect();
        Assert.IsFalse(IsAlive(weak));
        Assert.IsFalse(PgInternal.DangerousCreate(4096, PgMemoryContext.Current).IsManaged);
    }

    /// <summary>
    /// Isolates payload construction from the collecting test's stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Payload> CreateRetainedPayload()
    {
        var payload = new Payload { OnDispose = static () => throw new InvalidOperationException("cleanup probe") };
        _ = PgInternal.Create(payload);
        return new WeakReference<Payload>(payload);
    }

    /// <summary>
    /// Avoids retaining a temporary strong reference across test collections.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<Payload> weak) => weak.TryGetTarget(out _);

    /// <summary>
    /// Records disposal and executes an optional reentrant cleanup observation.
    /// </summary>
    private sealed class Payload : IDisposable
    {
        /// <summary>
        /// Gets the number of disposal calls.
        /// </summary>
        internal int Disposals
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets or sets a cleanup observation.
        /// </summary>
        internal Action? OnDispose
        {
            get;
            set;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Disposals++;
            OnDispose?.Invoke();
        }
    }

    /// <summary>
    /// Supplies one reusable native identity and owns successful cleanup registrations.
    /// </summary>
    private sealed unsafe class InternalFixture : IDisposable
    {
        private readonly MemoryContextTestFixture.Scope _scope;
        private readonly Queue<NativeMemoryRequest> _registrations = [];

        /// <summary>
        /// Installs the controlled allocation and registration behavior.
        /// </summary>
        internal InternalFixture()
        {
            _scope = MemoryContextTestFixture.Enter();
            Memory.Handler = Handle;
        }

        /// <summary>
        /// Gets the native operation observations.
        /// </summary>
        internal MemoryContextTestFixture Memory { get; } = new();

        /// <summary>
        /// Gets or sets whether registration fails before ownership transfers.
        /// </summary>
        internal bool FailRegistration
        {
            get;
            set;
        }

        /// <summary>
        /// Runs one pending native cleanup callback and releases its owned diagnostics.
        /// </summary>
        internal PgException? Invoke()
        {
            NativeMemoryRequest registration = _registrations.Dequeue();
            var callback = (delegate* unmanaged[Cdecl]<nint, nint, NativeCallError*, int>)registration._pointer;
            NativeCallError error = default;
            try
            {
                return callback(registration._other, _scope.Address, &error) == 0 ? null : error.ToException();
            }
            finally
            {
                error.Release();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            using (_scope)
            using (Memory)
            {
                while (_registrations.Count != 0)
                {
                    Assert.IsNull(Invoke());
                }
            }
        }

        /// <summary>
        /// Returns controlled memory responses without dereferencing the synthetic native pointer.
        /// </summary>
        private NativeMemoryResult Handle(NativeMemoryRequest request)
        {
            if (request._operation == NativeMemoryOperation.RegisterCallback)
            {
                if (FailRegistration)
                {
                    throw new PgException("53200", "registration failed");
                }

                _registrations.Enqueue(request);
            }

            return request._operation switch
            {
                NativeMemoryOperation.Callback => new NativeMemoryResult { _context = 101 },
                NativeMemoryOperation.Read => new NativeMemoryResult { _pointer = 4096 },
                _ => Memory.Respond(request),
            };
        }
    }
}
