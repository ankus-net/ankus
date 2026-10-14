using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies detached name construction, exact lookup requests and guarded ownership without modeling the PostgreSQL catalog.
/// </summary>
[TestClass]
public sealed class CatalogLookupTests
{
    [ThreadStatic]
    private static Script? s_script;

    /// <summary>
    /// Detached builders retain empty, quoted, dotted, Unicode and whitespace components in exact insertion order.
    /// </summary>
    [TestMethod]
    public void BuilderPreservesExactComponents()
    {
        var names = new PgQualifiedNameBuilder();
        Assert.IsEmpty(names);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => names[0]);
        Assert.AreSame(names, names.Add(""));
        names.Add(" Mixed.Café😀 ").Add("\"quoted\"").Add("+");
        Assert.HasCount(4, names);
        Assert.AreEqual(" Mixed.Café😀 ", names[1]);
        Assert.AreSequenceEqual(["", " Mixed.Café😀 ", "\"quoted\"", "+"], names);
        IEnumerable untyped = names;
        List<string> copied = [];
        foreach (string component in untyped)
        {
            copied.Add(component);
        }

        Assert.AreSequenceEqual(["", " Mixed.Café😀 ", "\"quoted\"", "+"], copied);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => names[-1]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => names[4]);
    }

    /// <summary>
    /// Rejected additions leave the original builder usable and unchanged.
    /// </summary>
    [TestMethod]
    public void BuilderRejectsLossWithoutMutation()
    {
        var names = new PgQualifiedNameBuilder { "pg_catalog" };
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentNullException>(() => names.Add(null!)).ParamName);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentException>(() => names.Add("a\0b")).ParamName);
        Assert.ThrowsExactly<EncoderFallbackException>(() => names.Add("\uD800"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => names.Add("\uDC00"));
        Assert.AreSequenceEqual(["pg_catalog"], names);
        names.Add("+");
        Assert.AreSequenceEqual(["pg_catalog", "+"], names);
    }

    /// <summary>
    /// Type strings reject lossy inputs before backend capability or dispatch is consulted.
    /// </summary>
    [TestMethod]
    public void TypeNamesRejectLossBeforeDispatch()
    {
        Assert.AreEqual("typeName", Assert.ThrowsExactly<ArgumentNullException>(() => PgTypes.GetOid(null!)).ParamName);
        Assert.AreEqual("typeName", Assert.ThrowsExactly<ArgumentException>(() => PgTypes.GetOid("int4\0text")).ParamName);
        Assert.ThrowsExactly<EncoderFallbackException>(() => PgTypes.GetOid("\uD800"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => PgTypes.GetOid("\uDC00"));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTypes.GetOid(""));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTypes.GetOid("int4"));
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgQualifiedNameBuilder { "+" }.GetOperatorOid(23, 23));
    }

    /// <summary>
    /// Operator lookup sends exact unsigned OIDs and separate components, returns zero, and never caches or consumes the builder.
    /// </summary>
    [TestMethod]
    public void OperatorRequestsPreserveComponentsAndOids()
    {
        using var script = new Script { Result = uint.MaxValue };
        var names = new PgQualifiedNameBuilder { "Mixed.Café😀", "+" };
        Assert.AreEqual(uint.MaxValue, names.GetOperatorOid(0, uint.MaxValue));
        Assert.AreEqual((byte)34, script.Operation);
        Assert.AreEqual(1, script.Suboperation);
        Assert.AreEqual(26U, script.ResultOid);
        Assert.AreSequenceEqual<uint>([26, 26, 25, 25], script.Types);
        Assert.AreSequenceEqual<object?>([0U, uint.MaxValue, "Mixed.Café😀", "+"], script.Values);
        Assert.AreEqual(2, script.ParameterReleases);
        Assert.AreEqual(1, script.ResultReleases);
        script.Result = 0;
        Assert.AreEqual(0U, names.GetOperatorOid(23, 0));
        Assert.AreSequenceEqual<object?>([23U, 0U, "Mixed.Café😀", "+"], script.Values);
        Assert.AreSequenceEqual(["Mixed.Café😀", "+"], names);
        Assert.AreEqual(2, script.Executions);
        Assert.AreEqual(2, script.ResultReleases);
        Assert.AreEqual(0U, new PgQualifiedNameBuilder().GetOperatorOid(0, 0));
        Assert.AreSequenceEqual<object?>([0U, 0U], script.Values);
    }

    /// <summary>
    /// Type syntax and finite CLR metadata names reach the native parser verbatim, independently of type mappings.
    /// </summary>
    [TestMethod]
    public void TypeRequestsPreserveSyntaxAndManagedName()
    {
        using var script = new Script { Result = 1700 };
        Assert.AreEqual(1700U, PgTypes.GetOid(" numeric(12,3)[] "));
        Assert.AreEqual((byte)34, script.Operation);
        Assert.AreEqual(0, script.Suboperation);
        Assert.AreEqual(26U, script.ResultOid);
        Assert.AreSequenceEqual<uint>([25], script.Types);
        Assert.AreSequenceEqual<object?>([" numeric(12,3)[] "], script.Values);
        script.Result = 0;
        Assert.AreEqual(0U, PgTypes.GetOid("-"));
        script.Result = uint.MaxValue;
        Assert.AreEqual(uint.MaxValue, PgTypes.GetOidByManagedName<LookupName>());
        Assert.AreSequenceEqual<object?>(["LookupName"], script.Values);
        _ = PgTypes.GetOidByManagedName<int>();
        Assert.AreSequenceEqual<object?>(["Int32"], script.Values);
        _ = PgTypes.GetOidByManagedName<LookupName[]>();
        Assert.AreSequenceEqual<object?>(["LookupName[]"], script.Values);
        _ = PgTypes.GetOidByManagedName<List<LookupName>>();
        Assert.AreSequenceEqual<object?>(["List`1"], script.Values);
        Assert.AreEqual(6, script.Executions);
        Assert.AreEqual(6, script.ParameterReleases);
        Assert.AreEqual(6, script.ResultReleases);
    }

    /// <summary>
    /// Abort restrictions stop dispatch and native failures release input, result and diagnostic ownership before a retry.
    /// </summary>
    [TestMethod]
    public void LookupLifetimesAndErrorsAreChecked()
    {
        using var script = new Script { Fail = true };
        var names = new PgQualifiedNameBuilder { "no_schema", "+" };
        PgException failure = Assert.ThrowsExactly<PgException>(() => names.GetOperatorOid(23, 23));
        Assert.AreEqual("42501", failure.SqlState);
        Assert.AreEqual("lookup denied café", failure.Message);
        Assert.AreEqual("detail", failure.Detail);
        Assert.AreEqual("hint", failure.Hint);
        Assert.AreEqual(3, script.ErrorReleases);
        Assert.AreEqual(2, script.ParameterReleases);
        Assert.AreEqual(1, script.ResultReleases);
        script.Fail = false;
        script.Result = 551;
        Assert.AreEqual(551U, names.GetOperatorOid(23, 23));
        nint previous = NativeBackend.Enter(Script.Pointer, abortCleanup: true);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => PgTypes.GetOid("int4"));
            Assert.ThrowsExactly<InvalidOperationException>(() => names.GetOperatorOid(23, 23));
            Assert.AreEqual(2, script.Executions);
        }
        finally
        {
            NativeBackend.Exit(previous, abortCleanup: true);
        }

        Assert.AreEqual(551U, PgTypes.GetOid("int4"));
        Assert.AreEqual(3, script.ResultReleases);
        Assert.AreEqual(5, script.ParameterReleases);
    }

    /// <summary>
    /// Active OID classification reads the native header major without arguments, honors guards and releases failed results.
    /// </summary>
    [TestMethod]
    public void ActiveOidVersionRequestsAreGuarded()
    {
        using var script = new Script { Result = 13 };
        Assert.AreEqual("CASHOID", PgBuiltInOids.GetNativeName(PgBuiltInOid.MoneyOid));
        Assert.AreEqual((byte)34, script.Operation);
        Assert.AreEqual(2, script.Suboperation);
        Assert.AreEqual(23U, script.ResultOid);
        Assert.IsEmpty(script.Types);
        Assert.IsEmpty(script.Values);
        Assert.HasCount(295, PgBuiltInOids.GetValues());
        Assert.AreEqual(PgOidKind.Custom, PgOid.FromValue(4451).Kind);
        script.Result = 14;
        Assert.IsTrue(PgBuiltInOids.TryFromValue(4451, out PgBuiltInOid member, out PgOidLookupError error));
        Assert.AreEqual(PgBuiltInOid.Int4MultirangeOid, member);
        Assert.AreEqual(PgOidLookupError.None, error);
        Assert.AreEqual(PgOid.FromBuiltIn(member, 14), PgOid.FromBuiltIn(member));
        script.Fail = true;
        Assert.AreEqual("42501", Assert.ThrowsExactly<PgException>(() => PgOid.FromValue(16)).SqlState);
        Assert.AreEqual(3, script.ErrorReleases);
        Assert.AreEqual(6, script.ResultReleases);
        script.Fail = false;
        script.Result = 18;
        Assert.AreEqual(PgOid.FromBuiltIn(PgBuiltInOid.BoolOid, 18), PgOid.FromValue(16));
        nint previous = NativeBackend.Enter(Script.Pointer, abortCleanup: true);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(PgBuiltInOids.GetValues);
            Assert.AreEqual(7, script.Executions);
        }
        finally
        {
            NativeBackend.Exit(previous, abortCleanup: true);
        }

        script.Result = 20;
        Assert.AreEqual("postgresMajor", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgOid.FromValue(16)).ParamName);
        Assert.AreEqual(8, script.ResultReleases);
        Assert.AreEqual(0, script.ParameterReleases);
    }

    /// <summary>
    /// Supplies a nested CLR name without a SQL mapping.
    /// </summary>
    private sealed class LookupName;

    /// <summary>
    /// Provides scripted native results and captures ownership events, without implementing catalog behavior.
    /// </summary>
    private sealed unsafe class Script : IDisposable
    {
        private readonly nint _previous;
        /// <summary>
        /// Gets or sets the next copied native OID.
        /// </summary>
        internal uint Result
        {
            get;
            set;
        }
        /// <summary>
        /// Gets or sets whether dispatch produces an owned diagnostic.
        /// </summary>
        internal bool Fail
        {
            get;
            set;
        }
        /// <summary>
        /// Gets the captured operation family.
        /// </summary>
        internal byte Operation
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the captured lookup selector.
        /// </summary>
        internal int Suboperation
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the requested result type identity.
        /// </summary>
        internal uint ResultOid
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets the exact argument type identities.
        /// </summary>
        internal uint[] Types
        {
            get;
            private set;
        } = [];
        /// <summary>
        /// Gets the copied arguments.
        /// </summary>
        internal object?[] Values
        {
            get;
            private set;
        } = [];
        /// <summary>
        /// Gets the native dispatch count.
        /// </summary>
        internal int Executions
        {
            get;
            private set;
        }
        /// <summary>
        /// Gets or sets the released input buffer count.
        /// </summary>
        internal int ParameterReleases
        {
            get;
            set;
        }
        /// <summary>
        /// Gets or sets the released result count.
        /// </summary>
        internal int ResultReleases
        {
            get;
            set;
        }
        /// <summary>
        /// Gets or sets the released diagnostic buffer count.
        /// </summary>
        internal int ErrorReleases
        {
            get;
            set;
        }

        /// <summary>
        /// Enters a thread-local scripted backend scope.
        /// </summary>
        internal Script()
        {
            s_script = this;
            _previous = NativeBackend.Enter(Pointer);
        }

        /// <summary>
        /// Gets the scripted backend entry point.
        /// </summary>
        internal static nint Pointer => (nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Execute;

        /// <summary>
        /// Restores the enclosing backend binding.
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
            result->_release = &ReleaseResult;
            try
            {
                script.Executions++;
                script.Operation = (byte)request->_operation;
                script.Suboperation = request->_scalarOperation;
                script.ResultOid = request->_scalarResultOid;
                script.Types = new uint[request->_parameterCount];
                script.Values = new object?[request->_parameterCount];
                for (int index = 0; index < request->_parameterCount; index++)
                {
                    ref NativeSpiParameter parameter = ref request->_parameters[index];
                    script.Types[index] = parameter._typeOid;
                    script.Values[index] = SpiType.FromNative(parameter._value, parameter._typeOid);
                    if (ReleaseCallback(ref parameter._value) != null)
                    {
                        ReleaseCallback(ref parameter._value) = &ReleaseParameter;
                    }
                }

                if (script.Fail)
                {
                    throw new PgException("42501", "lookup denied café", "detail", "hint");
                }

                result->_text.Integral = script.Result;
                return 0;
            }
            // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
            catch (Exception exception)
            {
                NativeError.Write(exception, error);
                for (int index = 0; index < NativeErrorFields.Length; index++)
                {
                    if (ReleaseCallback(ref error->_fields[index]) != null)
                    {
                        ReleaseCallback(ref error->_fields[index]) = &ReleaseError;
                    }
                }

                return 1;
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void ReleaseResult(NativeSpiResult* result) => s_script!.ResultReleases++;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void ReleaseParameter(void* buffer)
        {
            s_script!.ParameterReleases++;
            NativeMemory.Free(buffer);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
        private static void ReleaseError(void* buffer)
        {
            s_script!.ErrorReleases++;
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>
    /// Replaces transport release callbacks to prove allocator-matched ownership cleanup.
    /// </summary>
    private static unsafe ref delegate* unmanaged[Cdecl]<void*, void> ReleaseCallback(ref NativeValue value) => ref value._release;
}
