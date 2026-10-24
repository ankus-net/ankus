namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks <see cref="PgProcess.OnExit"/> registration and the order and failure handling of exit callbacks.
/// </summary>
[TestClass]
public sealed unsafe class PgProcessTests
{
    /// <summary>
    /// The native dispatcher is registered once per process; callbacks run newest first with the exit code, a
    /// failing callback does not stop the others and is reported, and a removed callback never runs.
    /// </summary>
    [TestMethod]
    public void ExitCallbacksRunNewestFirstAndReportTheFirstFailure()
    {
        using var fixture = new MemoryContextTestFixture();
        nint dispatcher = 0;
        fixture.Handler = request =>
        {
            Assert.AreEqual(NativeMemoryOperation.ProcessExit, request._operation);
            dispatcher = request._pointer;
            return default;
        };
        List<string> ran = [];
        using (MemoryContextTestFixture.Enter())
        {
            _ = PgProcess.OnExit(code => ran.Add("first " + code));
            using IDisposable removed = PgProcess.OnExit(code => ran.Add("removed " + code));
            _ = PgProcess.OnExit(_ => throw new InvalidOperationException("exit failed"));
            _ = PgProcess.OnExit(code => ran.Add("last " + code));
            removed.Dispose();
        }

        Assert.AreNotEqual(0, dispatcher);
        Assert.HasCount(1, fixture.Requests);
        NativeCallError error = default;
        using (MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter())
        {
            int status = ((delegate* unmanaged[Cdecl]<nint, nint, nint, NativeCallError*, int>)dispatcher)(1, scope.Address, 0, &error);
            Assert.AreEqual(1, status);
        }

        try
        {
            Assert.AreSequenceEqual(["last 1", "first 1"], ran);
            Assert.AreEqual(NativeError.PackSqlState("38000"), error.SqlState);
            Assert.Contains("exit failed", error.ToManagedException().Message);
        }
        finally
        {
            error.Release();
        }

        NativeCallError again = default;
        using (MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter())
        {
            Assert.AreEqual(0, ((delegate* unmanaged[Cdecl]<nint, nint, nint, NativeCallError*, int>)dispatcher)(0, scope.Address, 0, &again));
        }

        Assert.HasCount(2, ran);
    }
}
