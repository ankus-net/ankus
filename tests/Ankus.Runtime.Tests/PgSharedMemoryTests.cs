using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies managed shared-memory contracts independently of PostgreSQL's locking implementation.
/// </summary>
[TestClass]
public sealed unsafe partial class PgSharedMemoryTests
{
    /// <summary>
    /// Invalid names and null registration arguments fail before reaching native storage.
    /// </summary>
    [TestMethod]
    public void SharedDescriptorsRejectInvalidArguments()
    {
        using var fixture = new MemoryContextTestFixture();
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentNullException>(() => new PgLwLock<int>(null!)).ParamName);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => new PgLwLock<int>(string.Empty)).ParamName);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => new PgLwLock<int>("a\0b")).ParamName);
        Assert.AreEqual("storage", Assert.ThrowsExactly<ArgumentNullException>(() => PgSharedMemory.Initialize((PgLwLock<int>)null!)).ParamName);
        var storage = new PgLwLock<int>("exact name 🐘");
        Assert.AreEqual("exact name 🐘", storage.Name);
        Assert.AreEqual("initializer", Assert.ThrowsExactly<ArgumentNullException>(() => PgSharedMemory.Initialize(storage, null!)).ParamName);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgSharedMemory.Initialize(storage));
        Assert.ThrowsExactly<InvalidOperationException>(storage.Share);
        Assert.ThrowsExactly<InvalidOperationException>(storage.Exclusive);
        using (MemoryContextTestFixture.Enter())
        {
            var invalid = new PgLwLock<int>("bad\uD800");
            Assert.ThrowsExactly<EncoderFallbackException>(() => PgSharedMemory.Initialize(invalid));
        }

        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Registration preserves UTF-8 identity and size, defers construction, and writes exact unaligned bytes.
    /// </summary>
    [TestMethod]
    public void SharedRegistrationDefersInitializerAndPreservesExactValue()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<SharedPair>("state café 🐘");
        int calls = 0;
        SharedPair expected = new(ulong.MaxValue - 13, long.MinValue + 17);
        PgSharedMemory.Initialize(storage, () =>
        {
            calls++;
            return expected;
        });
        Assert.AreEqual(0, calls);
        Assert.AreEqual("state café 🐘", fixture.Name);
        Assert.AreEqual((nuint)16, fixture.Definition._size);
        Assert.AreSequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(typeof(SharedPair).AssemblyQualifiedName!)), fixture.Identity);
        PgSharedMemory.Initialize(storage, () => throw new InvalidOperationException("Must retain the original initializer."));
        Assert.HasCount(1, fixture.Memory.Requests);
        byte* bytes = stackalloc byte[18];
        new Span<byte>(bytes, 18).Fill(0xAB);
        NativeCallError error = default;
        Assert.AreEqual(0, fixture.Initialize((nint)(bytes + 1), 16, &error));
        Assert.AreEqual(expected, Unsafe.ReadUnaligned<SharedPair>(bytes + 1));
        Assert.AreEqual((byte)0xAB, bytes[0]);
        Assert.AreEqual((byte)0xAB, bytes[17]);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(17, NativeMemoryContext.Provider);
    }

    /// <summary>
    /// Wrong initializer storage, foreign providers and stale cookies produce owned errors before the factory runs.
    /// </summary>
    /// <param name="scenario">The invalid callback contract.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void SharedInitializerRejectsInvalidContextAndRecovers(int scenario)
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<long>("value");
        int calls = 0;
        PgSharedMemory.Initialize(storage, () =>
        {
            calls++;
            return -912345678901234;
        });
        long destination = 731;
        NativeCallError error = default;
        nint cookie = scenario == 3 ? fixture.Definition._cookie + 500 : fixture.Definition._cookie;
        using (MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter(scenario == 2 ? 19 : 17))
        {
            var context = new NativeCallbackContext((nint)(&error), 0, scope.Address, 0, 0);
            int status = ((delegate* unmanaged[Cdecl]<nint, nint, nuint, NativeCallbackContext*, int>)fixture.Definition._initialize)(
                cookie, scenario == 0 ? 0 : (nint)(&destination), scenario switch
                {
                    1 => 7u,
                    4 => 9u,
                    5 => nuint.MaxValue,
                    _ => 8u,
                }, &context);
            Assert.AreEqual(1, status);
            PgException failure = error.ToException();
            error.Release();
            Assert.AreEqual("38000", failure.SqlState);
            Assert.Contains(scenario is < 2 or > 3 ? "invalid value storage" : scenario == 2 ? "another extension provider" : "stale", failure.Message);
        }

        Assert.AreEqual(0, calls);
        Assert.AreEqual(731L, destination);
        Assert.AreEqual(0, fixture.Initialize((nint)(&destination), 8, &error));
        Assert.AreEqual(-912345678901234L, destination);
        Assert.AreEqual(1, calls);
    }

    /// <summary>
    /// A failed registration releases its unpublished root and can be retried; default initialization writes zero.
    /// </summary>
    [TestMethod]
    public void SharedRegistrationFailureCanRetryWithoutPublishingOldInitializer()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<long>("retry");
        fixture.FailRegistration = true;
        PgException failure = Assert.ThrowsExactly<PgException>(() => PgSharedMemory.Initialize(storage, () => 29));
        Assert.AreEqual("P7801", failure.SqlState);
        NativeSharedMemoryDefinition failed = fixture.Definition;
        fixture.FailRegistration = false;
        PgSharedMemory.Initialize(storage);
        Assert.AreNotEqual(failed._cookie, fixture.Definition._cookie);
        long value = 73;
        NativeCallError error = default;
        var context = new NativeCallbackContext((nint)(&error), 0, fixture.Scope.Address, 0, 0);
        int status = ((delegate* unmanaged[Cdecl]<nint, nint, nuint, NativeCallbackContext*, int>)failed._initialize)(
            failed._cookie, (nint)(&value), 8, &context);
        Assert.AreEqual(1, status);
        Assert.Contains("stale", error.ToException().Message);
        error.Release();
        Assert.AreEqual(73L, value);
        Assert.AreEqual(0, fixture.Initialize((nint)(&value), 8, &error));
        Assert.AreEqual(0L, value);
        Assert.IsGreaterThan(0, fixture.Memory.ErrorReleases);
    }

    /// <summary>
    /// Factory failures retain exact PostgreSQL diagnostics and restore the enclosing memory binding.
    /// </summary>
    [TestMethod]
    public void SharedInitializerTransportsErrorsAndRestoresCapabilities()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<int>("throws");
        int calls = 0;
        PgSharedMemory.Initialize(storage, () =>
        {
            calls++;
            Assert.AreEqual(17, NativeMemoryContext.Provider);
            throw new PgException("P7802", "Initializer failed.", "Details.", "Retry.");
        });
        int destination = 19;
        NativeCallError error = default;
        Assert.AreEqual(1, fixture.Initialize((nint)(&destination), 4, &error));
        PgException failure = error.ToException();
        error.Release();
        Assert.AreEqual("P7802", failure.SqlState);
        Assert.AreEqual("Initializer failed.", failure.Message);
        Assert.AreEqual("Details.", failure.Detail);
        Assert.AreEqual("Retry.", failure.Hint);
        Assert.AreEqual(19, destination);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(17, NativeMemoryContext.Provider);
    }

    private readonly record struct SharedPair(ulong First, long Second);

    private sealed class SharedFixture : IDisposable
    {
        private readonly List<nint> _cookies = [];
        private nint _nextLease = 20;

        internal SharedFixture()
        {
            Scope = MemoryContextTestFixture.Enter();
            Memory.Handler = Respond;
        }

        internal MemoryContextTestFixture Memory { get; } = new();
        internal MemoryContextTestFixture.Scope Scope { get; }
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

        internal byte[] Bytes
        {
            get;
            set
            {
                field = GC.AllocateArray<byte>(value.Length, pinned: true);
                value.CopyTo(field, 0);
            }
        } = [];

        internal int Initialize(nint destination, nuint size, NativeCallError* error)
        {
            var context = new NativeCallbackContext((nint)error, 0, Scope.Address, 0, 0);
            return ((delegate* unmanaged[Cdecl]<nint, nint, nuint, NativeCallbackContext*, int>)Definition._initialize)(
                Definition._cookie, destination, size, &context);
        }

        public void Dispose()
        {
            Scope.Dispose();
            foreach (nint cookie in _cookies)
            {
                NativeSharedMemoryInitializer.Remove(cookie);
            }

            Memory.Dispose();
        }

        private NativeMemoryResult Respond(NativeMemoryRequest request)
        {
            Assert.AreEqual(NativeMemoryOperation.SharedMemory, request._operation);
            switch (request._flags)
            {
                case 0:
                    Definition = *(NativeSharedMemoryDefinition*)request._data;
                    Name = Marshal.PtrToStringUTF8(Definition._name);
                    Identity = new ReadOnlySpan<byte>((void*)Definition._identity, 32).ToArray();
                    _cookies.Add(Definition._cookie);
                    if (FailRegistration)
                    {
                        throw new PgException("P7801", "Registration rejected.");
                    }

                    return new NativeMemoryResult { _value = 71 };
                case 1:
                case 2:
                    Assert.AreEqual(71, request._context);
                    return new NativeMemoryResult { _value = _nextLease++ };
                case 3:
                    Bytes.CopyTo(new Span<byte>((void*)request._data, checked((int)request._length)));
                    break;
                case 4:
                    Bytes = new ReadOnlySpan<byte>((void*)request._data, checked((int)request._length)).ToArray();
                    break;
                case 6:
                case 7:
                    return new NativeMemoryResult
                    {
                        _data = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(Bytes)),
                        _length = (nuint)Bytes.Length,
                    };
            }

            return default;
        }
    }
}
