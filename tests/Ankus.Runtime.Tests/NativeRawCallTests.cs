namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies generated-call transport, callback access and owned diagnostic cleanup independently of PostgreSQL.
/// </summary>
[TestClass]
public sealed unsafe class NativeRawCallTests
{
    /// <summary>
    /// Native frame addresses, counts and full-width sizes survive transport, including void and empty records.
    /// </summary>
    [TestMethod]
    public void RawCallsPreserveFrameAndResult()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int destination = 0;
        nint address = (nint)(&destination);
        int calls = 0;
        fixture.Handler = request =>
        {
            Assert.AreEqual(NativeMemoryOperation.NativeCall, request._operation);
            Assert.AreEqual(0x1357, request._pointer);
            NativeCallFrame frame = *(NativeCallFrame*)request._data;
            switch (calls++)
            {
                case 0:
                    Assert.AreEqual((nuint)2, frame._count);
                    Assert.AreEqual(new NativeCallArgument(0x1234, 8), frame._arguments[0]);
                    Assert.AreEqual(new NativeCallArgument(-17, nuint.MaxValue), frame._arguments[1]);
                    Assert.AreEqual(address, frame._result);
                    Assert.AreEqual((nuint)sizeof(int), frame._resultSize);
                    *(int*)frame._result = 731;
                    break;
                case 1:
                    Assert.AreEqual((nuint)0, frame._count);
                    Assert.AreEqual(0, (nint)frame._arguments);
                    Assert.AreEqual(0, frame._result);
                    Assert.AreEqual((nuint)0, frame._resultSize);
                    break;
                case 2:
                    Assert.AreEqual((nuint)0, frame._count);
                    Assert.AreEqual(address, frame._result);
                    Assert.AreEqual((nuint)0, frame._resultSize);
                    break;
                default:
                    Assert.AreEqual((nuint)0, frame._count);
                    Assert.AreEqual(address, frame._result);
                    Assert.AreEqual(nuint.MaxValue, frame._resultSize);
                    break;
            }

            return default;
        };
        NativeRawCall.Invoke(0x1357, [new(0x1234, 8), new(-17, nuint.MaxValue)], address, sizeof(int));
        Assert.AreEqual(731, destination);
        NativeRawCall.Invoke(0x1357, [], 0, 0);
        NativeRawCall.Invoke(0x1357, [], address, 0);
        NativeRawCall.Invoke(0x1357, [], address, nuint.MaxValue);
        Assert.AreEqual(4, calls);
        Assert.HasCount(4, fixture.Requests);
        Assert.AreEqual(731, destination);
        Assert.AreEqual(2 * sizeof(nint), sizeof(NativeCallArgument));
        Assert.AreEqual(4 * sizeof(nint), sizeof(NativeCallFrame));
    }

    /// <summary>
    /// Every rejected native contract reports its distinct reason and permits a later successful invocation.
    /// </summary>
    [TestMethod]
    [DataRow(1, "argument count")]
    [DataRow(2, "argument array")]
    [DataRow(3, "result storage")]
    [DataRow(4, "argument storage")]
    [DataRow(5, "argument alignment")]
    [DataRow(6, "function address")]
    [DataRow(99, "unknown status")]
    [DataRow(-1, "unknown status")]
    public void RawCallStatusesRejectInvalidContractsAndRecover(int status, string reason)
    {
        using var fixture = new MemoryContextTestFixture
        {
            Handler = _ => new NativeMemoryResult { _value = status },
        };
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => NativeRawCall.Invoke(1, [], 0, 0));
        Assert.Contains(reason, failure.Message);
        Assert.Contains($"status {status}", failure.Message);
        Assert.HasCount(1, fixture.Requests);
        fixture.Handler = null;
        NativeRawCall.Invoke(1, [], 0, 0);
        Assert.HasCount(2, fixture.Requests);
        Assert.AreEqual(0, fixture.ErrorReleases);
    }

    /// <summary>
    /// Missing, ended, masked and off-thread scopes cannot enter native code; cleanup and nested scopes retain their native capability.
    /// </summary>
    [TestMethod]
    public void RawCallsRequireActiveBackendScope()
    {
        using var fixture = new MemoryContextTestFixture();
        ArgumentOutOfRangeException missing = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NativeRawCall.Invoke(0, [], 0, 0));
        Assert.AreEqual("body", missing.ParamName);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeRawCall.Invoke(1, [], 0, 0));
        using (MemoryContextTestFixture.Enter())
        {
            nint hidden = NativeMemoryContext.Enter(0);
            try
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeRawCall.Invoke(1, [], 0, 0));
            }
            finally
            {
                NativeMemoryContext.Exit(hidden);
            }

            nint previous = NativeBackend.Enter(0, abortCleanup: true);
            try
            {
                NativeRawCall.Invoke(4, [], 0, 0);
            }
            finally
            {
                NativeBackend.Exit(previous, abortCleanup: true);
            }

            DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => NativeRawCall.Invoke(1, [], 0, 0)));

            Assert.AreSequenceEqual<nint>([4], fixture.Requests.Select(static request => request._pointer));
            using (MemoryContextTestFixture.Enter(29))
            {
                NativeRawCall.Invoke(2, [], 0, 0);
            }

            NativeRawCall.Invoke(3, [], 0, 0);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => NativeRawCall.Invoke(1, [], 0, 0));
        Assert.AreSequenceEqual<nint>([4, 2, 3], fixture.Requests.Select(static request => request._pointer));
    }

    /// <summary>
    /// Owned native error fields are copied and released before the exception escapes and subsequent calls remain usable.
    /// </summary>
    [TestMethod]
    public void RawCallErrorsReleaseDiagnosticsAndRecover()
    {
        using var fixture = new MemoryContextTestFixture
        {
            Handler = static _ => throw new PgException("22023", "raw café", "detail naïve", "hint déjà"),
        };
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgException failure = Assert.ThrowsExactly<PgException>(() => NativeRawCall.Invoke(1, [], 0, 0));
        Assert.AreEqual("22023", failure.SqlState);
        Assert.AreEqual("raw café", failure.Message);
        Assert.AreEqual("detail naïve", failure.Detail);
        Assert.AreEqual("hint déjà", failure.Hint);
        Assert.AreEqual(3, fixture.ErrorReleases);
        fixture.Handler = null;
        NativeRawCall.Invoke(2, [], 0, 0);
        Assert.HasCount(2, fixture.Requests);
        Assert.AreEqual(3, fixture.ErrorReleases);
        Assert.AreEqual("detail naïve", failure.Detail);
    }
}
