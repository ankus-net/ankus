using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Tests catalog snapshot normalization, transport ownership, discriminators and default-list requests.
/// </summary>
[TestClass]
public sealed unsafe class FunctionCatalogTests
{
    [ThreadStatic]
    private static Script? s_script;

    /// <summary>
    /// Missing rows preserve every OID bit and still require a live backend capability.
    /// </summary>
    [TestMethod]
    public void MissingCatalogRows()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => PgFunctions.GetInfo(0));
        using var script = new Script { Missing = true };
        foreach (uint oid in new uint[] { 0, 42, uint.MaxValue })
        {
            Assert.IsNull(PgFunctions.GetInfo(oid));
            Assert.AreEqual(oid, script.RequestedOid);
        }

        Assert.AreEqual(3, script.Releases);
    }

    /// <summary>
    /// Every scalar and array remains exact and detached after native buffers are released.
    /// </summary>
    [TestMethod]
    public void MetadataCopiesAndNormalizes()
    {
        PgFunctionInfo snapshot;
        using (var script = new Script { Rich = true })
        {
            snapshot = PgFunctions.GetInfo(uint.MaxValue)!;
            Assert.AreEqual(uint.MaxValue, snapshot.Oid);
            Assert.AreEqual((byte)34, script.Operation);
            Assert.AreEqual(3, script.Selector);
            Assert.AreEqual(26U, script.ParameterOid);
            Assert.AreEqual(1, script.Releases);
        }

        Assert.AreEqual(uint.MaxValue - 1, snapshot.OwnerOid);
        Assert.AreEqual(1.25F, snapshot.Cost);
        Assert.AreEqual(123.5F, snapshot.Rows);
        Assert.AreEqual(23U, snapshot.VariadicElementTypeOid);
        Assert.AreEqual(99U, snapshot.SupportFunctionOid);
        Assert.AreEqual(PgFunctionKind.Function, snapshot.Kind);
        Assert.IsTrue(snapshot.IsSecurityDefiner);
        Assert.IsTrue(snapshot.IsLeakProof);
        Assert.AreEqual(14U, snapshot.LanguageOid);
        Assert.AreEqual("source café 🐘", snapshot.Source);
        Assert.AreEqual("$libdir/example", snapshot.Binary);
        Assert.AreSequenceEqual(["search_path=pg_catalog", "work_mem=4MB"], snapshot.Configuration!);
        Assert.AreSequenceEqual([PgArgumentMode.In, PgArgumentMode.Out, PgArgumentMode.InOut, PgArgumentMode.Variadic, PgArgumentMode.Table], snapshot.ArgumentModes);
        Assert.AreEqual(3, snapshot.InputArgumentCount);
        Assert.AreEqual(1, snapshot.DefaultArgumentCount);
        Assert.AreSequenceEqual<string?>(["", "名", null, "many", "result"], snapshot.ArgumentNames);
        Assert.AreSequenceEqual<uint>([23, 25, 1007], snapshot.InputArgumentTypeOids);
        Assert.AreSequenceEqual<uint>([23, 20, 25, 1007, 16], snapshot.AllArgumentTypeOids);
        Assert.AreEqual(2249U, snapshot.ReturnTypeOid);
        Assert.IsTrue(snapshot.IsStrict);
        Assert.AreEqual(PgVolatility.Immutable, snapshot.Volatility);
        Assert.AreEqual(PgParallelSafety.Safe, snapshot.ParallelSafety);
        Assert.IsTrue(snapshot.ReturnsSet);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<uint>)snapshot.InputArgumentTypeOids)[0] = 0);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<uint>)snapshot.AllArgumentTypeOids)[0] = 0);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<string?>)snapshot.ArgumentNames)[0] = "changed");
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<PgArgumentMode>)snapshot.ArgumentModes)[0] = PgArgumentMode.Table);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<string>)snapshot.Configuration!)[0] = "changed");
    }

    /// <summary>
    /// Absent arrays preserve the pgrx all-input and unnamed fallbacks, including zero arguments.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    public void AbsentFieldsNormalize(int count)
    {
        using var script = new Script { ArgumentCount = count };
        PgFunctionInfo info = PgFunctions.GetInfo(42)!;
        Assert.AreEqual(count, info.InputArgumentCount);
        Assert.AreSequenceEqual(Enumerable.Repeat<uint>(23, count), info.InputArgumentTypeOids);
        Assert.AreSequenceEqual(Enumerable.Repeat<uint>(23, count), info.AllArgumentTypeOids);
        Assert.AreSequenceEqual(Enumerable.Repeat<string?>(null, count), info.ArgumentNames);
        Assert.AreSequenceEqual(Enumerable.Repeat(PgArgumentMode.In, count), info.ArgumentModes);
        Assert.IsNull(info.VariadicElementTypeOid);
        Assert.AreEqual(0U, info.SupportFunctionOid);
        Assert.IsNull(info.Binary);
        Assert.IsNull(info.Configuration);
        Assert.AreEqual(0, info.DefaultArgumentCount);
        Assert.IsFalse(info.IsSecurityDefiner);
        Assert.IsFalse(info.IsLeakProof);
        Assert.IsFalse(info.IsStrict);
        Assert.IsFalse(info.ReturnsSet);
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        Assert.IsNull(info.GetDefaultArguments(PgMemoryContext.Current));
        Assert.AreEqual("context", Assert.ThrowsExactly<ArgumentNullException>(() => info.GetDefaultArguments(null!)).ParamName);
        Assert.HasCount(1, memory.Requests);
    }

    /// <summary>
    /// Every supported catalog discriminator maps explicitly, independent of managed enum ordinals.
    /// </summary>
    [TestMethod]
    [DataRow('f', 'i', 's', PgFunctionKind.Function, PgVolatility.Immutable, PgParallelSafety.Safe)]
    [DataRow('p', 's', 'r', PgFunctionKind.Procedure, PgVolatility.Stable, PgParallelSafety.Restricted)]
    [DataRow('a', 'v', 'u', PgFunctionKind.Aggregate, PgVolatility.Volatile, PgParallelSafety.Unsafe)]
    [DataRow('w', 'i', 'u', PgFunctionKind.Window, PgVolatility.Immutable, PgParallelSafety.Unsafe)]
    public void DiscriminatorsMap(char kind, char volatility, char parallel, PgFunctionKind expectedKind,
        PgVolatility expectedVolatility, PgParallelSafety expectedParallel)
    {
        using var script = new Script { Kind = kind, Volatility = volatility, Parallel = parallel };
        PgFunctionInfo info = PgFunctions.GetInfo(42)!;
        Assert.AreEqual(expectedKind, info.Kind);
        Assert.AreEqual(expectedVolatility, info.Volatility);
        Assert.AreEqual(expectedParallel, info.ParallelSafety);
    }

    /// <summary>
    /// Unknown native discriminators are rejected after owned buffers are released, and retry succeeds.
    /// </summary>
    [TestMethod]
    [DataRow(5)]
    [DataRow(12)]
    [DataRow(20)]
    [DataRow(21)]
    public void DiscriminatorsRejectUnknownValues(int field)
    {
        using var script = new Script { InvalidField = field };
        Assert.ThrowsExactly<InvalidOperationException>(() => PgFunctions.GetInfo(42));
        Assert.AreEqual(1, script.Releases);
        script.InvalidField = -1;
        Assert.AreEqual(42U, PgFunctions.GetInfo(42)!.Oid);
        Assert.AreEqual(2, script.Releases);
    }

    /// <summary>
    /// Native errors, malformed result frames and abort restrictions cannot leak results or poison a retry.
    /// </summary>
    [TestMethod]
    public void GuardFailuresReleaseAndRecover()
    {
        using var script = new Script { Fail = true };
        PgException error = Assert.ThrowsExactly<PgException>(() => PgFunctions.GetInfo(42));
        Assert.AreEqual("42501", error.SqlState);
        Assert.AreEqual("catalog denied café", error.Message);
        Assert.AreEqual(1, script.Releases);
        script.Fail = false;
        script.Malformed = true;
        Assert.ThrowsExactly<InvalidOperationException>(() => PgFunctions.GetInfo(42));
        Assert.AreEqual(2, script.Releases);
        script.Malformed = false;
        Assert.AreEqual(42U, PgFunctions.GetInfo(42)!.Oid);
        nint previous = NativeBackend.Enter(Script.Pointer, abortCleanup: true);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => PgFunctions.GetInfo(42));
        }
        finally
        {
            NativeBackend.Exit(previous, abortCleanup: true);
        }

        Assert.AreEqual(3, script.Releases);
    }

    /// <summary>
    /// Defaults carry exact UTF-8 text, expected count and explicit owner into an owned pointer list.
    /// </summary>
    [TestMethod]
    public void DefaultsUseExplicitOwner()
    {
        using var script = new Script { Rich = true };
        PgFunctionInfo info = PgFunctions.GetInfo(42)!;
        using var memory = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        bool fail = false;
        memory.Handler = request =>
        {
            if (request._operation != NativeMemoryOperation.List)
            {
                return memory.Respond(request);
            }

            if (request._flags == 15)
            {
                Assert.AreEqual<nint>(101, request._context);
                Assert.AreEqual<nint>(1, request._value);
                Assert.AreEqual("(catalog café)", Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)request._data, checked((int)request._length))));
                if (fail)
                {
                    throw new PgException("22023", "bad catalog nodes");
                }

                return new NativeMemoryResult { _pointer = 717 };
            }

            Assert.AreEqual<nint>(717, request._context);
            return new NativeMemoryResult { _context = 101, _length = 1 };
        };
        PgMemoryContext owner = PgMemoryContext.Current;
        using PgList<nint> defaults = info.GetDefaultArguments(owner)!;
        Assert.AreEqual(1, defaults.Count);
        fail = true;
        Assert.AreEqual("22023", Assert.ThrowsExactly<PgException>(() => info.GetDefaultArguments(owner)).SqlState);
        fail = false;
        using PgList<nint> retry = info.GetDefaultArguments(owner)!;
        Assert.AreEqual(1, retry.Count);
    }

    /// <summary>
    /// Supplies independent catalog fields through the real native ABI, freeing every transferred value.
    /// </summary>
    private sealed class Script : IDisposable
    {
        private readonly nint _previous;

        /// <summary>
        /// Enters the native dispatcher.
        /// </summary>
        internal Script()
        {
            s_script = this;
            _previous = NativeBackend.Enter(Pointer);
        }

        /// <summary>
        /// Gets the entry point.
        /// </summary>
        internal static nint Pointer => (nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Execute;
        /// <summary>
        /// Gets or sets whether the catalog row is absent.
        /// </summary>
        internal bool Missing
        {
            get;
            set;
        }
        /// <summary>
        /// Gets or sets whether optional fields are populated.
        /// </summary>
        internal bool Rich
        {
            get;
            set;
        }
        /// <summary>
        /// Gets or sets whether dispatch fails.
        /// </summary>
        internal bool Fail
        {
            get;
            set;
        }
        /// <summary>
        /// Gets or sets whether the returned frame is malformed.
        /// </summary>
        internal bool Malformed
        {
            get;
            set;
        }
        /// <summary>
        /// Gets or sets the invalid discriminator field.
        /// </summary>
        internal int InvalidField
        {
            get;
            set;
        } = -1;
        /// <summary>
        /// Gets or sets the input argument count.
        /// </summary>
        internal int ArgumentCount
        {
            get;
            set;
        } = 1;
        /// <summary>
        /// Gets or sets the native function kind.
        /// </summary>
        internal char Kind
        {
            get;
            set;
        } = 'f';
        /// <summary>
        /// Gets or sets the native volatility.
        /// </summary>
        internal char Volatility
        {
            get;
            set;
        } = 'i';
        /// <summary>
        /// Gets or sets the native parallel mode.
        /// </summary>
        internal char Parallel
        {
            get;
            set;
        } = 's';
        /// <summary>
        /// Gets the requested OID.
        /// </summary>
        internal uint RequestedOid
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the requested parameter type.
        /// </summary>
        internal uint ParameterOid
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the operation family.
        /// </summary>
        internal byte Operation
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the selector.
        /// </summary>
        internal int Selector
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the result release count.
        /// </summary>
        internal int Releases
        {
            get;
            private set;
        }

        /// <summary>
        /// Restores the enclosing backend.
        /// </summary>
        public void Dispose()
        {
            NativeBackend.Exit(_previous);
            s_script = null;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static int Execute(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
        {
            Script script = s_script!;
            result->_release = &Release;
            try
            {
                script.Operation = (byte)request->_operation;
                script.Selector = request->_scalarOperation;
                script.ParameterOid = request->_parameters[0]._typeOid;
                script.RequestedOid = checked((uint)request->_parameters[0]._value.Integral);
                if (script.Fail)
                {
                    throw new PgException("42501", "catalog denied café");
                }

                if (script.Missing)
                {
                    return 0;
                }

                result->_rowCount = 1;
                result->_columnCount = script.Malformed ? 0 : 24;
                if (script.Malformed)
                {
                    return 0;
                }

                result->_values = (NativeValue*)NativeMemory.AllocZeroed(24, (nuint)sizeof(NativeValue));
                Span<NativeValue> values = new(result->_values, 24);
                values[0].Integral = uint.MaxValue - 1;
                values[1].Integral = BitConverter.SingleToInt32Bits(1.25F);
                values[2].Integral = BitConverter.SingleToInt32Bits(123.5F);
                values[3].Integral = script.Rich ? 23 : 0;
                values[4].Integral = script.Rich ? 99 : 0;
                values[5].Integral = script.Kind;
                values[6].Integral = script.Rich ? 1 : 0;
                values[7].Integral = script.Rich ? 1 : 0;
                values[8].Integral = 14;
                values[9] = NativeValue.FromString("source café 🐘");
                values[10] = script.Rich ? NativeValue.FromString("$libdir/example") : new()
                {
                    IsNull = 1
                };
                values[11] = script.Rich ? NativeValue.FromArray(new PgArray<string>(["search_path=pg_catalog", "work_mem=4MB"])) : new()
                {
                    IsNull = 1
                };
                values[12] = script.Rich ? NativeValue.FromString("iobvt") : new()
                {
                    IsNull = 1
                };
                values[13].Integral = script.Rich ? 3 : script.ArgumentCount;
                values[14].Integral = script.Rich ? 1 : 0;
                values[15] = script.Rich ? NativeValue.FromArray(new PgArray<string?>(["", "名", null, "many", "result"])) : new()
                {
                    IsNull = 1
                };
                values[16] = NativeValue.FromArray(new PgArray<uint>(script.Rich ? [23, 25, 1007] : Enumerable.Repeat<uint>(23, script.ArgumentCount).ToArray()));
                values[17] = script.Rich ? NativeValue.FromArray(new PgArray<uint>([23, 20, 25, 1007, 16])) : new()
                {
                    IsNull = 1
                };
                values[18].Integral = 2249;
                values[19].Integral = script.Rich ? 1 : 0;
                values[20].Integral = script.Volatility;
                values[21].Integral = script.Parallel;
                values[22].Integral = script.Rich ? 1 : 0;
                values[23] = script.Rich ? NativeValue.FromString("(catalog café)") : new()
                {
                    IsNull = 1
                };
                if (script.InvalidField >= 0)
                {
                    values[script.InvalidField].Release();
                    values[script.InvalidField] = script.InvalidField == 12 ? NativeValue.FromString("?") : new()
                    {
                        Integral = '?'
                    };
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

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void Release(NativeSpiResult* result)
        {
            s_script!.Releases++;
            for (int index = 0; index < result->_columnCount; index++)
            {
                result->_values[index].Release();
            }

            NativeMemory.Free(result->_values);
        }
    }
}
