using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies managed context ownership, validation, switching, and allocation outcomes.
/// </summary>
[TestClass]
public sealed class PgMemoryContextTests
{
    /// <summary>
    /// Invalid predefined discriminators are rejected before any native call.
    /// </summary>
    /// <param name="value">A value immediately outside the defined range.</param>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(9)]
    public void InvalidKindsAreRejectedBeforeNativeCalls(int value)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgMemoryContext.Get((PgMemoryContextKind)value));
        Assert.AreEqual("kind", exception.ParamName);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// A null context name is rejected before any native call.
    /// </summary>
    [TestMethod]
    public void NullNameIsRejectedBeforeNativeCalls()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        ArgumentNullException exception = Assert.ThrowsExactly<ArgumentNullException>(() => PgMemoryContext.Create(null!));
        Assert.AreEqual("name", exception.ParamName);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Empty names and embedded zero characters cannot reach PostgreSQL's string API.
    /// </summary>
    /// <param name="name">The invalid context identifier.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("before\0after")]
    public void InvalidNamesAreRejectedBeforeNativeCalls(string name)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        ArgumentException exception = Assert.ThrowsExactly<ArgumentException>(() => PgMemoryContext.Create(name));
        Assert.AreEqual("name", exception.ParamName);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Invalid UTF-16 identifiers cannot silently change through encoder replacement characters.
    /// </summary>
    /// <param name="name">An identifier containing an unpaired surrogate.</param>
    [TestMethod]
    [DataRow("high\ud800")]
    [DataRow("low\udc00")]
    public void InvalidUtf16NameIsRejectedBeforeNativeCalls(string name)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        Assert.ThrowsExactly<EncoderFallbackException>(() => PgMemoryContext.Create(name));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Context identifiers cross the native boundary as exact UTF-8 and are returned as managed strings.
    /// </summary>
    [TestMethod]
    public void CreatePreservesUtf8NameAndExplicitParent()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext parent = PgMemoryContext.Current;
        string? observedName = null;
        nint observedParent = 0;
        nuint observedLength = 0;
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.Create)
            {
                observedName = Marshal.PtrToStringUTF8(request._data);
                observedParent = request._context;
                observedLength = request._length;
            }

            return fixture.Respond(request);
        };
        using PgMemoryContext child = PgMemoryContext.Create("child é 🐘", parent);
        Assert.AreEqual("child é 🐘", observedName);
        Assert.AreEqual((nuint)Encoding.UTF8.GetByteCount("child é 🐘"), observedLength);
        Assert.AreEqual(parent.Id, observedParent);
        Assert.AreEqual(202, child.Id);
        Assert.AreEqual("context café 🐘", child.Name);
    }

    /// <summary>
    /// An absent predefined context is optional, whereas the current context is required.
    /// </summary>
    [TestMethod]
    public void MissingCurrentAndPredefinedContextsHaveDistinctOutcomes()
    {
        using var fixture = new MemoryContextTestFixture { Current = 0 };
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        Assert.IsNull(PgMemoryContext.Get(PgMemoryContextKind.Portal));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
        Assert.AreEqual(NativeMemoryOperation.Predefined, fixture.Requests[0]._operation);
        Assert.AreEqual((nint)PgMemoryContextKind.Portal, fixture.Requests[0]._value);
    }

    /// <summary>
    /// Disposing a borrowed handle retires only that handle and leaves native ownership untouched.
    /// </summary>
    [TestMethod]
    public void BorrowedDisposeNeverDeletesNativeContext()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        context.Dispose();
        context.Dispose();
        Assert.IsFalse(context.IsAlive);
        Assert.ThrowsExactly<ObjectDisposedException>(() => context.Name);
        Assert.ThrowsExactly<ObjectDisposedException>(() => context.Allocate(1));
        Assert.IsEmpty(fixture.Requests);
        Assert.IsTrue(PgMemoryContext.Current.IsAlive);
    }

    /// <summary>
    /// An unsuccessful native delete leaves the owned context available for a later retry.
    /// </summary>
    [TestMethod]
    public void OwnedDisposeCanRetryAfterNativeFailureAndDeletesOnlyOnce()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgMemoryContext context = PgMemoryContext.Create("owned");
        int deletes = 0;
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.Delete && ++deletes == 1)
            {
                throw new PgException("55006", "context is active");
            }

            return fixture.Respond(request);
        };
        PgException exception = Assert.ThrowsExactly<PgException>(context.Dispose);
        Assert.AreEqual("55006", exception.SqlState);
        Assert.IsTrue(context.IsAlive);
        context.Dispose();
        context.Dispose();
        Assert.AreEqual(2, deletes);
        Assert.IsFalse(context.IsAlive);
        Assert.ThrowsExactly<ObjectDisposedException>(() => context.Name);
    }

    /// <summary>
    /// Only the native stale-handle SQLSTATE becomes an absent liveness result or disposed exception.
    /// </summary>
    [TestMethod]
    public void StaleContextCannotAllocateAndReportsNotAlive()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        fixture.Handler = static _ => throw new PgException("55000", "stale context");
        Assert.IsFalse(context.IsAlive);
        Assert.ThrowsExactly<ObjectDisposedException>(() => context.Allocate(8));
        Assert.AreSequenceEqual([NativeMemoryOperation.Name, NativeMemoryOperation.Name], fixture.Requests.Select(static request => request._operation));
        Assert.AreEqual(2, fixture.ErrorReleases);
    }

    /// <summary>
    /// Operational failures remain observable when checking context liveness or using a live handle.
    /// </summary>
    [TestMethod]
    public void LivenessPropagatesNonStaleNativeErrors()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Handler = static _ => throw new PgException("53200", "diagnostic allocation failed");
        Assert.AreEqual("53200", Assert.ThrowsExactly<PgException>(() => context.IsAlive).SqlState);
        Assert.AreEqual("53200", Assert.ThrowsExactly<PgException>(() => context.Allocate(8)).SqlState);
    }

    /// <summary>
    /// Null callbacks cannot switch the current native memory context.
    /// </summary>
    [TestMethod]
    public void NullCallbacksAreRejectedBeforeNativeCalls()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        Assert.AreEqual("action", Assert.ThrowsExactly<ArgumentNullException>(() => context.Run(null!)).ParamName);
        Assert.AreEqual("func", Assert.ThrowsExactly<ArgumentNullException>(() => context.Run((Func<int>)null!)).ParamName);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Both callback overloads restore the prior native context on success and managed failure.
    /// </summary>
    [TestMethod]
    public void RunRestoresPreviousContextOnSuccessAndFailure()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgMemoryContext context = PgMemoryContext.Create("child");
        int result = context.Run(() =>
        {
            Assert.AreEqual(context.Id, PgMemoryContext.Current.Id);
            return 37;
        });
        Assert.AreEqual(37, result);
        Assert.AreEqual(101, PgMemoryContext.Current.Id);
        var expected = new InvalidOperationException("callback failed");
        InvalidOperationException actual = Assert.ThrowsExactly<InvalidOperationException>(() => context.Run(() =>
        {
            Assert.AreEqual(context.Id, PgMemoryContext.Current.Id);
            throw expected;
        }));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(101, PgMemoryContext.Current.Id);
        Assert.AreSequenceEqual([202, 101, 202, 101], fixture.Requests
            .Where(static request => request._operation == NativeMemoryOperation.Switch)
            .Select(static request => request._context));
    }

    /// <summary>
    /// Restoration errors preserve the original callback exception and the native diagnostic together.
    /// </summary>
    [TestMethod]
    public void RunPreservesPrimaryAndRestoreErrors()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgMemoryContext context = PgMemoryContext.Create("child");
        fixture.Handler = request => request._operation == NativeMemoryOperation.Switch && request._context == 101
            ? throw new PgException("55000", "previous context expired")
            : fixture.Respond(request);
        var expected = new InvalidOperationException("original failure");
        AggregateException exception = Assert.ThrowsExactly<AggregateException>(() => context.Run(() => throw expected));
        Assert.HasCount(2, exception.InnerExceptions);
        Assert.AreSame(expected, exception.InnerExceptions[0]);
        PgException restore = Assert.IsInstanceOfType<PgException>(exception.InnerExceptions[1]);
        Assert.AreEqual("55000", restore.SqlState);
        Assert.AreEqual("previous context expired", restore.Message);
    }

    /// <summary>
    /// A successful callback cannot conceal a failure to restore the prior context.
    /// </summary>
    [TestMethod]
    public void RunPropagatesRestoreFailureAfterSuccessfulCallback()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgMemoryContext context = PgMemoryContext.Create("child");
        fixture.Handler = request => request._operation == NativeMemoryOperation.Switch && request._context == 101
            ? throw new PgException("55000", "restore failed")
            : fixture.Respond(request);
        int calls = 0;
        PgException exception = Assert.ThrowsExactly<PgException>(() => context.Run(() => ++calls));
        Assert.AreEqual(1, calls);
        Assert.AreEqual("55000", exception.SqlState);
        Assert.AreEqual("restore failed", exception.Message);
    }

    /// <summary>
    /// Only a successful native no-OOM response with no pointer becomes a null allocation.
    /// </summary>
    /// <param name="zeroed">Whether the requested storage must be cleared.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TryAllocateReturnsNullOnlyForSuccessfulNativeNull(bool zeroed)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Handler = request => request._operation == NativeMemoryOperation.Allocate ? default : fixture.Respond(request);
        Assert.IsNull(context.TryAllocate(123, zeroed));
        NativeMemoryRequest allocation = fixture.Requests[^1];
        Assert.AreEqual(NativeMemoryOperation.Allocate, allocation._operation);
        Assert.AreEqual((nuint)123, allocation._length);
        Assert.AreEqual(context.Id, allocation._context);
        Assert.AreEqual(zeroed ? 3 : 2, allocation._flags);
        fixture.Handler = null;
        using PgAllocation? success = context.TryAllocate(8, zeroed);
        Assert.IsNotNull(success);
        Assert.AreEqual((nuint)8, success.Length);
    }

    /// <summary>
    /// Native validation and raised OOM diagnostics cannot be mistaken for a successful no-OOM null result.
    /// </summary>
    /// <param name="sqlState">The native failure SQLSTATE.</param>
    [TestMethod]
    [DataRow("22023")]
    [DataRow("53200")]
    public void TryAllocatePropagatesNativeErrors(string sqlState)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Handler = request => request._operation == NativeMemoryOperation.Allocate
            ? throw new PgException(sqlState, "allocation rejected")
            : fixture.Respond(request);
        PgException exception = Assert.ThrowsExactly<PgException>(() => context.TryAllocate(8));
        Assert.AreEqual(sqlState, exception.SqlState);
        Assert.AreEqual("allocation rejected", exception.Message);
        Assert.AreEqual(1, fixture.ErrorReleases);
    }

    /// <summary>
    /// Parent and native accounting responses retain their optional identity and full native-size values.
    /// </summary>
    [TestMethod]
    public void MetadataPreservesNativeResults()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        PgMemoryContext? parent = context.Parent;
        Assert.IsNotNull(parent);
        Assert.AreEqual(303, parent.Id);
        fixture.Handler = request => request._operation switch
        {
            NativeMemoryOperation.Parent => default,
            NativeMemoryOperation.IsEmpty => new NativeMemoryResult { _value = 1 },
            NativeMemoryOperation.Statistics => new NativeMemoryResult { _length = nuint.MaxValue },
            _ => fixture.Respond(request),
        };
        Assert.IsNull(context.Parent);
        Assert.IsTrue(context.IsEmpty);
        Assert.AreEqual(nuint.MaxValue, context.GetAllocatedBytes());
        fixture.Handler = null;
        Assert.IsFalse(context.IsEmpty);
    }
}
