using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Tests detached mapped ranges and validates the managed boundary against scripted native responses.
/// </summary>
[TestClass]
public sealed class PgDatumRangeTests
{
    /// <summary>
    /// Registration and detached construction never resolve a catalog identity or create the scalar converter.
    /// </summary>
    [TestMethod]
    public void RangeRegistrationAndConstructionRemainBackendFree()
    {
        PgDatumRegistry.RegisterValue<RegistrationBound>("bound", null, PgTypeOrigin.ThisExtension, typeof(Converter),
            static () => throw new InvalidOperationException("Construction must remain lazy."), true, true);
        PgDatumRegistry.RegisterRange<RegistrationBound>("range", null, PgTypeOrigin.ThisExtension);
        DatumTypeMapping mapping = PgDatumRegistry.Require(typeof(PgRange<RegistrationBound>));
        Assert.AreSame(PgDatumRegistry.Require(typeof(RegistrationBound)), mapping.RangeBound);
        Assert.IsNotNull(PgDatumRegistry.FindArray(typeof(PgRange<RegistrationBound>[])));
        Assert.IsNotNull(PgDatumRegistry.FindArray(typeof(PgArray<PgRange<RegistrationBound>>)));
        var empty = new PgRange<RegistrationBound>();
        var infinite = new PgRange<RegistrationBound>(null, null, true, true);
        var finite = new PgRange<RegistrationBound>(new(3), new(5), false, true);
        Assert.IsTrue(empty.IsEmpty);
        Assert.IsFalse(empty.IsUnbounded);
        Assert.IsTrue(infinite.IsUnbounded);
        Assert.IsFalse(infinite.LowerInclusive);
        Assert.IsFalse(infinite.UpperInclusive);
        Assert.AreEqual(new RegistrationBound(3), finite.Lower);
        Assert.AreEqual(new RegistrationBound(5), finite.Upper);
        Assert.IsFalse(finite.LowerInclusive);
        Assert.IsTrue(finite.UpperInclusive);
        Assert.AreEqual(finite, new PgRange<RegistrationBound>(new(3), new(5), false, true));
        Assert.AreNotEqual(empty, infinite);
        PgDatumRegistry.RegisterRange<RegistrationBound>("range", null, PgTypeOrigin.ThisExtension);
        Assert.AreSame(mapping, PgDatumRegistry.Require(typeof(PgRange<RegistrationBound>)));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterRange<RegistrationBound>("other", null, PgTypeOrigin.ThisExtension));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgRange<UnmappedBound>());
        var metadata = new PgRangeTypeAttribute(typeof(RegistrationBound), "range");
        Assert.AreEqual(typeof(RegistrationBound), metadata.ManagedType);
        Assert.AreEqual("range", metadata.Name);
        Assert.AreEqual(PgTypeOrigin.ThisExtension, metadata.Origin);
        Assert.IsNull(metadata.Schema);
    }

    /// <summary>
    /// Independent native flags convert only finite raw values and expire temporary handles without consuming the source.
    /// </summary>
    /// <param name="flags">The independent native inclusion and absence flags.</param>
    /// <param name="lower">The expected logical lower bound.</param>
    /// <param name="upper">The expected logical upper bound.</param>
    /// <param name="reads">The exact number of scalar reader calls.</param>
    [TestMethod]
    [DataRow(1, null, null, 0)]
    [DataRow(24, null, null, 0)]
    [DataRow(8, null, 18, 1)]
    [DataRow(18, 10, null, 1)]
    [DataRow(4, 10, 18, 2)]
    public void RangeReadsPreserveFlagsValuesAndTemporaryLifetimes(int flags, int? lower, int? upper, int reads)
    {
        using MemoryContextTestFixture memory = CreateMemory();
        using MemoryContextTestFixture.Scope callback = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope { Flags = flags };
        Register();
        PgDatum source = PgDatum.DangerousCreate(501, 9002, PgMemoryContext.Current);
        PgRange<Bound> value = source.Read<PgRange<Bound>>();
        Assert.AreEqual(Expected(lower), value.Lower);
        Assert.AreEqual(Expected(upper), value.Upper);
        Assert.AreEqual(flags == 1, value.IsEmpty);
        Assert.AreEqual((flags & 2) != 0, value.LowerInclusive);
        Assert.AreEqual((flags & 4) != 0, value.UpperInclusive);
        Assert.AreEqual(reads, backend.Reads);
        Assert.AreEqual(1, backend.Releases);
        Assert.ContainsSingle(memory.Requests.Where(static request => request._operation == NativeMemoryOperation.Delete));
        Assert.AreEqual((nuint)501, source.DangerousGetBits());
        if (backend.Captured is { } captured)
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => captured.DangerousGetBits());
        }

        static Bound? Expected(int? number) => number is int present ? new Bound(present) : null;
    }

    /// <summary>
    /// Malformed native headers fail before converting a bound and still release both native and context ownership.
    /// </summary>
    /// <param name="flags">The impossible native flag combination.</param>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(3)]
    [DataRow(10)]
    [DataRow(20)]
    [DataRow(32)]
    public void RangeReadsRejectMalformedNativeFlags(int flags)
    {
        using MemoryContextTestFixture memory = CreateMemory();
        using MemoryContextTestFixture.Scope callback = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope { Flags = flags };
        Register();
        PgDatum source = PgDatum.DangerousCreate(501, 9002, PgMemoryContext.Current);
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(source.Read<PgRange<Bound>>);
        Assert.AreEqual("Invalid native mapped range header.", error.Message);
        Assert.AreEqual(0, backend.Reads);
        Assert.AreEqual(1, backend.Releases);
        Assert.ContainsSingle(memory.Requests.Where(static request => request._operation == NativeMemoryOperation.Delete));
    }

    /// <summary>
    /// Whole SQL NULL still checks the current range-subtype relationship without invoking either scalar direction.
    /// </summary>
    [TestMethod]
    public void RangeNullChecksCurrentSubtypeAndCapturedRangeIdentity()
    {
        using MemoryContextTestFixture memory = CreateMemory();
        using MemoryContextTestFixture.Scope callback = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        Register();
        PgDatum source = PgDatum.DangerousCreate(0, 9002, PgMemoryContext.Current, isNull: true);
        Assert.IsNull(source.Read<PgRange<Bound>?>());
        SpiParameter retained = SpiParameter.Create<PgRange<Bound>?>(null);
        backend.Subtype = 23;
        InvalidCastException subtype = Assert.ThrowsExactly<InvalidCastException>(source.Read<PgRange<Bound>?>);
        Assert.AreEqual("PostgreSQL range subtype OID 23 does not match mapped bound type OID 9001.", subtype.Message);
        backend.Subtype = 9001;
        backend.RangeOid = 9003;
        Assert.ThrowsExactly<InvalidCastException>(source.Read<PgRange<Bound>?>);
        Assert.ThrowsExactly<InvalidOperationException>(() => retained.DatumMapping!.Write(retained.Value, retained.TypeOid));
        Assert.AreEqual(0, backend.Reads);
        Assert.AreEqual(0, backend.Writes);
        Assert.AreEqual(0, backend.Releases);
    }

    /// <summary>
    /// Writes preserve independent finite values and build into the requested owner before temporary bound disposal.
    /// </summary>
    [TestMethod]
    public void RangeWritesPreserveBoundEnvelopesAndFinalOwner()
    {
        using MemoryContextTestFixture memory = CreateMemory();
        using MemoryContextTestFixture.Scope callback = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        Register();
        DatumTypeMapping mapping = PgDatumRegistry.Require(typeof(PgRange<Bound>));
        PgDatum output = mapping.WriteDatum(new PgRange<Bound>(new(13), new(19), false, true), 9002, PgMemoryContext.Current);
        Assert.AreEqual((nuint)777, output.DangerousGetBits());
        Assert.AreEqual(9002u, output.TypeOid);
        Assert.AreEqual(4, backend.WrittenFlags);
        Assert.AreSequenceEqual<long?>([3, 9], backend.WrittenBounds);
        Assert.AreEqual(101, backend.Destination);
        Assert.AreEqual(2, backend.Writes);
        Assert.ContainsSingle(memory.Requests.Where(static request => request._operation == NativeMemoryOperation.Delete));
        _ = mapping.WriteDatum(new PgRange<Bound>(null, new(19), true, false), 9002, PgMemoryContext.Current);
        Assert.AreEqual(8, backend.WrittenFlags);
        Assert.AreSequenceEqual<long?>([null, 9], backend.WrittenBounds);
        _ = mapping.WriteDatum(new PgRange<Bound>(), 9002, PgMemoryContext.Current);
        Assert.AreEqual(1, backend.WrittenFlags);
        Assert.AreSequenceEqual<long?>([null, null], backend.WrittenBounds);
        Assert.AreEqual(3, backend.Writes);
    }

    /// <summary>
    /// Registers one stable scalar contract shared by all scripted per-thread cases.
    /// </summary>
    private static void Register()
    {
        PgDatumRegistry.RegisterValue<Bound>("bound", "mapped", PgTypeOrigin.External, typeof(Converter), static () => new Converter(), true, true);
        PgDatumRegistry.RegisterRange<Bound>("range", "mapped", PgTypeOrigin.External);
    }

    /// <summary>
    /// Exposes a callback parent for temporary range storage in the independent memory fixture.
    /// </summary>
    private static MemoryContextTestFixture CreateMemory()
    {
        var memory = new MemoryContextTestFixture();
        var generations = new Dictionary<nint, nint>();
        nint nextContext = 201;
        memory.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.Callback)
            {
                return new NativeMemoryResult { _context = 101 };
            }

            if (request._operation == NativeMemoryOperation.Create)
            {
                return new NativeMemoryResult { _context = ++nextContext };
            }

            if (request._operation == NativeMemoryOperation.Delete)
            {
                generations[request._context] = generations.GetValueOrDefault(request._context, 901) + 1;
            }

            return request._operation == NativeMemoryOperation.CaptureGeneration
                ? new NativeMemoryResult { _value = generations.GetValueOrDefault(request._context, 901) } : memory.Respond(request);
        };
        return memory;
    }

    /// <summary>
    /// Carries an independently transformed scalar bound.
    /// </summary>
    private readonly record struct Bound(int Number);

    /// <summary>
    /// Isolates registration-only assertions from conversions in other cases.
    /// </summary>
    private readonly record struct RegistrationBound(int Number);

    /// <summary>
    /// Identifies a value type with no generated range metadata.
    /// </summary>
    private readonly record struct UnmappedBound;

    /// <summary>
    /// Applies independently expected numeric transformations while observing borrowed handles.
    /// </summary>
    private sealed class Converter : IPgDatumReader<Bound>, IPgDatumWriter<Bound>
    {
        /// <inheritdoc />
        public Bound Read(PgDatum value)
        {
            BackendScope.Current.Reads++;
            BackendScope.Current.Captured = value;
            return new(checked((int)value.DangerousGetBits() + 10));
        }

        /// <inheritdoc />
        public PgDatum Write(Bound value, uint typeOid, PgMemoryContext destination)
        {
            BackendScope.Current.Writes++;
            return PgDatum.DangerousCreate(checked((nuint)(value.Number - 10)), typeOid, destination);
        }
    }

    /// <summary>
    /// Supplies raw native bounds and captures writes without modeling PostgreSQL canonicalization.
    /// </summary>
    private sealed unsafe class BackendScope : IDisposable
    {
        [ThreadStatic]
        private static BackendScope? s_current;

        /// <summary>
        /// Makes <paramref name="current"/> the BackendScope this thread's native callbacks use, or restores the enclosing one.
        /// </summary>
        private static void Install(BackendScope? current) => s_current = current;
        private readonly BackendScope? _previousScope = s_current;
        private readonly nint _previous = NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Invoke);

        /// <summary>
        /// Installs this case's isolated native response state.
        /// </summary>
        internal BackendScope() => Install(this);

        /// <summary>
        /// Gets the thread's active observation scope.
        /// </summary>
        internal static BackendScope Current => s_current!;

        /// <summary>
        /// Gets or sets the native flags returned for raw bounds.
        /// </summary>
        internal int Flags
        {
            get;
            set;
        } = 2;

        /// <summary>
        /// Gets or sets the catalog range identity.
        /// </summary>
        internal uint RangeOid
        {
            get;
            set;
        } = 9002;

        /// <summary>
        /// Gets or sets the subtype obtained independently from the range catalog.
        /// </summary>
        internal uint Subtype
        {
            get;
            set;
        } = 9001;

        /// <summary>
        /// Gets or sets observed finite reads.
        /// </summary>
        internal int Reads
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets observed finite writes.
        /// </summary>
        internal int Writes
        {
            get;
            set;
        }

        /// <summary>
        /// Gets native result releases.
        /// </summary>
        internal int Releases
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets or sets the last borrowed scalar handle.
        /// </summary>
        internal PgDatum? Captured
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the last construction's independent flags.
        /// </summary>
        internal int WrittenFlags
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the last construction's raw finite bound bits or NULL markers.
        /// </summary>
        internal long?[] WrittenBounds { get; } = new long?[2];

        /// <summary>
        /// Gets the selected final result context.
        /// </summary>
        internal nint Destination
        {
            get;
            private set;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            NativeBackend.Exit(_previous);
            Install(_previousScope);
        }

        /// <summary>
        /// Returns literal metadata and independent bounds while keeping exceptions inside the callback.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Invoke(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
        {
            try
            {
                BackendScope scope = Current;
                if (request->_operation == SpiOperation.DatumType)
                {
                    result->_text.Integral = request->_parameters[0]._value.ReadString() == "bound" ? 9001 : scope.RangeOid;
                }
                else if (request->_operation == SpiOperation.Range && request->_scalarOperation == (int)RangeOperation.Subtype)
                {
                    result->_text.Integral = scope.Subtype;
                }
                else if (request->_operation == SpiOperation.Datum && request->_scalarOperation == 4)
                {
                    result->_text.Integral = scope.Flags;
                    result->_resultTypeOid = scope.Subtype;
                    result->_rowCount = 2;
                    result->_columnCount = 1;
                    result->_values = (NativeValue*)NativeMemory.AllocZeroed(2, (nuint)sizeof(NativeValue));
                    result->_values[0] = new NativeValue { Integral = 0, IsNull = (byte)(scope.Flags == 1 || (scope.Flags & 8) != 0 ? 1 : 0) };
                    result->_values[1] = new NativeValue { Integral = 8, IsNull = (byte)(scope.Flags == 1 || (scope.Flags & 16) != 0 ? 1 : 0) };
                    result->_release = &Release;
                }
                else if (request->_operation == SpiOperation.Range && request->_scalarOperation == (int)RangeOperation.BuildMapped)
                {
                    scope.WrittenFlags = checked((int)request->_parameters[0]._value.Integral);
                    scope.Destination = request->_resultContext;
                    for (int index = 0; index < 2; index++)
                    {
                        NativeSpiParameter parameter = request->_parameters[index + 1];
                        Assert.AreEqual(9001u, parameter._typeOid);
                        scope.WrittenBounds[index] = parameter._value.IsNull != 0 ? null :
                            checked((long)MemoryMarshal.Read<NativeDatumReference>(parameter._value.ReadBytes())._bits);
                    }

                    result->_text.Integral = 777;
                    result->_resultTypeOid = scope.RangeOid;
                }
                else
                {
                    throw new InvalidOperationException("Unexpected mapped range test operation.");
                }

                return 0;
            }
            // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
            catch (Exception exception)
            {
                NativeError.Write(exception, error);
                return 1;
            }
        }

        /// <summary>
        /// Balances the exact native allocator and records cleanup independently of conversion success.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(NativeSpiResult* result)
        {
            Current.Releases++;
            NativeMemory.Free(result->_values);
        }
    }
}
