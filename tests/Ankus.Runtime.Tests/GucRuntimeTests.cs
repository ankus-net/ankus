using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies configuration metadata, immutable hook results, scoped native reads, and transport ownership.
/// </summary>
[TestClass]
public sealed class GucRuntimeTests
{
    [ThreadStatic]
    private static ReadFixture? s_fixture;

    /// <summary>
    /// Preserves typed attribute defaults and every shared registration option without backend access.
    /// </summary>
    [TestMethod]
    public void AttributesPreserveTypedDefaultsAndRegistrationMetadata()
    {
        var boolean = new PgGucBoolAttribute("sample.enabled", true, "enabled");
        var integer = new PgGucIntAttribute("sample.limit", -17, "limit");
        var real = new PgGucRealAttribute("sample.ratio", -0d, "ratio");
        var text = new PgGucStringAttribute("sample.text", null, "text");
        var enumeration = new PgGucEnumAttribute("sample.mode", DayOfWeek.Friday, "mode");
        Assert.IsTrue(boolean.DefaultValue);
        Assert.AreEqual(-17, integer.DefaultValue);
        Assert.AreEqual(int.MinValue, integer.Minimum);
        Assert.AreEqual(int.MaxValue, integer.Maximum);
        Assert.AreEqual(long.MinValue, BitConverter.DoubleToInt64Bits(real.DefaultValue));
        Assert.AreEqual(double.MinValue, real.Minimum);
        Assert.AreEqual(double.MaxValue, real.Maximum);
        Assert.IsNull(text.DefaultValue);
        Assert.AreEqual(DayOfWeek.Friday, enumeration.DefaultValue);
        foreach (PgGucAttribute attribute in new PgGucAttribute[] { boolean, integer, real, text, enumeration })
        {
            Assert.AreEqual(PgGucContext.UserSet, attribute.Context);
            Assert.AreEqual(PgGucOptions.None, attribute.Flags);
            Assert.AreEqual(PgGucUnit.None, attribute.Unit);
            Assert.IsNull(attribute.LongDescription);
            Assert.IsNull(attribute.Check);
            Assert.IsNull(attribute.Assign);
            Assert.IsNull(attribute.Show);
        }

        integer.Minimum = -40;
        integer.Maximum = 512;
        integer.LongDescription = "long description";
        integer.Context = PgGucContext.SuperuserSet;
        integer.Flags = PgGucOptions.Report | PgGucOptions.NoResetAll;
        integer.Unit = PgGucUnit.Kilobytes;
        integer.Check = "CheckLimit";
        integer.Assign = "AssignLimit";
        integer.Show = "ShowLimit";
        Assert.AreEqual("sample.limit", integer.Name);
        Assert.AreEqual("limit", integer.ShortDescription);
        Assert.AreEqual(-40, integer.Minimum);
        Assert.AreEqual(512, integer.Maximum);
        Assert.AreEqual("long description", integer.LongDescription);
        Assert.AreEqual(PgGucContext.SuperuserSet, integer.Context);
        Assert.AreEqual(PgGucOptions.Report | PgGucOptions.NoResetAll, integer.Flags);
        Assert.AreEqual(PgGucUnit.Kilobytes, integer.Unit);
        Assert.AreEqual("CheckLimit", integer.Check);
        Assert.AreEqual("AssignLimit", integer.Assign);
        Assert.AreEqual("ShowLimit", integer.Show);
        var label = new PgGucLabelAttribute("legacy-mode");
        Assert.AreEqual("legacy-mode", label.Name);
        Assert.IsFalse(label.Hidden);
        label.Hidden = true;
        Assert.IsTrue(label.Hidden);
    }

    /// <summary>
    /// Copies both incoming and outgoing byte arrays while retaining zero bytes, empty values, and index boundaries.
    /// </summary>
    [TestMethod]
    public void ExtraBytesRemainImmutableAndOwned()
    {
        byte[] original = [0, 17, 128, 255];
        var extra = new PgGucExtra(original);
        original[1] = 99;
        Assert.AreEqual(4, extra.Length);
        Assert.AreEqual((byte)17, extra[1]);
        Assert.AreSequenceEqual(new byte[] { 0, 17, 128, 255 }, extra.AsSpan().ToArray());
        byte[] copy = extra.ToArray();
        copy[2] = 1;
        Assert.AreEqual((byte)128, extra[2]);
        Assert.ThrowsExactly<IndexOutOfRangeException>(() => extra[-1]);
        Assert.ThrowsExactly<IndexOutOfRangeException>(() => extra[4]);
        var empty = new PgGucExtra([]);
        Assert.AreEqual(0, empty.Length);
        Assert.IsEmpty(empty.ToArray());
        Assert.ThrowsExactly<IndexOutOfRangeException>(() => empty[0]);
    }

