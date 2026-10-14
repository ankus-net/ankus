using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies deliberate input recovery does not misclassify unrecovered or operational native failures.
/// </summary>
[TestClass]
public sealed unsafe class NativeInputRecoveryTests
{
    [ThreadStatic]
    private static List<byte>? s_requests;

    [ThreadStatic]
    private static string? s_sqlState;

    [ThreadStatic]
    private static NativeErrorFlags s_flags;

    /// <summary>
    /// Makes the static native SPI callback record each request and fail it with <paramref name="sqlState"/>.
    /// </summary>
    private static void FailWith(string sqlState, NativeErrorFlags flags)
    {
        s_requests = [];
        s_sqlState = sqlState;
        s_flags = flags;
    }

    /// <summary>
    /// Clears the failure the static native SPI callback reports.
    /// </summary>
    private static void StopFailing()
    {
        s_requests = null;
        s_sqlState = null;
        s_flags = 0;
    }

    /// <summary>
    /// Rejects missing backend access before invoking conversion or entering an input recovery scope.
    /// </summary>
    [TestMethod]
    public void InputRecoveryRequiresBackendBeforeInvokingParser()
    {
        bool invoked = false;
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeInputRecovery.Run(() => invoked = true));
        Assert.IsFalse(invoked);
        Assert.IsFalse(NativeInputRecovery.IsActive);
    }

    /// <summary>
    /// Returns false only for a recovered data error and restores ordinary parse requests after every exit.
    /// </summary>
    /// <param name="state">The independently reported native SQLSTATE.</param>
    /// <param name="unrecovered">Whether rollback is still required.</param>
    [TestMethod]
    [DataRow("22P02", false)]
    [DataRow("22P02", true)]
    [DataRow("53200", false)]
    [DataRow("53200", true)]
    [DataRow("57014", false)]
    [DataRow("57014", true)]
    public void InputRecoveryPreservesFailureClassificationAndScope(string state, bool unrecovered)
    {
        FailWith(state, unrecovered ? NativeErrorFlags.Unrecovered : 0);
        nint previous = NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Fail);
        try
        {
            if (state == "22P02" && !unrecovered)
            {
                Assert.IsFalse(PgInet.TryParse("invalid", out PgInet result));
                Assert.AreEqual(default, result);
            }
            else if (state == "57014")
            {
                PgQueryCanceledException failure = Assert.ThrowsExactly<PgQueryCanceledException>(() => PgInet.TryParse("invalid", out _));
                Assert.AreEqual(state, failure.Diagnostic.SqlState);
                Assert.AreEqual(s_flags, failure.Diagnostic.NativeFlags);
            }
            else
            {
                PgException failure = Assert.ThrowsExactly<PgException>(() => PgInet.TryParse("invalid", out _));
                Assert.AreEqual(state, failure.SqlState);
                Assert.AreEqual(s_flags, failure.NativeFlags);
            }

            Assert.IsFalse(NativeInputRecovery.IsActive);
            if (state == "57014")
            {
                Assert.ThrowsExactly<PgQueryCanceledException>(() => PgInet.Parse("invalid"));
            }
            else
            {
                Assert.ThrowsExactly<PgException>(() => PgInet.Parse("invalid"));
            }

            Assert.AreSequenceEqual<byte>([1, 0], s_requests);
        }
        finally
        {
            NativeBackend.Exit(previous);
            StopFailing();
        }
    }

    /// <summary>
    /// Nested conversion recovery restores its parent even when an inner native parser throws.
    /// </summary>
    [TestMethod]
    public void NestedInputRecoveryRetainsParentAndRestoresOrdinaryCalls()
    {
        FailWith("22P02", 0);
        nint previous = NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Fail);
        try
        {
            Assert.AreEqual(42, NativeInputRecovery.Run(() =>
            {
                Assert.IsFalse(PgInet.TryParse("invalid", out _));
                _ = Assert.ThrowsExactly<PgException>(() => PgInet.Parse("invalid"));
                return 42;
            }));
            _ = Assert.ThrowsExactly<PgException>(() => PgInet.Parse("invalid"));
            Assert.AreSequenceEqual<byte>([1, 1, 0], s_requests);
            Assert.IsFalse(NativeInputRecovery.IsActive);
        }
        finally
        {
            NativeBackend.Exit(previous);
            StopFailing();
        }
    }

    /// <summary>
    /// Returns an independently chosen native error without simulating PostgreSQL rollback.
    /// </summary>
    /// <param name="request">The real managed input request.</param>
    /// <param name="result">The unused native result.</param>
    /// <param name="error">The owned error transport.</param>
    /// <returns>One for the chosen failure.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Fail(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        try
        {
            Assert.AreEqual(SpiOperation.Network, request->_operation);
            s_requests!.Add(request->_recoverInput);
            NativeError.Write(new PgException(s_sqlState!, "native input failure") { NativeFlags = s_flags }, error);
        }
        // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
        catch (Exception failure)
        {
            NativeError.Write(failure, error);
        }

        return 1;
    }
}
