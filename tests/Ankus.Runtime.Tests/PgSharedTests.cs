using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies scoped readonly shared aggregates, inline atomic values and address admission.
/// </summary>
[TestClass]
public sealed unsafe partial class PgSharedTests
{
    /// <summary>
    /// Invalid inputs and missing registration fail before PostgreSQL calls or callback invocation.
    /// </summary>
    [TestMethod]
    public void SharedViewsRejectInvalidContracts()
    {
        using var memory = new MemoryContextTestFixture();
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentNullException>(() => new PgShared<int>(null!)).ParamName);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => new PgShared<int>(string.Empty)).ParamName);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => new PgShared<int>("a\0b")).ParamName);
        Assert.AreEqual("storage", Assert.ThrowsExactly<ArgumentNullException>(() => PgSharedMemory.Initialize((PgShared<int>)null!)).ParamName);
        var storage = new PgShared<int>("unregistered");
        Assert.AreEqual("initializer", Assert.ThrowsExactly<ArgumentNullException>(() => PgSharedMemory.Initialize(storage, null!)).ParamName);
        Assert.AreEqual("reader", Assert.ThrowsExactly<ArgumentNullException>(() => storage.Read<int>(null!)).ParamName);
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read(static (in value) => value));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgSharedMemory.Initialize(storage));
        using (MemoryContextTestFixture.Enter())
        {
            Assert.ThrowsExactly<EncoderFallbackException>(() => PgSharedMemory.Initialize(new PgShared<int>("bad\uD800")));
        }

        Assert.IsEmpty(memory.Requests);
    }

    /// <summary>
    /// The native kind, type hash and exact size distinguish shared aggregates with deferred initialization.
    /// </summary>
    [TestMethod]
    public void SharedViewsPreserveInitializationAndIdentity()
    {
        using var fixture = new SharedFixture();
        var storage = new PgShared<Aggregate>("shared café 🐘");
        int calls = 0;
        PgSharedMemory.Initialize(storage, () =>
        {
            calls++;
            return new Aggregate(73, 11);
        });
        Assert.AreEqual(storage.Name, fixture.Name);
        Assert.AreEqual(2u, fixture.Definition._kind);
        Assert.AreEqual((nuint)sizeof(Aggregate), fixture.Definition._size);
        Assert.AreSequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(Aggregate).AssemblyQualifiedName!)), fixture.Identity);
        Assert.AreEqual(0, calls);
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read(static (in value) => value.Tag));
        PgSharedMemory.Initialize(storage, () => throw new InvalidOperationException("Replaced factory."));
        Assert.HasCount(1, fixture.Memory.Requests);
        fixture.Initialize();
        fixture.Publish();
        Assert.AreEqual(1, calls);
        Assert.AreEqual((73L, 11), storage.Read(static (in value) => (value._count.Value, value.Tag)));
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    /// <summary>
    /// Wide immutable values and odd-sized inline arrays retain their exact contents.
    /// </summary>
    [TestMethod]
    public void SharedViewsReadExactAggregateValues()
    {
        using (var fixture = new SharedFixture())
        {
            Guid identity = new("12345678-1234-5678-90ab-123456789abc");
            var expected = new ImmutableValue(identity, Int128.MinValue + 73, 1.234567890123456789m);
            PgShared<ImmutableValue> storage = fixture.Start(expected);
            Assert.AreEqual(expected, storage.Read(static (in value) => value));
            Assert.AreEqual(0, fixture.Access->_readers);
        }

        using (var fixture = new SharedFixture())
        {
            Bytes expected = default;
            expected[0] = 0x81;
            expected[2] = 0xF3;
            expected[4] = 0x7F;
            PgShared<Bytes> storage = fixture.Start(expected);
            Assert.AreEqual((nuint)5, fixture.Definition._size);
            Assert.AreSequenceEqual(new byte[] { 0x81, 0, 0xF3, 0, 0x7F }, storage.Read(static (in value) => ((ReadOnlySpan<byte>)value).ToArray()));
            fixture.AssertGuards(5);
        }
    }

    /// <summary>
    /// The factory-free overload initializes the complete aggregate, including inline atomic slots, to zero.
    /// </summary>
    [TestMethod]
    public void SharedViewsInitializeDefaults()
    {
        using var fixture = new SharedFixture();
        var storage = new PgShared<Aggregate>("default");
        PgSharedMemory.Initialize(storage);
        fixture.Initialize();
        fixture.Publish();
        Assert.AreEqual((0L, 0), storage.Read(static (in value) => (value._count.Value, value.Tag)));
        Assert.AreEqual(1L, storage.Read(static (in value) => value._count.Increment()));
        Assert.AreEqual(1L, storage.Read(static (in value) => value._count.Value));
        fixture.AssertGuards(sizeof(Aggregate));
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    /// <summary>
    /// Invalid startup storage leaves its bytes and factory untouched before a valid retry.
    /// </summary>
    [TestMethod]
    [DataRow(0, 8)]
    [DataRow(1, 0)]
    [DataRow(1, 7)]
    [DataRow(1, 9)]
    public void SharedViewInitializersRejectInvalidStorage(int present, int length)
    {
        using var fixture = new SharedFixture();
        var storage = new PgShared<long>("invalid");
        int calls = 0;
        PgSharedMemory.Initialize(storage, () =>
        {
            calls++;
            return 73;
        });
        NativeCallError error = default;
        try
        {
            Assert.AreEqual(1, fixture.Invoke(present == 0 ? 0 : fixture.Value, (nuint)length, &error));
            Assert.Contains("invalid value storage", error.ToException().Message);
            Assert.AreEqual(0, calls);
            fixture.AssertGuards(0);
        }
        finally
        {
            error.Release();
        }

        fixture.Initialize();
        fixture.Publish();
        Assert.AreEqual(1, calls);
        Assert.AreEqual(73L, storage.Read(static (in value) => value));
    }

    /// <summary>
    /// Nested callbacks balance admission; exceptions and closure do not leak or reopen retired addresses.
    /// </summary>
    [TestMethod]
    public void SharedViewsReleaseAdmissionAndObserveReplacement()
    {
        using var fixture = new SharedFixture();
        PgShared<Aggregate> storage = fixture.Start(new Aggregate(73, 11));
        Assert.AreEqual(74L, storage.Read((in value) =>
        {
            Assert.AreEqual(1, fixture.Access->_readers);
            Assert.AreEqual(73L, storage.Read((in nested) =>
            {
                Assert.AreEqual(2, fixture.Access->_readers);
                return nested._count.Value;
            }));
            Assert.AreEqual(1, fixture.Access->_readers);
            return value._count.Increment();
        }));
        var failure = new InvalidOperationException("reader failure");
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read<int>((in value) =>
        {
            value._count.Exchange(91);
            throw failure;
        })));
        Assert.AreEqual(0, fixture.Access->_readers);
        Assert.AreEqual(91L, storage.Read(static (in value) => value._count.Value));
        storage.Read((in value) =>
        {
            Interlocked.Or(ref fixture.Access->_readers, int.MinValue);
            Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read(static (in next) => next.Tag));
            return value._count.Increment();
        });
        Assert.AreEqual(int.MinValue, fixture.Access->_readers);
        Assert.AreEqual(92L, ((Aggregate*)fixture.Value)->_count.Value);
        fixture.UseReplacement(new Aggregate(107, 19));
        Assert.AreEqual((107L, 19), storage.Read(static (in value) => (value._count.Value, value.Tag)));
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    /// <summary>
    /// Backend callback admissions are counted separately so process exit can retire an address a FATAL report abandons.
    /// </summary>
    [TestMethod]
    public void SharedViewsCountBackendCallbackAdmissions()
    {
        using var fixture = new SharedFixture();
        PgShared<Aggregate> storage = fixture.Start(new Aggregate(73, 11));
        Assert.AreEqual(0, storage.Read((in _) => fixture.Access->_backendReaders));
        nint previous = NativeBackend.Enter(1);
        try
        {
            Assert.AreEqual(11, storage.Read((in value) =>
            {
                Assert.AreEqual(1, fixture.Access->_backendReaders);
                Assert.AreEqual(1, fixture.Access->_readers);
                Assert.AreEqual(2, storage.Read((in _) => fixture.Access->_backendReaders));

                // Other threads cannot call PostgreSQL, so their finite operations remain ordinary admissions.
                // A dedicated thread has no callback scope, unlike a reused pool thread.
                (int Backend, int Readers) observed = default;
                var other = new Thread(() => observed = storage.Read((in _) => (fixture.Access->_backendReaders, fixture.Access->_readers)));
                other.Start();
                Assert.IsTrue(other.Join(TimeSpan.FromSeconds(10)));
                Assert.AreEqual((1, 2), observed);
                return value.Tag;
            }));
            var failure = new InvalidOperationException("reader failure");
            Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read<int>((in _) => throw failure)));
            Assert.AreEqual(0, fixture.Access->_backendReaders);
        }
        finally
        {
            NativeBackend.Exit(previous);
        }

        Assert.AreEqual(0, fixture.Access->_readers);
        Assert.AreEqual(0, fixture.Access->_backendReaders);
    }

    /// <summary>
    /// Concurrent readers update original atomic fields without calling native PostgreSQL operations.
    /// </summary>
    [TestMethod]
    public void SharedViewsSupportManagedThreads()
    {
        using var fixture = new SharedFixture();
        PgShared<Aggregate> storage = fixture.Start(new Aggregate(73, 11));
        DedicatedThread.RunConcurrently(4, TimeSpan.FromSeconds(10), () =>
        {
            for (int iteration = 0; iteration < 2000; iteration++)
            {
                storage.Read(static (in value) => value._count.Increment());
            }
        });
        Assert.AreEqual((8073L, 11), storage.Read(static (in value) => (value._count.Value, value.Tag)));
        Assert.HasCount(1, fixture.Memory.Requests);
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    private readonly struct Aggregate(long count, int tag)
    {
        internal readonly PgAtomicValue<long> _count = new(count);
        internal int Tag { get; } = tag;
    }

    private readonly record struct ImmutableValue(Guid Identity, Int128 Bound, decimal Ratio);

    [InlineArray(5)]
    private struct Bytes
    {
        private byte _element;
    }

    private sealed class SharedFixture : IDisposable
    {
        private readonly byte* _buffer = (byte*)NativeMemory.Alloc(256);
        private nint _cookie;

        internal SharedFixture()
        {
            Scope = MemoryContextTestFixture.Enter();
            Memory.Handler = Respond;
            new Span<byte>(_buffer, 256).Fill(0xA5);
            Access->_readers = int.MinValue;
            Access->_processId = Environment.ProcessId;
        }

        internal MemoryContextTestFixture Memory { get; } = new();
        internal MemoryContextTestFixture.Scope Scope { get; }
        internal NativeSharedMemoryAccess* Access { get; } = (NativeSharedMemoryAccess*)NativeMemory.AllocZeroed((nuint)sizeof(NativeSharedMemoryAccess));
        internal nint Value => (nint)(_buffer + 8);
        internal NativeSharedMemoryDefinition Definition
        {
            get;
            private set;
        }

        internal string? Name
        {
            get;
            private set;
        }

        internal byte[] Identity
        {
            get;
            private set;
        } = [];

        internal PgShared<T> Start<T>(T value) where T : unmanaged
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeof(T), 120, nameof(value));
            var storage = new PgShared<T>("shared");
            PgSharedMemory.Initialize(storage, () => value);
            Initialize();
            Publish();
            return storage;
        }

        internal void Initialize()
        {
            NativeCallError error = default;
            try
            {
                Assert.AreEqual(0, Invoke(Value, Definition._size, &error));
            }
            finally
            {
                error.Release();
            }
        }

        internal int Invoke(nint destination, nuint length, NativeCallError* error)
        {
            var context = new NativeCallbackContext((nint)error, 0, Scope.Address, 0, 0);
            return ((delegate* unmanaged[Cdecl]<nint, nint, nuint, NativeCallbackContext*, int>)Definition._initialize)(
                Definition._cookie, destination, length, &context);
        }

        internal void Publish()
        {
            Access->_address = Value;
            Access->_processId = Environment.ProcessId;
            Volatile.Write(ref Access->_readers, 0);
        }

        internal void UseReplacement<T>(T value) where T : unmanaged
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeof(T), 120, nameof(value));
            Unsafe.Write(_buffer + 136, value);
            Access->_address = (long)(_buffer + 136);
            Volatile.Write(ref Access->_readers, 0);
        }

        internal void AssertGuards(int size)
        {
            Assert.AreEqual(-1, new ReadOnlySpan<byte>(_buffer, 8).IndexOfAnyExcept((byte)0xA5));
            Assert.AreEqual(-1, new ReadOnlySpan<byte>(_buffer + 8 + size, 120 - size).IndexOfAnyExcept((byte)0xA5));
            if (Access->_address == (long)(_buffer + 136))
            {
                Assert.AreEqual(-1, new ReadOnlySpan<byte>(_buffer + 128, 8).IndexOfAnyExcept((byte)0xA5));
                Assert.AreEqual(-1, new ReadOnlySpan<byte>(_buffer + 136 + size, 120 - size).IndexOfAnyExcept((byte)0xA5));
            }
        }

        public void Dispose()
        {
            Scope.Dispose();
            NativeSharedMemoryInitializer.Remove(_cookie);
            Memory.Dispose();
            NativeMemory.Free(Access);
            NativeMemory.Free(_buffer);
        }

        private NativeMemoryResult Respond(NativeMemoryRequest request)
        {
            Assert.AreEqual(NativeMemoryOperation.SharedMemory, request._operation);
            Assert.AreEqual(0, request._flags);
            Definition = *(NativeSharedMemoryDefinition*)request._data;
            _cookie = Definition._cookie;
            Name = Marshal.PtrToStringUTF8(Definition._name);
            Identity = new ReadOnlySpan<byte>((void*)Definition._identity, 32).ToArray();
            return new NativeMemoryResult { _value = 83, _data = (nint)Access, _length = (nuint)sizeof(NativeSharedMemoryAccess) };
        }
    }
}
