using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks the native requests behind <see cref="PgErrorContext"/> and the contexts recorded for managed exceptions.
/// </summary>
[TestClass]
public sealed unsafe class PgErrorContextTests
{
    /// <summary>
    /// Pushing sends the UTF-8 text and keeps the native entry, updating replaces the text, and disposing removes the
    /// entry once.
    /// </summary>
    [TestMethod]
    public void ContextsPushUpdateAndPopTheirNativeEntry()
    {
        using var fixture = new MemoryContextTestFixture();
        List<(int Operation, nint Entry, string? Text)> calls = [];
        fixture.Handler = request =>
        {
            Assert.AreEqual(NativeMemoryOperation.ErrorContext, request._operation);
            string? text = request._flags == 3 ? null : Encoding.UTF8.GetString((byte*)request._pointer, checked((int)request._length));
            calls.Add((request._flags, request._context, text));
            return new NativeMemoryResult { _value = request._flags == 1 ? 0x4242 : 0 };
        };
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgErrorContext context = PgErrorContext.Push("applying café 😀");
        Assert.AreEqual("applying café 😀", context.Text);
        context.Text = "";
        Assert.AreEqual("", context.Text);
        context.Dispose();
        context.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => context.Text = "late");
        Assert.AreSequenceEqual<(int, nint, string?)>([(1, 0, "applying café 😀"), (2, 0x4242, ""), (3, 0x4242, null)], calls);
        Assert.ThrowsExactly<ArgumentException>(() => PgErrorContext.Push("zero\0byte"));
    }

    /// <summary>
    /// A managed exception thrown while contexts are active records them, innermost first, and a callback's open
    /// contexts are removed when it exits.
    /// </summary>
    [TestMethod]
    public void ThrownExceptionsRecordTheActiveContexts()
    {
        using var fixture = new MemoryContextTestFixture();
        nint next = 1;
        List<nint> popped = [];
        fixture.Handler = request =>
        {
            if (request._flags == 3)
            {
                popped.Add(request._context);
            }

            return new NativeMemoryResult { _value = request._flags == 1 ? next++ : 0 };
        };
        InvalidOperationException thrown;
        using (MemoryContextTestFixture.Enter())
        {
            _ = PgErrorContext.Push("importing batch 3");
            PgErrorContext inner = PgErrorContext.Push("validating row 6");
            inner.Text = "validating row 7";
            thrown = Assert.ThrowsExactly<InvalidOperationException>(static () => throw new InvalidOperationException("bad row"));
            Assert.IsNull(PgErrorContext.Captured(new InvalidOperationException("never thrown")));
        }

        Assert.AreEqual("validating row 7\nimporting batch 3", PgErrorContext.Captured(thrown));
        Assert.AreSequenceEqual<nint>([2, 1], popped);
        Assert.IsNull(PgErrorContext.Captured(Assert.ThrowsExactly<InvalidOperationException>(static () => throw new InvalidOperationException("after"))));
    }
}
