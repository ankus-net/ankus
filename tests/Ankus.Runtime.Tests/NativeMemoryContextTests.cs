namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies memory capabilities, provider identity, and native diagnostic ownership.
/// </summary>
[TestClass]
public sealed class NativeMemoryContextTests
{
    /// <summary>
    /// Public context entry points require a live backend capability.
    /// </summary>
    [TestMethod]
    public void DetachedOperationsRequireBackendCapability()
    {
        using var fixture = new MemoryContextTestFixture();
        Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Get(PgMemoryContextKind.Top));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Create("detached"));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Disabled and foreign nested callbacks restore the exact outer capability after an exception.
    /// </summary>
    [TestMethod]
    public void NestedAndMaskedBindingsRestoreOuterProvider()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope outer = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        nint previous = NativeMemoryContext.Enter(0);
        Assert.AreEqual(outer.Address, previous);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => context.Name);
            var expected = new InvalidOperationException("nested exit");
            InvalidOperationException? actual = null;
            try
            {
                using MemoryContextTestFixture.Scope nested = MemoryContextTestFixture.Enter(29);
                Assert.ThrowsExactly<InvalidOperationException>(() => context.Name);
                Assert.AreEqual(101, PgMemoryContext.Current.Id);
                throw expected;
            }
            catch (InvalidOperationException exception)
            {
                actual = exception;
            }

            Assert.AreSame(expected, actual);
            Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
        }
        finally
        {
            NativeMemoryContext.Exit(previous);
        }

        Assert.AreEqual("context café 🐘", context.Name);
    }

    /// <summary>
    /// A handle follows its stable provider across distinct callback envelopes.
    /// </summary>
    [TestMethod]
    public void HandlesRemainUsableAcrossCallbackEnvelopesWithSameProvider()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope outer = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        using PgAllocation allocation = context.Allocate(8);
        using (MemoryContextTestFixture.Scope nested = MemoryContextTestFixture.Enter())
        {
            Assert.AreNotEqual(outer.Address, nested.Address);
            Assert.AreEqual("context café 🐘", context.Name);
            Assert.AreEqual(303, allocation.Context.Id);
            Assert.AreEqual((nuint)8, allocation.Length);
        }

        Assert.IsTrue(context.IsAlive);
        Assert.AreEqual(303, allocation.Context.Id);
    }

    /// <summary>
    /// Callback lifetime ends the capability while retained context and allocation handles can join a later callback.
    /// </summary>
    [TestMethod]
    public void RetainedHandlesRequireAndAcceptLaterCallbackFromSameProvider()
    {
        using var fixture = new MemoryContextTestFixture();
        PgMemoryContext context;
        PgAllocation allocation;
        using (MemoryContextTestFixture.Enter())
        {
            context = PgMemoryContext.Current;
            allocation = context.Allocate(8);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => context.Name);
        Assert.ThrowsExactly<InvalidOperationException>(() => allocation.Context);
        using (MemoryContextTestFixture.Enter())
        {
            Assert.AreEqual("context café 🐘", context.Name);
            Assert.AreEqual(303, allocation.Context.Id);
            allocation.Dispose();
        }

        Assert.AreEqual((nuint)0, allocation.Length);
    }

    /// <summary>
    /// Foreign provider handles cannot reach the currently bound native bridge.
    /// </summary>
    [TestMethod]
    public void ForeignProviderRejectsContextAndAllocationWithoutNativeCall()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope outer = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        using PgAllocation allocation = context.Allocate(8);
        fixture.Requests.Clear();
        using (MemoryContextTestFixture.Enter(29))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => context.Name);
            Assert.ThrowsExactly<InvalidOperationException>(() => context.IsAlive);
            Assert.ThrowsExactly<InvalidOperationException>(() => context.Allocate(1));
            Assert.ThrowsExactly<InvalidOperationException>(() => allocation.Context);
            Assert.ThrowsExactly<InvalidOperationException>(() => allocation.Read<int>());
            Assert.ThrowsExactly<InvalidOperationException>(allocation.Dispose);
            Assert.IsEmpty(fixture.Requests);
        }

        Assert.AreEqual("context café 🐘", context.Name);
    }

    /// <summary>
    /// Memory scopes do not flow to worker threads or change another thread's provider.
    /// </summary>
    [TestMethod]
    public void CapabilitiesRemainThreadBound()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        DedicatedThread.Run(() =>
        {
            using var workerFixture = new MemoryContextTestFixture();
            Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
            Assert.ThrowsExactly<InvalidOperationException>(() => context.Name);
            using (MemoryContextTestFixture.Enter(29))
            {
                Assert.AreEqual(101, PgMemoryContext.Current.Id);
                Assert.ThrowsExactly<InvalidOperationException>(() => context.Name);
            }

            Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
        });
        Assert.AreEqual("context café 🐘", context.Name);
    }

    /// <summary>
    /// Native failures copy diagnostics, release their buffers, and leave the callback usable.
    /// </summary>
    [TestMethod]
    public void NativeErrorsReleaseOwnedDiagnosticsAndRecover()
    {
        using var fixture = new MemoryContextTestFixture
        {
            Handler = static _ => throw new PgException("22023", "native café", "detail", "hint"),
        };
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgException exception = Assert.ThrowsExactly<PgException>(() => PgMemoryContext.Current);
        Assert.AreEqual("22023", exception.SqlState);
        Assert.AreEqual("native café", exception.Message);
        Assert.AreEqual("detail", exception.Detail);
        Assert.AreEqual("hint", exception.Hint);
        Assert.AreEqual(3, fixture.ErrorReleases);
        fixture.Handler = null;
        Assert.AreEqual(101, PgMemoryContext.Current.Id);
        Assert.AreEqual("native café", exception.Message);
    }

    /// <summary>
    /// Exiting an absent scope fails without corrupting the next valid callback.
    /// </summary>
    [TestMethod]
    public void ExitRejectsUnbalancedStack()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeMemoryContext.Exit(0));
        using var fixture = new MemoryContextTestFixture();
        using (MemoryContextTestFixture.Enter())
        {
            Assert.AreEqual(101, PgMemoryContext.Current.Id);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
    }
}
