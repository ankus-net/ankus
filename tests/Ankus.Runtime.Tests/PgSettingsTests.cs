using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies settings transport and managed callback containment without modeling PostgreSQL's configuration stack.
/// </summary>
[TestClass]
public sealed unsafe class PgSettingsTests
{
    [ThreadStatic]
    private static List<string>? s_received;

    [ThreadStatic]
    private static bool s_reject;

    /// <summary>
    /// Rejects missing arguments and offline calls before executing user code.
    /// </summary>
    [TestMethod]
    public void ScopesRequireArgumentsAndBackend()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => PgSettings.Run(null!, "1s", static () => { }));
        Assert.ThrowsExactly<ArgumentException>(() => PgSettings.Run(string.Empty, "1s", static () => { }));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgSettings.Run("lock_timeout", null!, static () => { }));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgSettings.Run("lock_timeout", "1s", null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgSettings.Run(null!, static () => { }));
        Assert.ThrowsExactly<ArgumentException>(() => PgSettings.Run([new("lock_timeout", "1s"), new(string.Empty, "1s")], static () => { }));
        bool invoked = false;
        Assert.ThrowsExactly<InvalidOperationException>(() => PgSettings.Run("lock_timeout", "1s", () => invoked = true));
        Assert.IsFalse(invoked);
    }

    /// <summary>
    /// Sends names and values in order, returns the callback's result, and rethrows its original exception after native
    /// code has returned.
    /// </summary>
    [TestMethod]
    public void ScopesSendSettingsAndPreserveResultsAndExceptions()
    {
        nint previous = Enter();
        try
        {
            Assert.AreEqual(42, PgSettings.Run([new("lock_timeout", "1s"), new("search_path", "ü, public")], static () => 42));
            Assert.AreSequenceEqual(["lock_timeout", "1s", "search_path", "ü, public", "0"], s_received!);
            var failure = new InvalidOperationException("original managed failure");
            InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() =>
                PgSettings.Run("lock_timeout", "2s", () => throw failure));
            Assert.AreSame(failure, actual);
            Assert.AreSequenceEqual(["lock_timeout", "1s", "search_path", "ü, public", "0", "lock_timeout", "2s", "1"], s_received!);
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// A native rejection propagates without running the callback.
    /// </summary>
    [TestMethod]
    public void RejectedSettingsSkipTheCallback()
    {
        nint previous = Enter();
        try
        {
            s_reject = true;
            bool invoked = false;
            PgException failure = Assert.ThrowsExactly<PgException>(() => PgSettings.Run("lock_timeout", "soon", () => invoked = true));
            Assert.AreEqual("22023", failure.SqlState);
            Assert.IsFalse(invoked);
        }
        finally
        {
            Exit(previous);
        }
    }

    /// <summary>
    /// Installs a synchronous native callback fixture on the current test thread.
    /// </summary>
    /// <returns>The enclosing backend capability.</returns>
    private static nint Enter()
    {
        s_received = [];
        return NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&Execute);
    }

    /// <summary>
    /// Restores the enclosing backend capability and releases recorded fixture state.
    /// </summary>
    /// <param name="previous">The capability captured on entry.</param>
    private static void Exit(nint previous)
    {
        NativeBackend.Exit(previous);
        s_received = null;
        s_reject = false;
    }

    /// <summary>
    /// Records the transported settings and the callback status, or rejects the settings before the callback.
    /// </summary>
    /// <param name="request">The borrowed settings, callback and state.</param>
    /// <param name="result">The unused result transport.</param>
    /// <param name="error">The native diagnostic.</param>
    /// <returns>Zero after the callback ran, or one after a rejection.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Execute(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        try
        {
            Assert.AreEqual(SpiOperation.Settings, request->_operation);
            if (s_reject)
            {
                NativeError.Write(new PgException("22023", "invalid value for parameter \"lock_timeout\": \"soon\""), error);
                return 1;
            }

            for (int index = 0; index < request->_parameterCount; index++)
            {
                s_received!.Add(request->_parameters[index]._value.ReadString());
            }

            var callback = (delegate* unmanaged[Cdecl]<nint, int>)request->_callback;
            s_received!.Add(callback(request->_callbackState).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] This stands in for native code, so every exception goes back as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            return 1;
        }
    }
}
