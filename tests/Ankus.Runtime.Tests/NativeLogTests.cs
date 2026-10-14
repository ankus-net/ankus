using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies independent logging capability, diagnostic ownership, and terminal managed unwinding.
/// </summary>
[TestClass]
public sealed partial class NativeLogTests
{
    [ThreadStatic]
    private static LogFixture? s_fixture;

    /// <summary>
    /// Terminal transport remains available during owned-resource cleanup without granting SQL or ordinary logging.
    /// </summary>
    /// <param name="level">The terminal severity retained before managed unwinding.</param>
    [TestMethod]
    [DataRow(PgLogLevel.Fatal)]
    [DataRow(PgLogLevel.Panic)]
    public void TerminalBackendTransportRemainsAvailableDuringAbortCleanup(PgLogLevel level)
    {
        using var fixture = new LogFixture();
        nint previous = NativeBackend.Enter(BackendPointer, abortCleanup: true);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 42"));
            Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
            var diagnostic = new PgDiagnostic("terminal cleanup café")
            {
                SqlState = "P7806",
                Detail = "terminal cleanup naïve",
                Hint = "restart after terminal cleanup déjà",
            };
            PgTerminalException terminal = Assert.ThrowsExactly<PgTerminalException>(() => PgLog.Write(level, diagnostic));
            Assert.AreEqual(level, terminal.Level);
            Assert.AreSame(diagnostic, terminal.Diagnostic);
            Assert.AreSequenceEqual([SpiOperation.Report], fixture.BackendOperations);
            Assert.IsEmpty(fixture.Calls);
            Assert.AreEqual(diagnostic.Message, fixture.Fields[(int)NativeDiagnosticField.Message]);
            Assert.AreEqual(diagnostic.Detail, fixture.Fields[(int)NativeDiagnosticField.Detail]);
            Assert.AreEqual(diagnostic.Hint, fixture.Fields[(int)NativeDiagnosticField.Hint]);
            Assert.AreEqual(3, fixture.ReportReleases);
        }
        finally
        {
            NativeBackend.Exit(previous, abortCleanup: true);
        }
    }

    /// <summary>
    /// Logging and terminal reports require an active capability even when no native message would be emitted.
    /// </summary>
    /// <param name="level">A representative nonterminal or terminal severity.</param>
    [TestMethod]
    [DataRow(PgLogLevel.Notice)]
    [DataRow(PgLogLevel.Error)]
    [DataRow(PgLogLevel.Fatal)]
    [DataRow(PgLogLevel.Panic)]
    public void DetachedLoggingRequiresCapability(PgLogLevel level)
    {
        using var fixture = new LogFixture();
        Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(level));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.Write(level, "detached"));
        Assert.IsEmpty(fixture.Calls);
        Assert.IsEmpty(fixture.BackendOperations);
    }

    /// <summary>
    /// Restricted logging remains usable while an enclosing SQL capability is explicitly masked.
    /// </summary>
    [TestMethod]
    public void LoggingCapabilityDoesNotEnableTransactionApis()
    {
        using var fixture = new LogFixture();
        using var outer = new BackendScope(BackendPointer);
        using var disabled = new BackendScope(0);
        using var scope = new LogScope(LogPointer);
        Assert.IsTrue(PgLog.IsEnabled(PgLogLevel.Notice));
        PgLog.Write(PgLogLevel.Notice, "restricted café");
        Assert.AreEqual("restricted café", fixture.Fields[(int)NativeDiagnosticField.Message]);
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1"));
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Connect(static session => session.Execute("SELECT 1")));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDate.CurrentDate);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("sample.setting"));
        Assert.AreSequenceEqual([(1, 0, 8), (1, 0, 8), (1, 1, 8)], fixture.Calls);
        Assert.IsEmpty(fixture.BackendOperations);
        Assert.AreEqual(1, fixture.ReportReleases);
    }

    /// <summary>
    /// Ordinary callbacks retain the established backend filtering and report operations when no logging scope exists.
    /// </summary>
    [TestMethod]
    public void OrdinaryBackendRouteRemainsAvailable()
    {
        using var fixture = new LogFixture();
        using var scope = new BackendScope(BackendPointer);
        Assert.IsTrue(PgLog.IsEnabled(PgLogLevel.Warning));
        PgLog.Write(PgLogLevel.Warning, "ordinary");
        Assert.AreSequenceEqual([SpiOperation.IsLogEnabled, SpiOperation.IsLogEnabled, SpiOperation.Report], fixture.BackendOperations);
        Assert.IsEmpty(fixture.Calls);
        Assert.AreEqual("ordinary", fixture.Fields[(int)NativeDiagnosticField.Message]);
        Assert.AreEqual(1, fixture.ReportReleases);
        fixture.Enabled = false;
        PgLog.Write(PgLogLevel.Warning, "filtered");
        Assert.HasCount(4, fixture.BackendOperations);
        Assert.AreEqual(SpiOperation.IsLogEnabled, fixture.BackendOperations[3]);
        Assert.AreEqual("ordinary", fixture.Fields[(int)NativeDiagnosticField.Message]);
    }

    /// <summary>
    /// Disabled nested scopes reject fallback, while distinct enabled scopes restore on every managed exit path.
    /// </summary>
    /// <param name="fail">Whether the nested callback throws before its finally block.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NestedAndDisabledScopesRestoreTheirExactBinding(bool fail)
    {
        using var fixture = new LogFixture();
        using var backend = new BackendScope(BackendPointer);
        using (new LogScope(LogPointer))
        {
            Assert.IsTrue(PgLog.IsEnabled(PgLogLevel.Notice));
            nint previous = NativeLog.Enter(0);
            Assert.AreEqual(LogPointer, previous);
            try
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
                Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.Write(PgLogLevel.Error, "disabled"));
                PgException? caught = null;
                try
                {
                    using var child = new LogScope(AlternateLogPointer);
                    Assert.IsFalse(PgLog.IsEnabled(PgLogLevel.Notice));
                    if (fail)
                    {
                        throw new PgException("PZ123", "nested failure");
                    }
                }
                catch (PgException error) when (fail)
                {
                    caught = error;
                }

                if (fail)
                {
                    Assert.IsNotNull(caught);
                    Assert.AreEqual("nested failure", caught.Message);
                }
                else
                {
                    Assert.IsNull(caught);
                }

                using (new LogScope(0))
                {
                    Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.Write(PgLogLevel.Notice, "still disabled"));
                }

                Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
            }
            finally
            {
                NativeLog.Exit(previous);
            }

            Assert.IsTrue(PgLog.IsEnabled(PgLogLevel.Notice));
            Assert.AreSequenceEqual([(1, 0, 8), (2, 0, 8), (1, 0, 8)], fixture.Calls);
            Assert.IsEmpty(fixture.BackendOperations);
        }

        Assert.IsTrue(PgLog.IsEnabled(PgLogLevel.Notice));
        Assert.AreSequenceEqual([SpiOperation.IsLogEnabled], fixture.BackendOperations);
    }

    /// <summary>
    /// Logging bindings do not flow to worker threads, and worker-owned scopes cannot change the parent capability.
    /// </summary>
    [TestMethod]
    public void LoggingCapabilityRemainsThreadBound()
    {
        using var fixture = new LogFixture();
        using var scope = new LogScope(LogPointer);
        Assert.IsTrue(PgLog.IsEnabled(PgLogLevel.Notice));
        DedicatedThread.Run(static () =>
        {
            using var worker = new LogFixture();
            Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
            Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.Write(PgLogLevel.Fatal, "worker"));
            using (new LogScope(LogPointer))
            {
                PgLog.Write(PgLogLevel.Notice, "worker owned");
                Assert.AreEqual("worker owned", worker.Fields[(int)NativeDiagnosticField.Message]);
                Assert.AreEqual(1, worker.ReportReleases);
                Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1"));
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.IsEnabled(PgLogLevel.Notice));
        });
        Assert.IsTrue(PgLog.IsEnabled(PgLogLevel.Notice));
        Assert.AreSequenceEqual([(1, 0, 8), (1, 0, 8)], fixture.Calls);
        Assert.AreEqual(0, fixture.ReportReleases);
    }

    /// <summary>
    /// Each nonterminal severity is passed unchanged in both filtering and reporting requests.
    /// </summary>
    /// <param name="level">The public reporting severity.</param>
    /// <param name="nativeLevel">Its stable ABI discriminator.</param>
    [TestMethod]
    [DataRow(PgLogLevel.Debug5, 0)]
    [DataRow(PgLogLevel.Debug4, 1)]
    [DataRow(PgLogLevel.Debug3, 2)]
    [DataRow(PgLogLevel.Debug2, 3)]
    [DataRow(PgLogLevel.Debug1, 4)]
    [DataRow(PgLogLevel.Log, 5)]
    [DataRow(PgLogLevel.ServerOnly, 6)]
    [DataRow(PgLogLevel.Info, 7)]
    [DataRow(PgLogLevel.Notice, 8)]
    [DataRow(PgLogLevel.Warning, 9)]
    public void NonterminalSeveritiesPreserveTransportValues(PgLogLevel level, int nativeLevel)
    {
        using var fixture = new LogFixture();
        using var scope = new LogScope(LogPointer);
        PgLog.Write(level, "literal 100% %s café");
        Assert.AreSequenceEqual([(1, 0, nativeLevel), (1, 1, nativeLevel)], fixture.Calls);
        Assert.AreEqual("literal 100% %s café", fixture.Fields[(int)NativeDiagnosticField.Message]);
        Assert.AreEqual(0, fixture.SqlState);
        Assert.AreEqual(1, fixture.ReportReleases);
    }

    /// <summary>
    /// Terminal threshold queries retain their exact native severity without invoking a report operation.
    /// </summary>
    /// <param name="level">The terminal reporting severity.</param>
    /// <param name="nativeLevel">Its stable ABI discriminator.</param>
    [TestMethod]
    [DataRow(PgLogLevel.Error, 10)]
    [DataRow(PgLogLevel.Fatal, 11)]
    [DataRow(PgLogLevel.Panic, 12)]
    public void TerminalThresholdQueriesPreserveTransportValues(PgLogLevel level, int nativeLevel)
    {
        using var fixture = new LogFixture();
        using var scope = new LogScope(LogPointer);
        Assert.IsTrue(PgLog.IsEnabled(level));
        Assert.AreSequenceEqual([(1, 0, nativeLevel)], fixture.Calls);
        Assert.IsEmpty(fixture.BackendOperations);
        Assert.AreEqual(0, fixture.ReportReleases);
    }

    /// <summary>
    /// Both routes preserve every structured field and release each outgoing allocation after copying.
    /// </summary>
    /// <param name="restricted">Whether to use the independent logging route.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StructuredReportsPreserveFieldsAndReleaseOwnership(bool restricted)
    {
        using var fixture = new LogFixture();
        using var backend = new BackendScope(BackendPointer);
        using LogScope? scope = restricted ? new(LogPointer) : null;
        string message = new string('x', 10000) + " café 🐘 100%";
        var diagnostic = new PgDiagnostic(message)
        {
            SqlState = "22023",
            Detail = "detail é",
            Hint = "",
            Context = "context",
            SchemaName = "schema",
            TableName = "table",
            ColumnName = "column",
            DataTypeName = "type",
            ConstraintName = "constraint",
            InternalQuery = "SELECT '🐘'",
            File = "source.cs",
            Routine = "Report",
            DetailLog = "server only",
            Domain = "test_extension_domain",
            Position = 7,
            InternalPosition = 11,
            Line = 19,
        };
        PgLog.Write(PgLogLevel.Warning, diagnostic);
        Assert.AreSequenceEqual<string?>(
            [message, "detail é", "", "context", "schema", "table", "column", "type", "constraint", "SELECT '🐘'", "source.cs", "Report", "server only", null,
                "test_extension_domain"],
            fixture.Fields);
        Assert.AreEqual(50_856_066, fixture.SqlState);
        Assert.AreEqual(7, fixture.Position);
        Assert.AreEqual(11, fixture.InternalPosition);
        Assert.AreEqual(19, fixture.Line);
        Assert.AreEqual(14, fixture.ReportReleases);
        Assert.HasCount(restricted ? 2 : 0, fixture.Calls);
        Assert.HasCount(restricted ? 0 : 2, fixture.BackendOperations);
        GarbageCollection.Collect();
        Assert.AreEqual(message, fixture.Fields[(int)NativeDiagnosticField.Message]);
    }

    /// <summary>
    /// Absent and empty diagnostic fields retain their distinct transport representations.
    /// </summary>
    /// <param name="detail">The optional field value.</param>
    [TestMethod]
    [DataRow(default(string))]
    [DataRow("")]
    [DataRow("café 🐘")]
    public void OptionalFieldsPreserveNullAndEmpty(string? detail)
    {
        using var fixture = new LogFixture();
        using var scope = new LogScope(LogPointer);
        PgLog.Write(PgLogLevel.Notice, new PgDiagnostic("") { Detail = detail, SqlState = "00000" });
        Assert.AreEqual("", fixture.Fields[(int)NativeDiagnosticField.Message]);
        Assert.AreEqual(detail, fixture.Fields[(int)NativeDiagnosticField.Detail]);
        Assert.IsNull(fixture.Fields[(int)NativeDiagnosticField.Hint]);
        Assert.AreEqual(detail is null ? 1 : 2, fixture.ReportReleases);
        Assert.AreEqual(0, fixture.SqlState);
    }

    /// <summary>
    /// Filtering happens before text encoding, while enabled reports reject invalid text and preserve the next call.
    /// </summary>
    /// <param name="zeroCharacter">Whether the invalid text contains a zero character instead of malformed UTF-16.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FilteredReportsAvoidMarshallingInvalidText(bool zeroCharacter)
    {
        using var fixture = new LogFixture { Enabled = false };
        using var scope = new LogScope(LogPointer);
        var diagnostic = new PgDiagnostic("valid") { Detail = zeroCharacter ? "invalid\0detail" : "invalid\ud800" };
        PgLog.Write(PgLogLevel.Notice, diagnostic);
        Assert.AreSequenceEqual([(1, 0, 8)], fixture.Calls);
        Assert.AreEqual(0, fixture.ReportReleases);
        fixture.Enabled = true;
        if (zeroCharacter)
        {
            Assert.ThrowsExactly<ArgumentException>(() => PgLog.Write(PgLogLevel.Notice, diagnostic));
        }
        else
        {
            Assert.ThrowsExactly<EncoderFallbackException>(() => PgLog.Write(PgLogLevel.Notice, diagnostic));
        }

        Assert.AreSequenceEqual([(1, 0, 8), (1, 0, 8)], fixture.Calls);
        PgLog.Write(PgLogLevel.Notice, "recovered");
        Assert.AreEqual("recovered", fixture.Fields[(int)NativeDiagnosticField.Message]);
        Assert.AreEqual(1, fixture.ReportReleases);
    }

    /// <summary>
    /// Native filter and report failures copy diagnostics before releasing every error and report buffer.
    /// </summary>
    /// <param name="operation">The failing filter or report operation.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void NativeFailuresReleaseOwnedDiagnosticsAndAllowRecovery(int operation)
    {
        using var fixture = new LogFixture { FailOperation = operation };
        using var scope = new LogScope(LogPointer);
        PgException error = Assert.ThrowsExactly<PgException>(() =>
            PgLog.Write(PgLogLevel.Warning, new PgDiagnostic("outgoing") { Detail = "outgoing detail" }));
        Assert.AreEqual("22021", error.SqlState);
        Assert.AreEqual("native failure café", error.Message);
        Assert.AreEqual("native detail", error.Detail);
        Assert.AreEqual("native hint", error.Hint);
        Assert.AreEqual("native context", error.Context);
        Assert.AreEqual(4, fixture.ErrorReleases);
        Assert.AreEqual(operation == 1 ? 2 : 0, fixture.ReportReleases);
        fixture.FailOperation = -1;
        PgLog.Write(PgLogLevel.Notice, "after failure");
        Assert.AreEqual("after failure", fixture.Fields[(int)NativeDiagnosticField.Message]);
        Assert.AreEqual(operation == 1 ? 3 : 1, fixture.ReportReleases);
        Assert.AreEqual(4, fixture.ErrorReleases);
        Assert.AreEqual("native failure café", error.Message);
        Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1"));
    }

    /// <summary>
    /// Malformed native error text cannot bypass cleanup of the error or the outgoing report.
    /// </summary>
    /// <param name="operation">The failing filter or report operation.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void NativeErrorDecodingFailureStillReleasesOwnership(int operation)
    {
        using var fixture = new LogFixture { FailOperation = operation, MalformedError = true };
        using var scope = new LogScope(LogPointer);
        Assert.ThrowsExactly<DecoderFallbackException>(() => PgLog.Write(PgLogLevel.Warning, "outgoing"));
        Assert.AreEqual(4, fixture.ErrorReleases);
        Assert.AreEqual(operation == 1 ? 1 : 0, fixture.ReportReleases);
    }

    /// <summary>
    /// Terminal reports retain their native boundary request before unwinding without querying filters.
    /// </summary>
    /// <param name="level">The terminal reporting severity.</param>
    [TestMethod]
    [DataRow(PgLogLevel.Error)]
    [DataRow(PgLogLevel.Fatal)]
    [DataRow(PgLogLevel.Panic)]
    public unsafe void TerminalReportsRetainManagedUnwindSemantics(PgLogLevel level)
    {
        using var fixture = new LogFixture { Enabled = false };
        using var scope = new LogScope(LogPointer);
        var diagnostic = new PgDiagnostic("terminal café") { SqlState = "22023", Detail = "detail", Hint = "hint" };
        Exception exception;
        if (level == PgLogLevel.Error)
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => PgLog.Write(level, diagnostic));
            Assert.AreEqual("22023", error.SqlState);
            Assert.AreEqual("detail", error.Detail);
            Assert.AreEqual("hint", error.Hint);
            exception = error;
        }
        else
        {
            PgTerminalException terminal = Assert.ThrowsExactly<PgTerminalException>(() => PgLog.Write(level, diagnostic));
            Assert.AreEqual(level, terminal.Level);
            Assert.AreSame(diagnostic, terminal.Diagnostic);
            exception = terminal;
        }

        NativeCallError transport = default;
        try
        {
            NativeError.Write(exception, &transport);
            Assert.AreEqual(level == PgLogLevel.Error ? 0 : (int)level + 1, transport._reportLevel);
            Assert.AreEqual("terminal café", transport.ToException().Message);
        }
        finally
        {
            transport.Release();
        }

        if (level == PgLogLevel.Error)
        {
            Assert.IsEmpty(fixture.Calls);
            Assert.AreEqual(0, fixture.ReportReleases);
        }
        else
        {
            Assert.AreEqual((1, 2, (int)level), Assert.ContainsSingle(fixture.Calls));
            Assert.AreEqual("terminal café", fixture.Fields[(int)NativeDiagnosticField.Message]);
            Assert.AreEqual(3, fixture.ReportReleases);
        }
    }

    /// <summary>
    /// Terminal recording remains available while a spinlock is held so managed unwinding can release it.
    /// </summary>
    /// <param name="scoped">Whether the callback uses the restricted logger instead of the ordinary backend route.</param>
    /// <param name="level">The terminal severity retained for the native boundary.</param>
    [TestMethod]
    [DataRow(false, PgLogLevel.Fatal)]
    [DataRow(false, PgLogLevel.Panic)]
    [DataRow(true, PgLogLevel.Fatal)]
    [DataRow(true, PgLogLevel.Panic)]
    public void TerminalReportsReleaseHeldLocks(bool scoped, PgLogLevel level)
    {
        using var locks = new NativeSpinLockTestFixture();
        using var fixture = new LogFixture { Enabled = false };
        using var backend = new BackendScope(BackendPointer);
        using LogScope? logger = scoped ? new(LogPointer) : null;
        var storage = new PgSpinLock<int>(19);
        PgTerminalException failure = Assert.ThrowsExactly<PgTerminalException>(() => ReportWhileLocked(storage, level));
        Assert.AreEqual(level, failure.Level);
        Assert.AreEqual("locked terminal", failure.Message);
        Assert.AreEqual("locked terminal", fixture.Fields[(int)NativeDiagnosticField.Message]);
        Assert.AreEqual(1, fixture.ReportReleases);
        Assert.IsEmpty(locks.Held);
        Assert.HasCount(1, locks.Released);
        if (scoped)
        {
            Assert.AreEqual((1, 2, (int)level), Assert.ContainsSingle(fixture.Calls));
            Assert.IsEmpty(fixture.BackendOperations);
        }
        else
        {
            Assert.AreEqual(SpiOperation.Report, Assert.ContainsSingle(fixture.BackendOperations));
            Assert.IsEmpty(fixture.Calls);
        }

        using PgSpinLockGuard<int> recovered = storage.Lock();
        Assert.AreEqual(37, recovered.Value);

        static void ReportWhileLocked(PgSpinLock<int> target, PgLogLevel severity)
        {
            using PgSpinLockGuard<int> held = target.Lock();
            held.Value = 37;
            PgLog.Write(severity, "locked terminal");
        }
    }

    /// <summary>
    /// SQLSTATE validation occurs before filtering and rejects error success codes before terminal dispatch.
    /// </summary>
    /// <param name="state">The invalid SQLSTATE.</param>
    /// <param name="level">The reporting severity.</param>
    [TestMethod]
    [DataRow("", PgLogLevel.Notice)]
    [DataRow("2202", PgLogLevel.Notice)]
    [DataRow("220230", PgLogLevel.Notice)]
    [DataRow("p0001", PgLogLevel.Notice)]
    [DataRow("22 23", PgLogLevel.Notice)]
    [DataRow("é0023", PgLogLevel.Notice)]
    [DataRow("00000", PgLogLevel.Error)]
    [DataRow("00000", PgLogLevel.Fatal)]
    [DataRow("00000", PgLogLevel.Panic)]
    public void InvalidSqlStateNeverReachesNativeLogging(string state, PgLogLevel level)
    {
        using var fixture = new LogFixture { Enabled = false };
        using var scope = new LogScope(LogPointer);
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => PgLog.Write(level, new PgDiagnostic("invalid") { SqlState = state }));
        Assert.AreEqual("diagnostic", error.ParamName);
        Assert.IsEmpty(fixture.Calls);
    }

    /// <summary>
    /// Values outside the public severity range are rejected before either native logging operation.
    /// </summary>
    /// <param name="level">The invalid reporting severity.</param>
    [TestMethod]
    [DataRow((PgLogLevel)(-1))]
    [DataRow((PgLogLevel)13)]
    public void InvalidSeverityNeverReachesNativeLogging(PgLogLevel level)
    {
        using var fixture = new LogFixture();
        using var scope = new LogScope(LogPointer);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgLog.IsEnabled(level));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgLog.Write(level, "invalid"));
        Assert.IsEmpty(fixture.Calls);
    }

    /// <summary>
    /// Gets the controlled dedicated logging entry point.
    /// </summary>
    private static unsafe nint LogPointer
        => (nint)(delegate* unmanaged[Cdecl]<int, int, NativeCallError*, NativeCallError*, int*, int>)&Log;

    /// <summary>
    /// Gets a distinct pointer whose filter result makes nested restoration observable.
    /// </summary>
    private static unsafe nint AlternateLogPointer
        => (nint)(delegate* unmanaged[Cdecl]<int, int, NativeCallError*, NativeCallError*, int*, int>)&AlternateLog;

    /// <summary>
    /// Gets the existing native backend ABI for fallback checks.
    /// </summary>
    private static unsafe nint BackendPointer
        => (nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Backend;

    /// <summary>
    /// Captures independent native logging requests without allowing exceptions through the unmanaged boundary.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Log(int operation, int level, NativeCallError* report, NativeCallError* error, int* enabled)
        => LogCore(1, operation, level, report, error, enabled);

    /// <summary>
    /// Supplies the alternate scope's independent native filter result.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int AlternateLog(int operation, int level, NativeCallError* report, NativeCallError* error, int* enabled)
        => LogCore(2, operation, level, report, error, enabled);

    /// <summary>
    /// Captures existing backend logging operations without granting any other fake backend operation.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Backend(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        try
        {
            LogFixture fixture = s_fixture!;
            fixture.BackendOperations.Add(request->_operation);
            if (request->_operation == SpiOperation.IsLogEnabled)
            {
                result->_rowsAffected = fixture.Enabled ? 1 : 0;
            }
            else if (request->_operation == SpiOperation.Report)
            {
                CaptureReport(request->_diagnostic);
            }
            else
            {
                throw new InvalidOperationException("The logging fixture cannot execute transaction operations.");
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
    /// Models the native filter/report transport, including owned failures after borrowing outgoing diagnostics.
    /// </summary>
    private static unsafe int LogCore(int route, int operation, int level, NativeCallError* report, NativeCallError* error, int* enabled)
    {
        try
        {
            LogFixture fixture = s_fixture!;
            fixture.Calls.Add((route, operation, level));
            if (operation == 0 && report == null)
            {
                *enabled = route == 1 && fixture.Enabled ? 1 : 0;
            }
            else if (operation is 1 or 2 && report != null)
            {
                CaptureReport(report);
            }
            else
            {
                throw new InvalidOperationException("Unexpected logging operation or report pointer.");
            }

            if (fixture.FailOperation == operation)
            {
                throw new PgException("22021", "native failure café", "native detail", "native hint") { Context = "native context" };
            }

            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            if (s_fixture!.MalformedError)
            {
                error->_fields[(int)NativeDiagnosticField.Message].Release();
                error->_fields[(int)NativeDiagnosticField.Message] = NativeValue.FromBytes([0xC3, 0x28]);
            }

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

    /// <summary>
    /// Copies borrowed report fields and wraps their allocator-compatible release callbacks for observation.
    /// </summary>
    private static unsafe void CaptureReport(NativeCallError* report)
    {
        LogFixture fixture = s_fixture!;
        fixture.SqlState = report->SqlState;
        fixture.Position = report->_position;
        fixture.InternalPosition = report->_internalPosition;
        fixture.Line = report->_line;
        for (int index = 0; index < NativeErrorFields.Length; index++)
        {
            if (ReleaseCallback(ref report->_fields[index]) != null)
            {
                ReleaseCallback(ref report->_fields[index]) = &ReleaseReport;
            }

            fixture.Fields[index] = report->_fields[index].ReadOptionalString();
        }
    }

    /// <summary>
    /// Observes outgoing buffer release while retaining the NativeMemory allocator pair.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ReleaseReport(void* buffer)
    {
        s_fixture!.ReportReleases++;
        NativeMemory.Free(buffer);
    }

    /// <summary>
    /// Observes owned error buffer release while retaining the NativeMemory allocator pair.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ReleaseError(void* buffer)
    {
        s_fixture!.ErrorReleases++;
        NativeMemory.Free(buffer);
    }

    /// <summary>
    /// Accesses only the release callback for controlled allocator ownership assertions.
    /// </summary>
    private static unsafe ref delegate* unmanaged[Cdecl]<void*, void> ReleaseCallback(ref NativeValue value) => ref value._release;

    /// <summary>
    /// Isolates controlled callback values and ownership counters to each test thread.
    /// </summary>
    private sealed class LogFixture : IDisposable
    {
        private readonly LogFixture? _previous = s_fixture;

        /// <summary>
        /// Installs this test's native callback state.
        /// </summary>
        internal LogFixture() => s_fixture = this;

        /// <summary>
        /// Gets the route, operation, and severity observed by dedicated native callbacks.
        /// </summary>
        internal List<(int Route, int Operation, int Level)> Calls { get; } = [];

        /// <summary>
        /// Gets the native operations observed through the ordinary backend fallback.
        /// </summary>
        internal List<SpiOperation> BackendOperations { get; } = [];

        /// <summary>
        /// Gets the copied diagnostic text slots.
        /// </summary>
        internal string?[] Fields { get; } = new string?[NativeErrorFields.Length];

        /// <summary>
        /// Gets or sets the controlled native routing result.
        /// </summary>
        internal bool Enabled
        {
            get;
            set;
        } = true;

        /// <summary>
        /// Gets or sets which operation fails, or minus one for success.
        /// </summary>
        internal int FailOperation
        {
            get;
            set;
        } = -1;

        /// <summary>
        /// Gets or sets whether a failure contains invalid UTF-8 text.
        /// </summary>
        internal bool MalformedError
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the last report's packed native SQLSTATE.
        /// </summary>
        internal int SqlState
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the last report's client query position.
        /// </summary>
        internal int Position
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the last report's internal query position.
        /// </summary>
        internal int InternalPosition
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the last report's source line.
        /// </summary>
        internal int Line
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the number of freed outgoing report allocations.
        /// </summary>
        internal int ReportReleases
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the number of freed incoming native diagnostic allocations.
        /// </summary>
        internal int ErrorReleases
        {
            get;
            set;
        }

        /// <summary>
        /// Restores the previous test callback state.
        /// </summary>
        public void Dispose() => s_fixture = _previous;
    }

    /// <summary>
    /// Balances the independent logging scope on ordinary and exceptional exits.
    /// </summary>
    private sealed class LogScope(nint log) : IDisposable
    {
        private readonly nint _previous = NativeLog.Enter(log);

        /// <summary>
        /// Restores the enclosing native logger.
        /// </summary>
        public void Dispose() => NativeLog.Exit(_previous);
    }

    /// <summary>
    /// Balances the independent ordinary backend scope.
    /// </summary>
    private sealed class BackendScope(nint execute) : IDisposable
    {
        private readonly nint _previous = NativeBackend.Enter(execute);

        /// <summary>
        /// Restores the enclosing backend capability.
        /// </summary>
        public void Dispose() => NativeBackend.Exit(_previous);
    }
}