    /// <summary>
    /// Preserves accepted normalization, nullable accepted values, optional extra, and rejection-side access guards.
    /// </summary>
    [TestMethod]
    public void CheckResultsPreserveAcceptanceAndRejection()
    {
        var extra = new PgGucExtra([0, 42]);
        var accepted = new PgGucCheckResult<int>(17, extra);
        Assert.IsTrue(accepted.IsAccepted);
        Assert.AreEqual(17, accepted.Value);
        Assert.AreSame(extra, accepted.Extra);
        Assert.ThrowsExactly<InvalidOperationException>(() => accepted.Error);
        var acceptedNull = new PgGucCheckResult<string?>((string?)null);
        Assert.IsTrue(acceptedNull.IsAccepted);
        Assert.IsNull(acceptedNull.Value);
        Assert.IsNull(acceptedNull.Extra);
        var acceptedEmpty = new PgGucCheckResult<string?>("", new PgGucExtra([]));
        Assert.AreEqual("", acceptedEmpty.Value);
        Assert.IsNotNull(acceptedEmpty.Extra);
        Assert.AreEqual(0, acceptedEmpty.Extra.Length);
        var error = new PgGucCheckError("rejected", "detail", "hint", "PZ123");
        var rejected = new PgGucCheckResult<int>(error);
        Assert.IsFalse(rejected.IsAccepted);
        Assert.AreSame(error, rejected.Error);
        Assert.ThrowsExactly<InvalidOperationException>(() => rejected.Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => rejected.Extra);
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgGucCheckResult<int>(null!));
    }

    /// <summary>
    /// Preserves omitted versus empty diagnostic fields and explicit versus native-default SQLSTATE.
    /// </summary>
    /// <param name="message">The optional primary message.</param>
    /// <param name="detail">The optional detail.</param>
    /// <param name="hint">The optional hint.</param>
    /// <param name="state">The optional SQLSTATE.</param>
    [TestMethod]
    [DataRow(null, null, null, null)]
    [DataRow("", "", "", "22023")]
    [DataRow("café 🐘", "detail", "hint", "PZ123")]
    public unsafe void CheckErrorsPreserveOptionalDiagnostics(string? message, string? detail, string? hint, string? state)
    {
        var diagnostic = new PgGucCheckError(message, detail, hint, state);
        Assert.AreEqual(message, diagnostic.Message);
        Assert.AreEqual(detail, diagnostic.Detail);
        Assert.AreEqual(hint, diagnostic.Hint);
        Assert.AreEqual(state, diagnostic.SqlState);
        NativeCallError transport = default;
        try
        {
            NativeGuc.WriteCheckError(diagnostic, &transport);
            Assert.AreEqual(NativeError.PackSqlState(state ?? "22023"), transport.SqlState);
            Assert.AreEqual(message, transport._fields[(int)NativeDiagnosticField.Message].ReadOptionalString());
            Assert.AreEqual(detail, transport._fields[(int)NativeDiagnosticField.Detail].ReadOptionalString());
            Assert.AreEqual(hint, transport._fields[(int)NativeDiagnosticField.Hint].ReadOptionalString());
            Assert.AreEqual((byte)0, transport.Message[0]);
        }
        finally
        {
            transport.Release();
        }

        Assert.IsNull(transport._fields[(int)NativeDiagnosticField.Message].ReadOptionalString());
        Assert.IsNull(transport._fields[(int)NativeDiagnosticField.Detail].ReadOptionalString());
        Assert.IsNull(transport._fields[(int)NativeDiagnosticField.Hint].ReadOptionalString());
    }

    /// <summary>
    /// Rejects every invalid SQLSTATE syntax partition before native transport.
    /// </summary>
    /// <param name="state">The malformed SQLSTATE.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("1234")]
    [DataRow("123456")]
    [DataRow("00000")]
    [DataRow("p0001")]
    [DataRow("22 23")]
    [DataRow("é0023")]
    public void CheckErrorsRejectInvalidSqlStates(string state)
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => new PgGucCheckError(sqlState: state));
        Assert.AreEqual("sqlState", error.ParamName);
    }

    /// <summary>
    /// Rejects embedded NUL and malformed UTF-16 independently in each diagnostic field.
    /// </summary>
    /// <param name="field">The zero-based diagnostic field.</param>
    /// <param name="zero">Whether to use NUL instead of an unpaired surrogate.</param>
    [TestMethod]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    public void CheckErrorsRejectUnrepresentableText(int field, bool zero)
    {
        string invalid = zero ? "before\0after" : "before\ud800after";
        void construct() => _ = new PgGucCheckError(field == 0 ? invalid : null, field == 1 ? invalid : null, field == 2 ? invalid : null);
        if (zero)
        {
            Assert.ThrowsExactly<ArgumentException>(construct);
        }
        else
        {
            Assert.ThrowsExactly<EncoderFallbackException>(construct);
        }
    }

    /// <summary>
    /// Preserves exact scalar values, native type discriminators, and fresh reads rather than a managed cached mirror.
    /// </summary>
    [TestMethod]
    public void NativeReadsPreserveScalarValuesAndTypeDiscriminators()
    {
        using var fixture = new ReadFixture();
        using var scope = new ReadScope(ReadPointer);
        fixture.Value = new NativeValue { Integral = 0 };
        Assert.IsFalse(NativeGuc.ReadBoolean("sample.enabled"));
        Assert.AreEqual(0, fixture.Kind);
        fixture.Value = new NativeValue { Integral = 1 };
        Assert.IsTrue(NativeGuc.ReadBoolean("sample.enabled"));
        fixture.Value = new NativeValue { Integral = int.MinValue };
        Assert.AreEqual(int.MinValue, NativeGuc.ReadInt32("sample.count"));
        Assert.AreEqual(1, fixture.Kind);
        fixture.Value = new NativeValue { Integral = int.MaxValue };
        Assert.AreEqual(int.MaxValue, NativeGuc.ReadInt32("sample.count"));
        fixture.Value = new NativeValue { Integral = -17 };
        Assert.AreEqual(-17, NativeGuc.ReadEnum("sample.mode"));
        Assert.AreEqual(4, fixture.Kind);
        Assert.AreEqual("sample.mode", fixture.Name);
        Assert.AreEqual(5, fixture.ReadCalls);
        Assert.AreEqual(0, fixture.BackendCalls);
    }

    /// <summary>
    /// Carries all IEEE64 bits without decimal conversion, including negative zero, subnormal values, and infinities.
    /// </summary>
    /// <param name="bits">The independently specified floating-point encoding.</param>
    [TestMethod]
    [DataRow(0L)]
    [DataRow(long.MinValue)]
    [DataRow(1L)]
    [DataRow(long.MinValue + 1)]
    [DataRow(0x3FF8000000000000L)]
    [DataRow(0x7FEFFFFFFFFFFFFFL)]
    [DataRow(0x7FF0000000000000L)]
    [DataRow(unchecked((long)0xFFF0000000000000UL))]
    public void NativeRealReadsPreserveExactBits(long bits)
    {
        using var fixture = new ReadFixture { Value = new NativeValue { Integral = bits } };
        using var scope = new ReadScope(ReadPointer);
        Assert.AreEqual(bits, BitConverter.DoubleToInt64Bits(NativeGuc.ReadDouble("sample.ratio")));
        Assert.AreEqual(2, fixture.Kind);
        Assert.AreEqual(1, fixture.ReadCalls);
    }

    /// <summary>
    /// Copies null, empty, Unicode, and long string values and releases each returned native buffer exactly once.
    /// </summary>
    /// <param name="text">The native string value.</param>
    [TestMethod]
    [DataRow(default(string))]
    [DataRow("")]
    [DataRow("café 🐘")]
    public void NativeStringReadsOwnValuesAndReleaseBuffers(string? text)
    {
        using var fixture = new ReadFixture { Text = text, ReturnText = true };
        using var scope = new ReadScope(ReadPointer);
        string? owned = NativeGuc.ReadString("sample.text");
        Assert.AreEqual(text, owned);
        Assert.AreEqual(3, fixture.Kind);
        Assert.AreEqual(text is null ? 0 : 1, fixture.Releases);
        fixture.Text = new string('x', 4096);
        Assert.AreEqual(new string('x', 4096), NativeGuc.ReadRequiredString("sample.text"));
        Assert.AreEqual(text, owned);
        Assert.AreEqual(text is null ? 1 : 2, fixture.Releases);
        fixture.Text = null;
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadRequiredString("sample.text"));
        Assert.AreEqual(text is null ? 1 : 2, fixture.Releases);
    }

    /// <summary>
    /// Releases malformed native buffers when UTF-8 decoding, NUL validation, or scalar envelope validation fails.
    /// </summary>
    /// <param name="mode">The malformed transport partition.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void InvalidNativeBuffersAreReleased(int mode)
    {
        using var fixture = new ReadFixture { Bytes = mode == 0 ? [0xC0, 0xAF] : [65, 0, 66] };
        using var scope = new ReadScope(ReadPointer);
        if (mode == 0)
        {
            Assert.ThrowsExactly<DecoderFallbackException>(() => NativeGuc.ReadString("sample.text"));
        }
        else if (mode == 1)
        {
            Assert.ThrowsExactly<ArgumentException>(() => NativeGuc.ReadString("sample.text"));
        }
        else
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("sample.count"));
        }

        Assert.AreEqual(1, fixture.Releases);
    }

    /// <summary>
    /// Rejects malformed scalar Boolean, integer, enum, and null transports before returning a typed value.
    /// </summary>
    [TestMethod]
    public void InvalidScalarEnvelopesAreRejected()
    {
        using var fixture = new ReadFixture { Value = new NativeValue { Integral = 2 } };
        using var scope = new ReadScope(ReadPointer);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadBoolean("sample.enabled"));
        fixture.Value = new NativeValue { Integral = -1 };
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadBoolean("sample.enabled"));
        fixture.Value = new NativeValue { Integral = (long)int.MaxValue + 1 };
        Assert.ThrowsExactly<OverflowException>(() => NativeGuc.ReadInt32("sample.count"));
        Assert.ThrowsExactly<OverflowException>(() => NativeGuc.ReadEnum("sample.mode"));
        fixture.Value = new NativeValue { Integral = (long)int.MinValue - 1 };
        Assert.ThrowsExactly<OverflowException>(() => NativeGuc.ReadInt32("sample.count"));
        fixture.Value = new NativeValue { IsNull = 1 };
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadDouble("sample.ratio"));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadBoolean("sample.enabled"));
        fixture.Value = NativeValue.FromTimeTz(new PgTimeTz(default, 1));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("sample.count"));
    }

    /// <summary>
    /// Copies extra bytes without taking the caller's borrowed transport ownership and preserves absent versus empty data.
    /// </summary>
    [TestMethod]
    public void ExtraTransportPreservesNullEmptyAndBorrowedOwnership()
    {
        Assert.IsNull(NativeGuc.ReadExtra(NativeGuc.FromExtra(null)));
        var source = new PgGucExtra([0, 1, 128, 255]);
        NativeValue value = NativeGuc.FromExtra(source);
        PgGucExtra? copied;
        try
        {
            copied = NativeGuc.ReadExtra(value);
            Assert.IsNotNull(copied);
            Assert.AreNotSame(source, copied);
            Assert.AreSequenceEqual(new byte[] { 0, 1, 128, 255 }, copied.ToArray());
            Assert.AreSequenceEqual(new byte[] { 0, 1, 128, 255 }, value.ReadBytes());
        }
        finally
        {
            value.Release();
        }

        Assert.AreSequenceEqual(new byte[] { 0, 1, 128, 255 }, copied.ToArray());
        NativeValue empty = NativeGuc.FromExtra(new PgGucExtra([]));
        try
        {
            PgGucExtra? result = NativeGuc.ReadExtra(empty);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Length);
        }
        finally
        {
            empty.Release();
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadExtra(default));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadExtra(new NativeValue { IsNull = 2 }));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadExtra(new NativeValue { IsNull = 1, Integral = 1 }));
    }

    /// <summary>
    /// Rejects each independently invalid extra-buffer discriminator without consuming the native caller's borrowed ownership.
    /// </summary>
    /// <param name="field">The malformed envelope field.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public void InvalidExtraEnvelopesPreserveCallerOwnership(int field)
    {
        using var fixture = new ReadFixture();
        NativeValue value = Tracked(NativeValue.FromBytes([1]));
        NativeValue owner = value;
        try
        {
            switch (field)
            {
                case 0:
                    value.Integral = 1;
                    break;
                case 1:
                    value.IsNull = 2;
                    break;
                case 2:
                    value = new NativeValue { IsNull = 1 };
                    Length(ref value) = 1;
                    break;
                case 3:
                    Length(ref value) = -1;
                    break;
                case 4:
                    Auxiliary1(ref value) = 1;
                    break;
                case 5:
                    Auxiliary2(ref value) = 1;
                    break;
                case 6:
                    Infinity(ref value) = 1;
                    break;
                case 7:
                    value.IsNull = 1;
                    Length(ref value) = 0;
                    break;
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadExtra(value));
            Assert.AreEqual(0, fixture.Releases);
        }
        finally
        {
            owner.Release();
        }

        Assert.AreEqual(1, fixture.Releases);
    }

    /// <summary>
    /// Uses the dedicated backend read operation outside hooks, preserving the UTF-8 name and transferred result ownership.
    /// </summary>
    [TestMethod]
    public void BackendFallbackReadsActualStorageWithoutSql()
    {
        using var fixture = new ReadFixture { ReturnText = true, Text = "backing" };
        using var backend = new BackendScope(BackendPointer);
        Assert.AreEqual("backing", NativeGuc.ReadString("sample.café"));
        Assert.AreEqual(SpiOperation.GucRead, fixture.Operation);
        Assert.AreEqual(25, (int)fixture.Operation);
        Assert.AreEqual(3, fixture.Kind);
        Assert.AreEqual("sample.café", fixture.Name);
        Assert.AreEqual(12, fixture.NameByteLength);
        Assert.AreEqual(1, fixture.BackendCalls);
        Assert.AreEqual(0, fixture.ReadCalls);
        Assert.AreEqual(1, fixture.Releases);
        fixture.Text = "changed";
        Assert.AreEqual("changed", NativeGuc.ReadString("sample.café"));
        Assert.AreEqual(2, fixture.BackendCalls);
        Assert.AreEqual(2, fixture.Releases);
    }

    /// <summary>
    /// Releases partially returned values and every diagnostic allocation through both native read routes on failure.
    /// </summary>
    /// <param name="hook">Whether to use the dedicated hook read capability.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedReadsReleaseValuesAndOwnDiagnostics(bool hook)
    {
        using var fixture = new ReadFixture { ReturnText = true, Text = "partial", Fail = true };
        using var backend = new BackendScope(BackendPointer);
        nint previous = hook ? NativeGuc.Enter(ReadPointer) : 0;
        try
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => NativeGuc.ReadString("sample.text"));
            Assert.AreEqual("42704", error.SqlState);
            Assert.AreEqual("setting missing café", error.Message);
            Assert.AreEqual("owned detail", error.Detail);
            Assert.AreEqual("owned hint", error.Hint);
            Assert.AreEqual(4, fixture.Releases);
            fixture.Fail = false;
            fixture.Text = "recovered";
            Assert.AreEqual("recovered", NativeGuc.ReadString("sample.text"));
            Assert.AreEqual(5, fixture.Releases);
            Assert.AreEqual("setting missing café", error.Message);
        }
        finally
        {
            if (hook)
            {
                NativeGuc.Exit(previous);
            }
        }
    }

    /// <summary>
    /// Keeps GUC reads available in transaction-disabled hooks and restores nested or deliberately disabled read scopes.
    /// </summary>
    [TestMethod]
    public void HookReadScopesRemainSeparateFromTransactionAccess()
    {
        using var fixture = new ReadFixture { Value = new NativeValue { Integral = 17 } };
        using var backend = new BackendScope(0);
        using (new ReadScope(ReadPointer))
        {
            Assert.AreEqual(17, NativeGuc.ReadInt32("sample.count"));
            Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1"));
            using (new ReadScope(AlternateReadPointer))
            {
                Assert.AreEqual(31, NativeGuc.ReadInt32("sample.count"));
            }

            Assert.AreEqual(17, NativeGuc.ReadInt32("sample.count"));
            using (new ReadScope(0))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("sample.count"));
            }

            Assert.AreEqual(17, NativeGuc.ReadInt32("sample.count"));
            Assert.AreEqual(4, fixture.ReadCalls);
            Assert.AreEqual(0, fixture.BackendCalls);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("sample.count"));
    }

    /// <summary>
    /// A disabled hook read scope cannot fall back to an otherwise live outer backend binding.
    /// </summary>
    [TestMethod]
    public void DisabledHookScopeDoesNotFallBackToBackend()
    {
        using var fixture = new ReadFixture { Value = new NativeValue { Integral = 17 } };
        using var backend = new BackendScope(BackendPointer);
        using (new ReadScope(0))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("sample.count"));
            Assert.AreEqual(0, fixture.BackendCalls);
        }

        Assert.AreEqual(17, NativeGuc.ReadInt32("sample.count"));
        Assert.AreEqual(1, fixture.BackendCalls);
    }

    /// <summary>
    /// Dedicated hook capabilities never flow to another managed thread, and failed worker reads preserve parent access.
    /// </summary>
    [TestMethod]
    public void HookReadsRequireTheirOwningThread()
    {
        using var fixture = new ReadFixture { Value = new NativeValue { Integral = 17 } };
        using var scope = new ReadScope(ReadPointer);
        DedicatedThread.Run(static () =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadBoolean("sample.enabled"));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadString("sample.text"));
        });
        Assert.AreEqual(17, NativeGuc.ReadInt32("sample.count"));
        Assert.AreEqual(1, fixture.ReadCalls);
    }

    /// <summary>
    /// Invalid names are rejected before native reads or backend operations on both transport routes.
    /// </summary>
    /// <param name="hook">Whether to test the dedicated hook read capability.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidReadNamesNeverReachNativeCode(bool hook)
    {
        using var fixture = new ReadFixture();
        using var backend = new BackendScope(BackendPointer);
        nint previous = hook ? NativeGuc.Enter(ReadPointer) : 0;
        try
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => NativeGuc.ReadInt32(null!));
            Assert.ThrowsExactly<ArgumentException>(() => NativeGuc.ReadInt32(""));
            Assert.ThrowsExactly<ArgumentException>(() => NativeGuc.ReadInt32(" "));
            Assert.ThrowsExactly<ArgumentException>(() => NativeGuc.ReadInt32("sample.\0value"));
            Assert.ThrowsExactly<EncoderFallbackException>(() => NativeGuc.ReadInt32("sample.\ud800"));
            Assert.AreEqual(0, fixture.ReadCalls);
            Assert.AreEqual(0, fixture.BackendCalls);
        }
        finally
        {
            if (hook)
            {
                NativeGuc.Exit(previous);
            }
        }
    }

    /// <summary>
    /// Gets the controlled hook read entry point.
    /// </summary>
    private static unsafe nint ReadPointer
        => (nint)(delegate* unmanaged[Cdecl]<byte*, int, NativeValue*, NativeCallError*, int>)&Read;

    /// <summary>
    /// Gets a distinct controlled hook read binding for nested restoration checks.
    /// </summary>
    private static unsafe nint AlternateReadPointer
        => (nint)(delegate* unmanaged[Cdecl]<byte*, int, NativeValue*, NativeCallError*, int>)&AlternateRead;

    /// <summary>
    /// Gets the controlled backend transport entry point.
    /// </summary>
    private static unsafe nint BackendPointer
        => (nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Backend;

    /// <summary>
    /// Produces a native-shaped value or owned diagnostic while ensuring no exception crosses the unmanaged callback.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Read(byte* name, int kind, NativeValue* value, NativeCallError* error)
    {
        s_fixture!.ReadCalls++;
        return ReadCore(name, kind, value, error);
    }

    /// <summary>
    /// Produces a different scalar value so restored pointer identity has an observable effect.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int AlternateRead(byte* name, int kind, NativeValue* value, NativeCallError* error)
    {
        s_fixture!.ReadCalls++;
        *value = new NativeValue { Integral = 31 };
        return 0;
    }

    /// <summary>
    /// Captures the dedicated backend operation and writes a scalar transport with independent ownership.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Backend(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        ReadFixture fixture = s_fixture!;
        fixture.BackendCalls++;
        fixture.Operation = request->_operation;
        fixture.NameByteLength = request->_commandLength;
        return ReadCore(request->_command, request->_scalarOperation, &result->_text, error);
    }

    /// <summary>
    /// Copies the requested fixture value and models partial native ownership on an error path.
    /// </summary>
    private static unsafe int ReadCore(byte* name, int kind, NativeValue* value, NativeCallError* error)
    {
        try
        {
            ReadFixture fixture = s_fixture!;
            fixture.Name = Marshal.PtrToStringUTF8((nint)name);
            fixture.Kind = kind;
            *value = fixture.Bytes is byte[] bytes ? Tracked(NativeValue.FromBytes(bytes))
                : fixture.ReturnText ? fixture.Text is string text ? Tracked(NativeValue.FromString(text)) : new NativeValue { IsNull = 1 }
                : fixture.Value;
            if (fixture.Fail)
            {
                throw new PgException("42704", "setting missing café", "owned detail", "owned hint");
            }

            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            for (int index = 0; index < NativeErrorFields.Length; index++)
            {
                if (error->_fields[index].ReadOptionalString() is not null)
                {
                    error->_fields[index] = Tracked(error->_fields[index]);
                }
            }

            return 1;
        }
    }

    /// <summary>
    /// Replaces the allocator-compatible release callback with an observable wrapper.
    /// </summary>
    private static unsafe NativeValue Tracked(NativeValue value)
    {
        ReleaseCallback(ref value) = &Release;
        return value;
    }

    /// <summary>
    /// Counts ownership release while retaining NativeValue's NativeMemory allocator pairing.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void Release(void* pointer)
    {
        s_fixture!.Releases++;
        NativeMemory.Free(pointer);
    }

    /// <summary>
    /// Accesses the release callback to observe matching allocator cleanup in controlled native fixtures.
    /// </summary>
    private static unsafe ref delegate* unmanaged[Cdecl]<void*, void> ReleaseCallback(ref NativeValue value) => ref value._release;

    /// <summary>
    /// Accesses byte length to reject malformed input without reading its buffer.
    /// </summary>
    private static ref int Length(ref NativeValue value) => ref value._length;

    /// <summary>
    /// Accesses the first non-GUC scalar discriminator for malformed-envelope tests.
    /// </summary>
    private static ref int Auxiliary1(ref NativeValue value) => ref value._auxiliary1;

    /// <summary>
    /// Accesses the second non-GUC scalar discriminator for malformed-envelope tests.
    /// </summary>
    private static ref int Auxiliary2(ref NativeValue value) => ref value._auxiliary2;

    /// <summary>
    /// Accesses the non-GUC temporal discriminator for malformed-envelope tests.
    /// </summary>
    private static ref int Infinity(ref NativeValue value) => ref value._temporalInfinity;

    /// <summary>
    /// Keeps controlled callback data isolated to each test thread.
    /// </summary>
    private sealed class ReadFixture : IDisposable
    {
        private readonly ReadFixture? _previous = s_fixture;

        /// <summary>
        /// Installs the controlled native reader state on this test thread.
        /// </summary>
        internal ReadFixture() => s_fixture = this;

        /// <summary>
        /// Gets or sets the allocation-free scalar reply.
        /// </summary>
        internal NativeValue Value
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the owned string reply.
        /// </summary>
        internal string? Text
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether the reply is a nullable string.
        /// </summary>
        internal bool ReturnText
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets raw bytes for malformed UTF-8 and envelope probes.
        /// </summary>
        internal byte[]? Bytes
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether the native call fails after returning a partial value.
        /// </summary>
        internal bool Fail
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the copied setting name.
        /// </summary>
        internal string? Name
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the requested native setting kind.
        /// </summary>
        internal int Kind
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the UTF-8 byte length from the fallback request.
        /// </summary>
        internal int NameByteLength
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the fallback operation discriminator.
        /// </summary>
        internal SpiOperation Operation
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the dedicated read callback count.
        /// </summary>
        internal int ReadCalls
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the backend callback count.
        /// </summary>
        internal int BackendCalls
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the count of released native value and diagnostic allocations.
        /// </summary>
        internal int Releases
        {
            get;
            set;
        }

        /// <summary>
        /// Restores any enclosing controlled native state.
        /// </summary>
        public void Dispose() => s_fixture = _previous;
    }

    /// <summary>
    /// Balances nested configuration read bindings on every test exit path.
    /// </summary>
    private sealed class ReadScope(nint read) : IDisposable
    {
        private readonly nint _previous = NativeGuc.Enter(read);

        /// <summary>
        /// Restores the enclosing read binding.
        /// </summary>
        public void Dispose() => NativeGuc.Exit(_previous);
    }

    /// <summary>
    /// Balances the ordinary backend binding independently of GUC hook reads.
    /// </summary>
    private sealed class BackendScope(nint execute) : IDisposable
    {
        private readonly nint _previous = NativeBackend.Enter(execute);

        /// <summary>
        /// Restores the enclosing backend binding.
        /// </summary>
        public void Dispose() => NativeBackend.Exit(_previous);
    }
}
