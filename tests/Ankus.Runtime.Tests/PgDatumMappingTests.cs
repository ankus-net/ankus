using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Tests closed datum converter selection, lazy construction, exact identities and checked native owners.
/// </summary>
[TestClass]
public sealed unsafe class PgDatumMappingTests
{
    /// <summary>
    /// Registration captures scalar, nullable and array identities without a backend lookup or converter.
    /// </summary>
    [TestMethod]
    public void RegistrationDefersUserCodeAndCatalogAccess()
    {
        int constructions = 0;
        PgDatumRegistry.RegisterValue<RegistrationValue>("registered", null, PgTypeOrigin.ThisExtension,
            typeof(Reader<RegistrationValue>),
            () =>
            {
                constructions++;
                return new Reader<RegistrationValue>(_ => new(1));
            }, true, false);
        Assert.AreEqual(0, constructions);
        Assert.AreSame(PgDatumRegistry.Require(typeof(RegistrationValue)), PgDatumRegistry.Require(typeof(RegistrationValue?)));
        Assert.IsNull(PgDatumRegistry.Find(typeof(RegistrationValue[])));
        Assert.IsNull(PgDatumRegistry.Find(typeof(PgArray<RegistrationValue>)));
        Assert.IsNotNull(PgDatumRegistry.FindArray(typeof(RegistrationValue[])));
        Assert.ThrowsExactly<InvalidOperationException>(() => SpiType.GetOid<RegistrationValue[]>());
        DatumTypeMapping first = PgDatumRegistry.Require(typeof(RegistrationValue));
        PgDatumRegistry.RegisterValue<RegistrationValue>("registered", null, PgTypeOrigin.ThisExtension,
            typeof(Reader<RegistrationValue>), static () => throw new InvalidOperationException("Duplicate factory must not run."), true, false);
        Assert.AreSame(first, PgDatumRegistry.Require(typeof(RegistrationValue)));
        Assert.AreSame(first, PgDatumRegistry.Require(typeof(RegistrationValue?)));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<RegistrationValue>(
            "Registered", null, PgTypeOrigin.ThisExtension, typeof(Reader<RegistrationValue>),
            static () => throw new NotSupportedException("Conflicting factory must not run."), true, false));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<RegistrationValue>(
            "registered", "fixed", PgTypeOrigin.ThisExtension, typeof(Reader<RegistrationValue>),
            static () => throw new NotSupportedException("Conflicting factory must not run."), true, false));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<RegistrationValue>(
            "registered", null, PgTypeOrigin.ThisExtension, typeof(Writer<RegistrationValue>),
            static () => throw new NotSupportedException("Conflicting factory must not run."), true, false));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<RegistrationValue>(
            "registered", null, PgTypeOrigin.ThisExtension, typeof(Reader<RegistrationValue>),
            static () => throw new NotSupportedException("Conflicting factory must not run."), true, true));
        Assert.AreEqual(0, constructions);
        var metadata = new PgDatumTypeAttribute("registered", typeof(Reader<RegistrationValue>));
        Assert.AreEqual(PgTypeOrigin.ThisExtension, metadata.Origin);
        Assert.IsNull(metadata.Schema);
        var provider = new PgSqlTypeProviderAttribute("provider", typeof(RegistrationValue));
        Assert.AreEqual(typeof(RegistrationValue), provider.ManagedType);
        Assert.IsNull(provider.Name);
        Assert.AreEqual("provider", provider.SqlId);
    }

    /// <summary>
    /// A generated mapping cannot claim a type with a built-in conversion, or one whose array has one, so SPI
    /// parameters and tuple cells always agree on its OID.
    /// </summary>
    [TestMethod]
    public void RegistrationRejectsBuiltinTypes()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterReference<string>("text_copy", "fixed",
            PgTypeOrigin.External, typeof(object), static () => throw new NotSupportedException("Factory must not run."), true, false));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<int>("int_copy", "fixed",
            PgTypeOrigin.External, typeof(object), static () => throw new NotSupportedException("Factory must not run."), true, false));
        InvalidOperationException array = Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<byte>(
            "byte_copy", "fixed", PgTypeOrigin.External, typeof(object), static () => throw new NotSupportedException("Factory must not run."),
            true, false));
        Assert.Contains("System.Byte[]", array.Message);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTypeRegistry.RegisterReference<string>("text_copy", null,
            static () => throw new NotSupportedException("Factory must not run.")));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTypeRegistry.RegisterValue<Guid>("uuid_copy", null,
            static () => throw new NotSupportedException("Factory must not run.")));
        Assert.IsNull(PgDatumRegistry.Find(typeof(string)));
        Assert.IsNull(PgDatumRegistry.Find(typeof(int?)));
        Assert.IsNull(PgDatumRegistry.FindArray(typeof(byte[])));
        Assert.IsNull(PgTypeRegistry.Find(typeof(string)));
        Assert.IsNull(PgTypeRegistry.Find(typeof(Guid?)));
        Assert.AreEqual(25U, SpiType.GetOid<string>());
        Assert.AreEqual(17U, SpiType.GetOid<byte[]>());
    }

    /// <summary>
    /// Both adapters share one converter, preserve independent literal values and observe changed catalog identities.
    /// </summary>
    [TestMethod]
    public void ReadWriteAdaptersShareFactoryAndResolveEveryOperation()
    {
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        int constructions = 0;
        int reads = 0;
        int writes = 0;
        PgDatumRegistry.RegisterValue<Number>("numeric_value", "fixed", PgTypeOrigin.External, typeof(Converter<Number>), () =>
        {
            constructions++;
            return new Converter<Number>(datum =>
            {
                reads++;
                return new Number((int)datum.DangerousGetBits() + 1);
            },
                (value, oid, destination) =>
                {
                    writes++;
                    Assert.AreEqual(101, destination.Id);
                    return PgDatum.DangerousCreate((nuint)(value.Value + 10), oid, destination);
                });
        }, true, true);
        PgDatumRegistry.RegisterValue<Number>("numeric_value", "fixed", PgTypeOrigin.External, typeof(Converter<Number>),
            static () => throw new InvalidOperationException("A compatible registration must reuse the first converter."), true, true);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<Number>("numeric_value", "fixed",
            PgTypeOrigin.ThisExtension, typeof(Converter<Number>), static () => throw new NotSupportedException("A conflict must not run user code."), true, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<Number>("numeric_value", "Fixed",
            PgTypeOrigin.External, typeof(Converter<Number>), static () => throw new NotSupportedException("A conflict must not run user code."), true, true));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDatumRegistry.RegisterValue<Number>("numeric_value", "fixed",
            PgTypeOrigin.External, typeof(Converter<Number>), static () => throw new NotSupportedException("A conflict must not run user code."), false, true));
        PgDatum datum = PgDatum.DangerousCreate(41, 9001, PgMemoryContext.Current);
        Assert.AreEqual(new Number(42), datum.Read<Number>());
        var input = new NativeValue { Integral = 8 };
        Auxiliary2(ref input) = 9001;
        Assert.AreEqual(new Number(9), input.ReadMapped<Number>());
        NativeValue output = NativeValue.FromMapped(new Number(5));
        try
        {
            Assert.AreEqual(9001L, output.Integral);
            Assert.AreEqual((byte)0, output.IsNull);
            NativeDatumReference reference = MemoryMarshal.Read<NativeDatumReference>(output.ReadBytes());
            Assert.AreEqual((nuint)15, reference._bits);
            Assert.AreEqual(101, reference._context);
            Assert.AreEqual((nuint)901, reference._generation);
        }
        finally
        {
            output.Release();
        }

        backend.Oid = 9002;
        Assert.ThrowsExactly<InvalidCastException>(() => datum.Read<Number>());
        Assert.AreEqual(new Number(2), PgDatum.DangerousCreate(1, 9002, PgMemoryContext.Current).Read<Number>());
        Assert.AreEqual(1, constructions);
        Assert.AreEqual(3, reads);
        Assert.AreEqual(1, writes);
        Assert.AreEqual(1, backend.Copies);
        Assert.HasCount(5, backend.Lookups);
        Assert.IsTrue(backend.Lookups.All(static item => item == ("numeric_value", "fixed")));
    }

    /// <summary>
    /// NULL bypasses user conversion only after exact OID and owner validation, while nonnullable reads still reject it.
    /// </summary>
    [TestMethod]
    public void NullReadsValidateIdentityAndLifetimeWithoutConstructingConverter()
    {
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        int constructions = 0;
        PgDatumRegistry.RegisterValue<NullValue>("nullable_value", null, PgTypeOrigin.ThisExtension,
            typeof(Converter<NullValue>),
            () =>
            {
                constructions++;
                throw new InvalidOperationException("NULL must not construct this converter.");
            }, true, true);
        PgDatum absent = PgDatum.DangerousCreate(0, 9001, PgMemoryContext.Current, isNull: true);
        Assert.IsNull(absent.Read<NullValue?>());
        Assert.ThrowsExactly<InvalidOperationException>(() => absent.Read<NullValue>());
        PgDatum wrong = PgDatum.DangerousCreate(0, 23, PgMemoryContext.Current, isNull: true);
        Assert.ThrowsExactly<InvalidCastException>(() => wrong.Read<NullValue?>());
        memory.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration
            ? new NativeMemoryResult { _value = 902 } : memory.Respond(request);
        int lookups = backend.Lookups.Count;
        Assert.ThrowsExactly<ObjectDisposedException>(() => absent.Read<NullValue?>());
        Assert.HasCount(lookups, backend.Lookups);
        Assert.AreEqual(0, constructions);
    }

    /// <summary>
    /// Unsupported directions fail before factories and backend lookup; type-only defaults need no writer.
    /// </summary>
    [TestMethod]
    public void DirectionsFailBeforeFactoryAndDefaultsRequireOnlyMetadata()
    {
        int constructions = 0;
        PgDatumRegistry.RegisterValue<ReadOnlyValue>("readable", "fixed", PgTypeOrigin.External,
            typeof(Reader<ReadOnlyValue>),
            () =>
            {
                constructions++;
                return new Reader<ReadOnlyValue>(_ => new(7));
            }, true, false);
        PgDatumRegistry.RegisterValue<WriteOnlyValue>("writable", "fixed", PgTypeOrigin.External,
            typeof(Writer<WriteOnlyValue>),
            () =>
            {
                constructions++;
                return new Writer<WriteOnlyValue>((_, oid, owner) => PgDatum.DangerousCreate(7, oid, owner));
            }, false, true);
        Assert.ThrowsExactly<NotSupportedException>(() => NativeValue.FromMapped(new ReadOnlyValue(1)));
        Assert.ThrowsExactly<NotSupportedException>(() => SpiParameter.Create<ReadOnlyValue?>(null));
        Assert.ThrowsExactly<NotSupportedException>(() => new NativeValue().ReadMapped<WriteOnlyValue>());
        using var backend = new BackendScope();
        PgFunctionArgument defaultValue = PgFunctionArgument.Default<ReadOnlyValue>();
        Assert.AreEqual(9001U, defaultValue.TypeOid);
        Assert.IsTrue(defaultValue.IsDefault);
        Assert.HasCount(1, backend.Lookups);
        Assert.AreEqual(0, constructions);
    }

    /// <summary>
    /// A shared SQL OID never selects a converter, and a derived runtime value keeps its declared parameter mapping.
    /// </summary>
    [TestMethod]
    public void DeclaredManagedIdentitySelectsConverterInsteadOfRuntimeTypeOrOid()
    {
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        int rootWrites = 0;
        int derivedWrites = 0;
        PgDatumRegistry.RegisterReference<Message>("shared", "fixed", PgTypeOrigin.External,
            typeof(Converter<Message>),
            () => new Converter<Message>(value => new("root:" + value.DangerousGetBits()), (_, oid, destination) =>
            {
                rootWrites++;
                return PgDatum.DangerousCreate(17, oid, destination);
            }), true, true);
        PgDatumRegistry.RegisterReference<DerivedMessage>("shared", "fixed", PgTypeOrigin.External,
            typeof(Converter<DerivedMessage>),
            () => new Converter<DerivedMessage>(value => new("derived:" + value.DangerousGetBits()), (_, oid, destination) =>
            {
                derivedWrites++;
                return PgDatum.DangerousCreate(99, oid, destination);
            }), true, true);
        PgDatum source = PgDatum.DangerousCreate(42, 9001, PgMemoryContext.Current);
        Assert.AreEqual("root:42", source.Read<Message>().Text);
        Assert.AreEqual("derived:42", source.Read<DerivedMessage>().Text);
        Message value = new DerivedMessage("present");
        PgFunctionArgument argument = PgFunctionArgument.Create(value);
        Assert.AreEqual(9001U, argument.TypeOid);
        Assert.AreSame(value, argument.Parameter.Value);
        NativeValue output = MarshalParameter(argument.Parameter);
        try
        {
            Assert.AreEqual((nuint)17, MemoryMarshal.Read<NativeDatumReference>(output.ReadBytes())._bits);
        }
        finally
        {
            output.Release();
        }

        Assert.AreEqual(1, rootWrites);
        Assert.AreEqual(0, derivedWrites);
    }

    /// <summary>
    /// Writers may produce typed SQL NULL, while wrong types, null handles and expired owners remain errors.
    /// </summary>
    /// <param name="mode">Valid NULL, wrong present/NULL type, expired present/NULL owner, or null handle.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void WriterResultsValidateExactIdentityAndOwnerIncludingNull(int mode)
    {
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        var mapping = new DatumTypeMapping<OutputValue>("output", "fixed", PgTypeOrigin.External, typeof(Writer<OutputValue>),
            () => new Writer<OutputValue>((_, oid, destination) =>
            {
                if (mode == 5)
                {
                    return null!;
                }

                PgDatum result = PgDatum.DangerousCreate(0, mode is 1 or 2 ? 23U : oid, destination, isNull: mode is 0 or 2 or 4);
                if (mode is 3 or 4)
                {
                    memory.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration
                        ? new NativeMemoryResult { _value = 902 } : memory.Respond(request);
                }

                return result;
            }), false, true);
        if (mode == 0)
        {
            NativeValue output = mapping.Write(new OutputValue());
            try
            {
                Assert.AreEqual((byte)1, output.IsNull);
                Assert.AreEqual(9001L, output.Integral);
                Assert.AreEqual((nuint)0, MemoryMarshal.Read<NativeDatumReference>(output.ReadBytes())._bits);
            }
            finally
            {
                output.Release();
            }
        }
        else if (mode is 1 or 2)
        {
            Assert.ThrowsExactly<InvalidCastException>(() => mapping.Write(new OutputValue()));
        }
        else if (mode is 3 or 4)
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => mapping.Write(new OutputValue()));
        }
        else
        {
            InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => mapping.Write(new OutputValue()));
            Assert.AreEqual("A datum writer returned a null handle.", error.Message);
        }
    }

    /// <summary>
    /// Lazy failures preserve exception identity and caching; a present reader result cannot silently become SQL NULL.
    /// </summary>
    [TestMethod]
    public void FactoryFailuresAreCachedAndReaderNullIsRejected()
    {
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        int constructions = 0;
        var failure = new PgException("P8409", "factory failed", detail: "owned detail", hint: "owned hint");
        var mapping = new DatumTypeMapping<FailureValue>("failure", "fixed", PgTypeOrigin.External, typeof(Converter<FailureValue>), () =>
        {
            constructions++;
            throw failure;
        }, true, true);
        PgDatum source = PgDatum.DangerousCreate(42, 9001, PgMemoryContext.Current);
        Assert.AreSame(failure, Assert.ThrowsExactly<PgException>(() => mapping.Read(source)));
        Assert.AreSame(failure, Assert.ThrowsExactly<PgException>(() => mapping.Write(new FailureValue())));
        Assert.AreEqual(1, constructions);
        int ordinaryConstructions = 0;
        var ordinary = new InvalidOperationException("ordinary factory failure");
        var ordinaryFactory = new DatumTypeMapping<FailureValue>("failure", "fixed", PgTypeOrigin.External, typeof(Converter<FailureValue>), () =>
        {
            ordinaryConstructions++;
            throw ordinary;
        }, true, true);
        Assert.AreSame(ordinary, Assert.ThrowsExactly<InvalidOperationException>(() => ordinaryFactory.Read(source)));
        Assert.AreSame(ordinary, Assert.ThrowsExactly<InvalidOperationException>(() => ordinaryFactory.Write(new FailureValue())));
        Assert.AreEqual(1, ordinaryConstructions);
        var nullFactory = new DatumTypeMapping<FailureValue>("failure", "fixed", PgTypeOrigin.External,
            typeof(Converter<FailureValue>), static () => null!, true, true);
        InvalidOperationException first = Assert.ThrowsExactly<InvalidOperationException>(() => nullFactory.Read(source));
        Assert.AreEqual("A datum converter factory returned null.", first.Message);
        Assert.AreSame(first, Assert.ThrowsExactly<InvalidOperationException>(() => nullFactory.Write(new FailureValue())));
        var nullReader = new DatumTypeMapping<NullMessage>("failure", "fixed", PgTypeOrigin.External,
            typeof(Reader<NullMessage>), static () => new Reader<NullMessage>(_ => null!), true, false);
        Assert.AreEqual("A datum reader returned null for a present PostgreSQL value.",
            Assert.ThrowsExactly<InvalidOperationException>(() => nullReader.Read(source)).Message);
    }

    /// <summary>
    /// A retained parameter cannot silently rebind after DDL, even when its CLR value is absent.
    /// </summary>
    [TestMethod]
    public void RetainedParametersRejectChangedOidBeforeWriterForValuesAndNull()
    {
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        int constructions = 0;
        PgDatumRegistry.RegisterValue<RetainedValue>("retained", "fixed", PgTypeOrigin.External, typeof(Writer<RetainedValue>), () =>
        {
            constructions++;
            return new Writer<RetainedValue>((value, oid, destination) => PgDatum.DangerousCreate((nuint)value.Value, oid, destination));
        }, false, true);
        SpiParameter present = SpiParameter.Create(new RetainedValue(42));
        SpiParameter absent = SpiParameter.Create<RetainedValue?>(null);
        Assert.AreEqual(9001U, present.TypeOid);
        Assert.AreEqual(9001U, absent.TypeOid);
        NativeValue nullOutput = MarshalParameter(absent);
        try
        {
            Assert.AreEqual((byte)1, nullOutput.IsNull);
            Assert.IsEmpty(nullOutput.ReadBytes());
        }
        finally
        {
            nullOutput.Release();
        }

        backend.Oid = 9002;
        Assert.ThrowsExactly<InvalidOperationException>(() => MarshalParameter(present));
        Assert.ThrowsExactly<InvalidOperationException>(() => MarshalParameter(absent));
        Assert.AreEqual(0, constructions);
        SpiParameter current = SpiParameter.Create(new RetainedValue(7));
        NativeValue output = MarshalParameter(current);
        try
        {
            Assert.AreEqual(9002L, output.Integral);
            Assert.AreEqual((nuint)7, MemoryMarshal.Read<NativeDatumReference>(output.ReadBytes())._bits);
        }
        finally
        {
            output.Release();
        }

        Assert.AreEqual(1, constructions);
    }

    /// <summary>
    /// Deferred typed result APIs reject mapped targets before backend execution, including a later mixed column.
    /// </summary>
    [TestMethod]
    public void UnsupportedMappedResultsFailBeforeBackendExecution()
    {
        PgDatumRegistry.RegisterValue<UnsupportedValue>("unsupported", "fixed", PgTypeOrigin.External,
            typeof(Converter<UnsupportedValue>),
            static () => throw new InvalidOperationException("No converter should be created."), false, true);
        PgDatumRegistry.RegisterReference<UnsupportedMessage>("unsupported", "fixed", PgTypeOrigin.External,
            typeof(Converter<UnsupportedMessage>),
            static () => throw new InvalidOperationException("No reference converter should be created."), false, true);
        using var backend = new BackendScope();
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalar<UnsupportedValue>("SELECT 1"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalar<UnsupportedValue[]>("SELECT ARRAY[1]"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalar<UnsupportedValue?[]>("SELECT ARRAY[1,NULL]"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalar<PgArray<UnsupportedValue>>("SELECT ARRAY[1]"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalar<PgArray<UnsupportedValue?>>("SELECT ARRAY[1,NULL]"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalar<UnsupportedMessage[]>("SELECT ARRAY['a']"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalar<PgArray<UnsupportedMessage>>("SELECT ARRAY['a']"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalars<PgAnyElement, UnsupportedValue>("SELECT 1,2"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalars<PgAnyElement, UnsupportedValue[]>("SELECT 1,ARRAY[2]"));
        Assert.ThrowsExactly<NotSupportedException>(() => Spi.ExecuteScalars<PgAnyElement, int, PgArray<UnsupportedValue?>>("SELECT 1,2,ARRAY[3,NULL]"));
        Assert.ThrowsExactly<NotSupportedException>(() => PgFunctions.Call<UnsupportedValue>("fixed.value"));
        Assert.ThrowsExactly<NotSupportedException>(() => PgFunctions.Call<UnsupportedValue[]>("fixed.values"));
        Assert.ThrowsExactly<NotSupportedException>(() => PgFunctions.DangerousCall<UnsupportedValue>(1, 0));
        var row = new SpiRow([null], [new SpiColumn("value", 9001)]);
        Assert.ThrowsExactly<NotSupportedException>(() => row.Get<UnsupportedValue?>(0));
        Assert.ThrowsExactly<NotSupportedException>(() => row.Get<UnsupportedValue[]>(0));
        Assert.IsEmpty(backend.Operations);
    }

    /// <summary>
    /// Writer-only arrays reject before raw decoding, while ordinary rows retain CLR array identity guards.
    /// </summary>
    [TestMethod]
    public void WriterOnlyArraysRejectBeforeDecodingOrClrArrayCasts()
    {
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new BackendScope();
        PgDatumRegistry.RegisterValue<ArrayKind>("int4", "pg_catalog", PgTypeOrigin.External,
            typeof(Writer<ArrayKind>), static () => throw new InvalidOperationException("Array conversion must not construct a writer."), false, true);
        PgDatum present = PgDatum.DangerousCreate(0, 1007, PgMemoryContext.Current);
        PgDatum absent = PgDatum.DangerousCreate(0, 1007, PgMemoryContext.Current, isNull: true);
        const string message = "The mapped PostgreSQL type has no datum reader.";
        Assert.AreEqual(message, Assert.ThrowsExactly<NotSupportedException>(() => present.Read<ArrayKind[]>()).Message);
        Assert.AreEqual(message, Assert.ThrowsExactly<NotSupportedException>(() => absent.Read<ArrayKind[]>()).Message);
        Assert.AreEqual(message, Assert.ThrowsExactly<NotSupportedException>(present.Read<PgArray<ArrayKind>>).Message);
        Assert.AreEqual(message, Assert.ThrowsExactly<NotSupportedException>(absent.Read<PgArray<ArrayKind>>).Message);
        Assert.AreEqual(message, Assert.ThrowsExactly<NotSupportedException>(() => absent.Read<ArrayKind?[]>()).Message);
        Assert.AreEqual(message, Assert.ThrowsExactly<NotSupportedException>(absent.Read<PgArray<ArrayKind?>>).Message);
        int[] underlying = [0, 1];
        var row = new SpiRow([underlying], [new SpiColumn("value", 1007)]);
        Assert.AreEqual(message, Assert.ThrowsExactly<NotSupportedException>(() => row.Get<ArrayKind[]>(0)).Message);
        Assert.AreSame(underlying, row.Get<int[]>(0));
        Assert.AreSequenceEqual<int>([0, 1], underlying);
        Assert.IsEmpty(backend.Operations);
    }

    /// <summary>
    /// Marshals the same declared mapping metadata as ordinary SPI invocation.
    /// </summary>
    private static NativeValue MarshalParameter(SpiParameter parameter) => SpiType.ToNative(parameter.Value,
        parameter.CustomMapping, parameter.CustomArrayMapping, parameter.DatumMapping, parameter.TypeOid);

    /// <summary>
    /// Sets the independently specified incoming native type envelope.
    /// </summary>
    private static ref int Auxiliary2(ref NativeValue value) => ref value._auxiliary2;

    /// <summary>
    /// Adapts deterministic test readers without runtime code generation.
    /// </summary>
    private sealed class Reader<T>(Func<PgDatum, T> read) : IPgDatumReader<T>
    {
        /// <inheritdoc />
        public T Read(PgDatum value) => read(value);
    }

    /// <summary>
    /// Adapts deterministic test writers without backend codecs.
    /// </summary>
    private sealed class Writer<T>(Func<T, uint, PgMemoryContext, PgDatum> write) : IPgDatumWriter<T>
    {
        /// <inheritdoc />
        public PgDatum Write(T value, uint typeOid, PgMemoryContext destination) => write(value, typeOid, destination);
    }

    /// <summary>
    /// Exposes both directions through one observed lazy instance.
    /// </summary>
    private sealed class Converter<T>(Func<PgDatum, T> read, Func<T, uint, PgMemoryContext, PgDatum> write)
        : IPgDatumReader<T>, IPgDatumWriter<T>
    {
        /// <inheritdoc />
        public T Read(PgDatum value) => read(value);

        /// <inheritdoc />
        public PgDatum Write(T value, uint typeOid, PgMemoryContext destination) => write(value, typeOid, destination);
    }

    /// <summary>
    /// Supplies exact catalog/copy responses while recording every backend operation.
    /// </summary>
    private sealed class BackendScope : IDisposable
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
        /// Installs this scope's deterministic response state.
        /// </summary>
        internal BackendScope() => Install(this);

        /// <summary>
        /// Gets or sets the current catalog identity returned on the next lookup.
        /// </summary>
        internal uint Oid
        {
            get;
            set;
        } = 9001;

        /// <summary>
        /// Gets captured exact names and optional fixed schemas.
        /// </summary>
        internal List<(string Name, string? Schema)> Lookups { get; } = [];

        /// <summary>
        /// Gets every requested backend operation.
        /// </summary>
        internal List<SpiOperation> Operations { get; } = [];

        /// <summary>
        /// Gets the number of raw input copies.
        /// </summary>
        internal int Copies
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
        /// Returns literal lookup or raw-copy responses without running a database or interpreting a codec payload.
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Invoke(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
        {
            try
            {
                BackendScope scope = s_current!;
                scope.Operations.Add(request->_operation);
                if (request->_operation == SpiOperation.DatumType)
                {
                    scope.Lookups.Add((request->_parameters[0]._value.ReadString(), request->_parameters[1]._value.IsNull != 0
                        ? null : request->_parameters[1]._value.ReadString()));
                    result->_text = new NativeValue { Integral = scope.Oid };
                    return 0;
                }

                if (request->_operation == SpiOperation.Datum && request->_scalarOperation is 2 or 17)
                {
                    scope.Copies++;
                    NativeDatumReference reference = MemoryMarshal.Read<NativeDatumReference>(request->_parameters[0]._value.ReadBytes());
                    result->_text = new NativeValue { Integral = (long)reference._bits };
                    return 0;
                }

                throw new InvalidOperationException("Unexpected test backend operation.");
            }
            // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
            catch (Exception exception)
            {
                NativeError.Write(exception, error);
                return 1;
            }
        }
    }

    /// <summary>
    /// Isolates the registration-only contract.
    /// </summary>
    private readonly record struct RegistrationValue(int Value);

    /// <summary>
    /// Carries independent read and write numeric expectations.
    /// </summary>
    private readonly record struct Number(int Value);

    /// <summary>
    /// Distinguishes nullable value handling from reader output.
    /// </summary>
    private readonly record struct NullValue(int Value);

    /// <summary>
    /// Defines the read-only direction.
    /// </summary>
    private readonly record struct ReadOnlyValue(int Value);

    /// <summary>
    /// Defines the write-only direction.
    /// </summary>
    private readonly record struct WriteOnlyValue(int Value);

    /// <summary>
    /// Defines the declared reference contract.
    /// </summary>
    private record Message(string Text);

    /// <summary>
    /// Defines a separately mapped runtime subtype with the same SQL identity.
    /// </summary>
    private sealed record DerivedMessage(string Text) : Message(Text);

    /// <summary>
    /// Isolates writer-result validation.
    /// </summary>
    private readonly record struct OutputValue;

    /// <summary>
    /// Isolates failed converter construction.
    /// </summary>
    private readonly record struct FailureValue;

    /// <summary>
    /// Supplies a reference reader capable of an invalid null result.
    /// </summary>
    private sealed record NullMessage;

    /// <summary>
    /// Identifies retained parameter metadata independently of other registrations.
    /// </summary>
    private readonly record struct RetainedValue(int Value);

    /// <summary>
    /// Identifies deliberately deferred result paths.
    /// </summary>
    private readonly record struct UnsupportedValue;

    /// <summary>
    /// Identifies deferred reference-element array result paths independently of value nullability.
    /// </summary>
    private sealed record UnsupportedMessage;

    /// <summary>
    /// Exercises the CLR's enum-array and underlying-array compatibility at an unsupported mapping boundary.
    /// </summary>
    private enum ArrayKind
    {
        /// <summary>
        /// Preserves the present zero enum value.
        /// </summary>
        None,

        /// <summary>
        /// Preserves a nonzero enum value.
        /// </summary>
        One,
    }
}
