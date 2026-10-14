namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies callback leases expire independently of native memory operations and nesting depth reuse.
/// </summary>
[TestClass]
public sealed class NativeBorrowScopeTests
{
    /// <summary>
    /// Nested captures preserve the same ancestor lease, including uncaptured intermediate callbacks.
    /// </summary>
    [TestMethod]
    public void NestedScopesPreserveAncestorsAndExpireChildren()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope outer = MemoryContextTestFixture.Enter();
        NativeBorrowScope ancestor = NativeMemoryContext.BorrowScope;
        Assert.AreSame(ancestor, NativeMemoryContext.BorrowScope);
        NativeBorrowScope child;
        using (MemoryContextTestFixture.Enter())
        {
            using (MemoryContextTestFixture.Enter())
            {
                child = NativeMemoryContext.BorrowScope;
                Assert.AreNotSame(ancestor, child);
                ancestor.Validate();
                child.Validate();
            }

            Assert.ThrowsExactly<ObjectDisposedException>(child.Validate);
            ancestor.Validate();
        }

        Assert.AreSame(ancestor, NativeMemoryContext.BorrowScope);
        ancestor.Validate();
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// New callbacks at the same depth never revive earlier leases, and exit requires no native capability call.
    /// </summary>
    [TestMethod]
    public void ReusedDepthNeverRevivesExpiredScope()
    {
        using var fixture = new MemoryContextTestFixture();
        fixture.Handler = static _ => throw new InvalidOperationException("Scope exit must not invoke native code.");
        NativeBorrowScope expired;
        using (MemoryContextTestFixture.Enter())
        {
            expired = NativeMemoryContext.BorrowScope;
        }

        Assert.ThrowsExactly<ObjectDisposedException>(expired.Validate);
        using (MemoryContextTestFixture.Enter())
        {
            NativeBorrowScope current = NativeMemoryContext.BorrowScope;
            Assert.AreNotSame(expired, current);
            Assert.AreEqual(expired.Depth, current.Depth);
            current.Validate();
            Assert.ThrowsExactly<ObjectDisposedException>(expired.Validate);
        }

        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// An uncaptured outer callback can capture a fresh lease after its captured child has exited.
    /// </summary>
    [TestMethod]
    public void OuterScopeCanBeCapturedAfterChildExit()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope outer = MemoryContextTestFixture.Enter();
        NativeBorrowScope child;
        using (MemoryContextTestFixture.Enter())
        {
            child = NativeMemoryContext.BorrowScope;
        }

        NativeBorrowScope parent = NativeMemoryContext.BorrowScope;
        Assert.AreEqual(parent.Depth + 1, child.Depth);
        parent.Validate();
        Assert.ThrowsExactly<ObjectDisposedException>(child.Validate);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// A masked or different provider cannot use an active lease and restoring its provider restores access.
    /// </summary>
    [TestMethod]
    public void MaskedAndForeignProvidersCannotUseActiveScope()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope outer = MemoryContextTestFixture.Enter();
        NativeBorrowScope lease = NativeMemoryContext.BorrowScope;
        using (MemoryContextTestFixture.Enter(29))
        {
            Assert.ThrowsExactly<InvalidOperationException>(lease.Validate);
        }

        nint previous = NativeMemoryContext.Enter(0);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(lease.Validate);
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeMemoryContext.BorrowScope);
        }
        finally
        {
            NativeMemoryContext.Exit(previous);
        }

        lease.Validate();
        Assert.AreSame(lease, NativeMemoryContext.BorrowScope);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// A matching capability on a worker thread cannot validate the original callback's lease.
    /// </summary>
    [TestMethod]
    public void LeaseRequiresOriginatingThreadWithMatchingProvider()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        NativeBorrowScope lease = NativeMemoryContext.BorrowScope;
        DedicatedThread.Run(() =>
        {
            using var otherFixture = new MemoryContextTestFixture();
            using MemoryContextTestFixture.Scope otherScope = MemoryContextTestFixture.Enter();
            Assert.ThrowsExactly<InvalidOperationException>(lease.Validate);
            Assert.IsEmpty(otherFixture.Requests);
        });

        lease.Validate();
        Assert.IsEmpty(fixture.Requests);
    }
}
