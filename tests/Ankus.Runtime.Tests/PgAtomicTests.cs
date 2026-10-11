using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies scalar atomic values, admission and managed threads independently of PostgreSQL attachment.
/// </summary>
[TestClass]
public sealed unsafe class PgAtomicTests
{
    /// <summary>
    /// Invalid names, unsupported values and null initialization fail before native registration.
    /// </summary>
    [TestMethod]
    public void AtomicDescriptorsRejectInvalidContracts()
    {
        using var memory = new MemoryContextTestFixture();
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentNullException>(() => new PgAtomic<int>(null!)).ParamName);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => new PgAtomic<int>(string.Empty)).ParamName);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => new PgAtomic<int>("a\0b")).ParamName);
        Assert.ThrowsExactly<NotSupportedException>(() => new PgAtomic<decimal>("decimal"));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgAtomic<Guid>("guid"));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgAtomic<Int128>("wide"));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgAtomic<OneField>("struct"));
        Assert.AreEqual("storage", Assert.ThrowsExactly<ArgumentNullException>(() => PgSharedMemory.Initialize((PgAtomic<int>)null!)).ParamName);
        var storage = new PgAtomic<int>("unregistered");
        Assert.AreEqual("initializer", Assert.ThrowsExactly<ArgumentNullException>(() => PgSharedMemory.Initialize(storage, null!)).ParamName);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgSharedMemory.Initialize(storage));
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Exchange(3));
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.CompareExchange(3, 2));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgAtomic.Add<int>(null!, 1));
        using (MemoryContextTestFixture.Enter())
        {
            Assert.ThrowsExactly<EncoderFallbackException>(() => PgSharedMemory.Initialize(new PgAtomic<int>("bad\uD800")));
        }

        Assert.IsEmpty(memory.Requests);
    }

    /// <summary>
    /// Atomic registration retains exact identity and defers its factory until checked native startup.
    /// </summary>
    [TestMethod]
    public void AtomicRegistrationPreservesIdentityAndInitialization()
    {
        using var fixture = new AtomicFixture();
        var storage = new PgAtomic<ulong>("count café 🐘");
        int calls = 0;
        PgSharedMemory.Initialize(storage, () =>
        {
            calls++;
            return 0xFEDCBA9876543210UL;
        });
        Assert.AreEqual("count café 🐘", storage.Name);
        Assert.AreEqual(storage.Name, fixture.Name);
        Assert.AreEqual(1u, fixture.Definition._kind);
        Assert.AreEqual((nuint)8, fixture.Definition._size);
        Assert.AreSequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(ulong).AssemblyQualifiedName!)), fixture.Identity);
        Assert.AreEqual(0, calls);
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Value);
        PgSharedMemory.Initialize(storage, () => throw new InvalidOperationException("Replaced initializer."));
        Assert.HasCount(1, fixture.Memory.Requests);
        fixture.Initialize();
        fixture.Publish();
        Assert.AreEqual(0xFEDCBA9876543210UL, storage.Value);
        Assert.AreEqual(1, calls);
        fixture.AssertGuards(8);
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    /// <summary>
    /// Rejected native registration releases the old factory and permits a fresh default initialization.
    /// </summary>
    [TestMethod]
    public void AtomicRegistrationFailureCanRetryWithoutOldInitializer()
    {
        using var fixture = new AtomicFixture { FailRegistration = true };
        var storage = new PgAtomic<int>("retry");
        Assert.AreEqual("P7803", Assert.ThrowsExactly<PgException>(() => PgSharedMemory.Initialize(storage, () => 19)).SqlState);
        NativeSharedMemoryDefinition failed = fixture.Definition;
        fixture.FailRegistration = false;
        PgSharedMemory.Initialize(storage);
        Assert.AreNotEqual(failed._cookie, fixture.Definition._cookie);
        NativeCallError error = default;
        Assert.AreEqual(1, fixture.Invoke(failed, fixture.Value, 4, &error));
        Assert.Contains("stale", error.ToException().Message);
        error.Release();
        fixture.Initialize();
        fixture.Publish();
        Assert.AreEqual(0, storage.Value);
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    /// <summary>
    /// A bad native destination cannot invoke the factory or overwrite storage, and a valid retry still initializes it.
    /// </summary>
    /// <param name="scenario">The null address or incorrect byte length.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void AtomicInitializerRejectsInvalidStorageBeforeCallingFactory(int scenario)
    {
        using var fixture = new AtomicFixture();
        var storage = new PgAtomic<int>("initializer");
        int calls = 0;
        PgSharedMemory.Initialize(storage, () =>
        {
            calls++;
            return 73;
        });
        NativeCallError error = default;
        try
        {
            nuint length = scenario switch { 1 => 0, 2 => 3, 3 => 5, _ => 4 };
            Assert.AreEqual(1, fixture.Invoke(fixture.Definition, scenario == 0 ? 0 : fixture.Value, length, &error));
            Assert.Contains("atomic initializer has invalid value storage", error.ToException().Message);
            Assert.AreEqual(0, calls);
            fixture.AssertGuards(0);
        }
        finally
        {
            error.Release();
        }

        fixture.Initialize();
        fixture.Publish();
        Assert.AreEqual(73, storage.Value);
        Assert.AreEqual(1, calls);
        fixture.AssertGuards(4);
    }

    /// <summary>
    /// The final available admission is usable and is balanced without wrapping into the closed bit.
    /// </summary>
    [TestMethod]
    public void AtomicAdmissionAcceptsLastAvailableReader()
    {
        using var fixture = new AtomicFixture();
        PgAtomic<int> storage = fixture.Start(73);
        fixture.Access->_readers = int.MaxValue - 1;
        Assert.AreEqual(73, storage.Exchange(79));
        Assert.AreEqual(int.MaxValue - 1, fixture.Access->_readers);
        fixture.Access->_readers = 0;
        Assert.AreEqual(79, storage.Value);
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    /// <summary>
    /// Malformed native registration output is rejected before dereferencing access memory.
    /// </summary>
    /// <param name="scenario">The malformed result field.</param>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void AtomicRegistrationRejectsInvalidAccessStorage(int scenario)
    {
        using var fixture = new AtomicFixture { InvalidRegistration = scenario };
        var storage = new PgAtomic<long>("invalid");
        Assert.ThrowsExactly<InvalidOperationException>(() => PgSharedMemory.Initialize(storage));
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Value);
        Assert.AreEqual(int.MinValue, fixture.Access->_readers);
    }

    /// <summary>
    /// Unpublished, foreign-process, misaligned and exhausted slots fail without retaining admission.
    /// </summary>
    /// <param name="scenario">The invalid slot state.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void AtomicAccessRejectsUnpublishedOrInvalidStorage(int scenario)
    {
        using var fixture = new AtomicFixture();
        PgAtomic<long> storage = fixture.Start(17L);
        if (scenario == 0)
        {
            fixture.Access->_readers = int.MinValue;
        }
        else if (scenario == 1)
        {
            fixture.Access->_processId = -1;
        }
        else if (scenario == 2)
        {
            fixture.Access->_address = 0;
        }
        else if (scenario == 3)
        {
            fixture.Access->_address++;
        }
        else
        {
            fixture.Access->_readers = int.MaxValue;
        }

        int before = fixture.Access->_readers;
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Increment());
        Assert.AreEqual(before, fixture.Access->_readers);
        Assert.AreEqual(17L, Unsafe.Read<long>((void*)fixture.Value));
        fixture.Publish();
        Assert.AreEqual(18L, storage.Increment());
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    /// <summary>
    /// Every supported scalar width retains high bits and exact exchange/comparison return values.
    /// </summary>
    [TestMethod]
    public void AtomicScalarOperationsPreserveExactBits()
    {
        VerifyScalar(true, false, true);
        VerifyScalar<sbyte>(sbyte.MinValue, sbyte.MaxValue, -17);
        VerifyScalar<byte>(byte.MaxValue, 0, 173);
        VerifyScalar<short>(short.MinValue, short.MaxValue, -12345);
        VerifyScalar<ushort>(ushort.MaxValue, 0, 54321);
        VerifyScalar(int.MinValue, int.MaxValue, -123456789);
        VerifyScalar(uint.MaxValue, 0u, 0xFEDCBA98u);
        VerifyScalar(long.MinValue, long.MaxValue, -912345678901234L);
        VerifyScalar(ulong.MaxValue, 0UL, 0xFEDCBA9876543210UL);
        VerifyScalar(nint.MinValue, nint.MaxValue, -73);
        VerifyScalar(nuint.MaxValue, (nuint)0, 0xFEDCBA98);
        VerifyScalar('\uffff', '\0', '\u1234');
        VerifyScalar(Marker.High, Marker.Zero, Marker.Value);
    }

    /// <summary>
    /// Floating-point comparisons use bits, including distinct NaNs and signed zero.
    /// </summary>
    [TestMethod]
    public void AtomicFloatingPointOperationsPreserveNaNPayloadsAndSignedZero()
    {
        using (var fixture = new AtomicFixture())
        {
            double first = BitConverter.Int64BitsToDouble(0x7FF8000000000073);
            double other = BitConverter.Int64BitsToDouble(0x7FF8000000000074);
            PgAtomic<double> storage = fixture.Start(first);
            Assert.AreEqual(0x7FF8000000000073L, BitConverter.DoubleToInt64Bits(storage.Value));
            Assert.AreEqual(0x7FF8000000000073L, BitConverter.DoubleToInt64Bits(storage.CompareExchange(19, other)));
            Assert.AreEqual(0x7FF8000000000073L, BitConverter.DoubleToInt64Bits(storage.Value));
            Assert.AreEqual(0x7FF8000000000073L, BitConverter.DoubleToInt64Bits(storage.CompareExchange(-0d, first)));
            Assert.AreEqual(long.MinValue, BitConverter.DoubleToInt64Bits(storage.Value));
            Assert.AreEqual(long.MinValue, BitConverter.DoubleToInt64Bits(storage.CompareExchange(23, +0d)));
            Assert.AreEqual(long.MinValue, BitConverter.DoubleToInt64Bits(storage.Exchange(29)));
            Assert.AreEqual(29d, storage.Value);
            fixture.AssertGuards(8);
        }

        using (var fixture = new AtomicFixture())
        {
            float first = BitConverter.Int32BitsToSingle(0x7FC00073);
            PgAtomic<float> storage = fixture.Start(first);
            Assert.AreEqual(0x7FC00073, BitConverter.SingleToInt32Bits(storage.Value));
            Assert.AreEqual(0x7FC00073, BitConverter.SingleToInt32Bits(storage.CompareExchange(-0f, first)));
            Assert.AreEqual(int.MinValue, BitConverter.SingleToInt32Bits(storage.CompareExchange(13, +0f)));
            Assert.AreEqual(int.MinValue, BitConverter.SingleToInt32Bits(storage.Exchange(17)));
            Assert.AreEqual(17f, storage.Value);
            fixture.AssertGuards(4);
        }
    }

    /// <summary>
    /// Arithmetic wraps at each width while bitwise operations return the original value.
    /// </summary>
    [TestMethod]
    public void AtomicArithmeticPreservesDotNetReturnAndWrapContracts()
    {
        VerifyInteger<sbyte>();
        VerifyInteger<byte>();
        VerifyInteger<short>();
        VerifyInteger<ushort>();
        VerifyInteger<int>();
        VerifyInteger<uint>();
        VerifyInteger<long>();
        VerifyInteger<ulong>();
        VerifyInteger<nint>();
        VerifyInteger<nuint>();
        using var fixture = new AtomicFixture();
        PgAtomic<bool> storage = fixture.Start(true);
        Assert.IsTrue(storage.And(false));
        Assert.IsFalse(storage.Value);
        Assert.IsFalse(storage.Or(true));
        Assert.IsTrue(storage.Value);
        Assert.IsTrue(storage.Xor(true));
        Assert.IsFalse(storage.Value);
        fixture.AssertGuards(1);
    }

    /// <summary>
    /// Concurrent managed threads perform exact updates without any PostgreSQL capability on those threads.
    /// </summary>
    [TestMethod]
    public void AtomicOperationsWorkAcrossThreadsWithoutBackendCalls()
    {
        using var fixture = new AtomicFixture();
        PgAtomic<long> storage = fixture.Start(0L);
        var failures = new ConcurrentQueue<Exception>();
        Thread[] workers = [.. Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            try
            {
                for (int index = 0; index < 2000; index++)
                {
                    storage.Increment();
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        }))];
        foreach (Thread thread in workers)
        {
            thread.Start();
        }

        foreach (Thread thread in workers)
        {
            thread.Join();
        }

        Assert.IsEmpty(failures);
        Assert.AreEqual(8000L, storage.Value);
        Assert.HasCount(1, fixture.Memory.Requests);
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    /// <summary>
    /// Retirement rejects new readers, retains the admitted address, and reopens only onto the replacement value.
    /// </summary>
    [TestMethod]
    public void AtomicAdmissionPreservesActiveLeaseAndReplacementSegment()
    {
        using var fixture = new AtomicFixture();
        PgAtomic<long> storage = fixture.Start(73L);
        NativeSharedMemoryAccessLease lease = storage.Open();
        try
        {
            Assert.AreEqual(1, fixture.Access->_readers);
            Interlocked.Or(ref fixture.Access->_readers, int.MinValue);
            Assert.ThrowsExactly<InvalidOperationException>(() => storage.Value);
            Assert.AreEqual(int.MinValue + 1, fixture.Access->_readers);
            Assert.AreEqual(73L, lease.GetReference<long>());
        }
        finally
        {
            lease.Dispose();
        }

        Assert.AreEqual(int.MinValue, fixture.Access->_readers);
        long replacement = -91;
        fixture.Access->_address = (long)&replacement;
        fixture.Access->_readers = 0;
        Assert.AreEqual(-91L, storage.Value);
        Assert.AreEqual(-91L, storage.Exchange(-97));
        Assert.AreEqual(-97L, replacement);
        Assert.AreEqual(73L, Unsafe.Read<long>((void*)fixture.Value));
        lease.Dispose();
        Assert.AreEqual(0, fixture.Access->_readers);
    }

    private static void VerifyScalar<T>(T initial, T replacement, T final) where T : unmanaged
    {
        using var fixture = new AtomicFixture();
        PgAtomic<T> storage = fixture.Start(initial);
        Assert.AreEqual(initial, storage.Value, typeof(T).Name);
        Assert.AreEqual(initial, storage.Exchange(replacement), typeof(T).Name);
        Assert.AreEqual(replacement, storage.Value, typeof(T).Name);
        Assert.AreEqual(replacement, storage.CompareExchange(final, initial), typeof(T).Name);
        Assert.AreEqual(replacement, storage.Value, typeof(T).Name);
        Assert.AreEqual(replacement, storage.CompareExchange(final, replacement), typeof(T).Name);
        Assert.AreEqual(final, storage.Value, typeof(T).Name);
        storage.Value = initial;
        Assert.AreEqual(initial, storage.Value, typeof(T).Name);
        Assert.AreEqual(0, fixture.Access->_readers);
        fixture.AssertGuards(sizeof(T));
    }

    private static void VerifyInteger<T>() where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        using var fixture = new AtomicFixture();
        PgAtomic<T> storage = fixture.Start(T.MaxValue);
        Assert.AreEqual(T.MinValue, storage.Increment(), typeof(T).Name);
        Assert.AreEqual(T.MinValue, storage.Value, typeof(T).Name);
        Assert.AreEqual(T.MaxValue, storage.Decrement(), typeof(T).Name);
        storage.Value = T.CreateChecked(12);
        Assert.AreEqual(T.CreateChecked(42), storage.Add(T.CreateChecked(30)), typeof(T).Name);
        Assert.AreEqual(T.Zero, storage.Subtract(T.CreateChecked(42)), typeof(T).Name);
        storage.Value = T.CreateChecked(0x5A);
        Assert.AreEqual(T.CreateChecked(0x5A), storage.And(T.CreateChecked(0x0F)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x0A), storage.Value, typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x0A), storage.Or(T.CreateChecked(0x30)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x3A), storage.Value, typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x3A), storage.Xor(T.CreateChecked(0x66)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x5C), storage.Value, typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x5C), storage.Max(T.CreateChecked(0x10)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x5C), storage.Max(T.MaxValue), typeof(T).Name);
        Assert.AreEqual(T.MaxValue, storage.Value, typeof(T).Name);
        Assert.AreEqual(T.MaxValue, storage.Min(T.MinValue), typeof(T).Name);
        Assert.AreEqual(T.MinValue, storage.Min(T.Zero), typeof(T).Name);
        Assert.AreEqual(T.MinValue, storage.Value, typeof(T).Name);
        fixture.AssertGuards(sizeof(T));
    }

    private enum Marker : ulong
    {
        Zero = 0,
        Value = 0xFEDCBA9876543210,
        High = ulong.MaxValue,
    }

    private readonly record struct OneField(int Value);

    private sealed class AtomicFixture : IDisposable
    {
        private readonly List<nint> _cookies = [];
        private readonly byte* _buffer = (byte*)NativeMemory.Alloc(32);

        internal AtomicFixture()
        {
            Scope = MemoryContextTestFixture.Enter();
            Memory.Handler = Respond;
            new Span<byte>(_buffer, 32).Fill(0xA5);
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

        internal bool FailRegistration
        {
            get;
            set;
        }

        internal int InvalidRegistration
        {
            get;
            set;
        }

        internal PgAtomic<T> Start<T>(T value) where T : unmanaged
        {
            var storage = new PgAtomic<T>("atomic");
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
                Assert.AreEqual(0, Invoke(Definition, Value, Definition._size, &error));
            }
            finally
            {
                error.Release();
            }
        }

        internal int Invoke(NativeSharedMemoryDefinition definition, nint destination, nuint length, NativeCallError* error)
        {
            var context = new NativeCallbackContext((nint)error, 0, Scope.Address, 0, 0);
            return ((delegate* unmanaged[Cdecl]<nint, nint, nuint, NativeCallbackContext*, int>)definition._initialize)(
                definition._cookie, destination, length, &context);
        }

        internal void Publish()
        {
            Access->_address = Value;
            Access->_processId = Environment.ProcessId;
            Volatile.Write(ref Access->_readers, 0);
        }

        internal void AssertGuards(int size)
        {
            Assert.AreEqual(-1, new ReadOnlySpan<byte>(_buffer, 8).IndexOfAnyExcept((byte)0xA5));
            Assert.AreEqual(-1, new ReadOnlySpan<byte>(_buffer + 8 + size, 24 - size).IndexOfAnyExcept((byte)0xA5));
        }

        public void Dispose()
        {
            Scope.Dispose();
            foreach (nint cookie in _cookies)
            {
                NativeSharedMemoryInitializer.Remove(cookie);
            }

            Memory.Dispose();
            NativeMemory.Free(Access);
            NativeMemory.Free(_buffer);
        }

        private NativeMemoryResult Respond(NativeMemoryRequest request)
        {
            Assert.AreEqual(NativeMemoryOperation.SharedMemory, request._operation);
            Assert.AreEqual(0, request._flags);
            Definition = *(NativeSharedMemoryDefinition*)request._data;
            Name = Marshal.PtrToStringUTF8(Definition._name);
            Identity = new ReadOnlySpan<byte>((void*)Definition._identity, 32).ToArray();
            _cookies.Add(Definition._cookie);
            if (FailRegistration)
            {
                throw new PgException("P7803", "Atomic registration rejected.");
            }

            return new NativeMemoryResult
            {
                _value = InvalidRegistration == 1 ? 0 : 83,
                _data = InvalidRegistration == 2 ? 0 : (nint)Access + (InvalidRegistration == 3 ? 1 : 0),
                _length = InvalidRegistration == 4 ? 15u : (nuint)sizeof(NativeSharedMemoryAccess),
            };
        }
    }
}
