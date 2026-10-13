using System.Runtime.CompilerServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks the reusable buffer that carries a scalar function's text result to native code.
/// </summary>
[TestClass]
public sealed unsafe class NativeResultTextTests
{
    /// <summary>
    /// Results on one thread share a buffer with no release of their own, and each holds exactly the UTF-8 bytes of
    /// its string followed by a terminating zero.
    /// </summary>
    [TestMethod]
    public void ResultsReuseOneBufferWithoutRelease()
    {
        NativeValue first = NativeValue.FromResultString("café 😀");
        byte* address = Data(ref first);
        Assert.AreEqual("café 😀", first.ReadString());
        Assert.AreEqual(0, Terminator(first));
        NativeValue second = NativeValue.FromResultString("");
        Assert.AreEqual((nint)address, (nint)Data(ref second));
        Assert.AreEqual(string.Empty, second.ReadString());
        Assert.AreEqual(0, Terminator(second));
        Assert.IsTrue(Release(ref second) == null);
        second.Release();
    }

    /// <summary>
    /// A result too long for the buffer gets a larger one sized to its exact bytes, and the next short result returns
    /// to a small buffer.
    /// </summary>
    [TestMethod]
    public void LongResultsAreCountedAndDoNotKeepTheirBuffer()
    {
        string large = new('é', 400_000);
        NativeValue first = NativeValue.FromResultString(large);
        Assert.AreEqual(800_000, Length(ref first));
        Assert.AreEqual(large, first.ReadString());
        Assert.AreEqual(800_000, Capacity(default));
        NativeValue second = NativeValue.FromResultString("small");
        Assert.AreEqual("small", second.ReadString());
        Assert.AreEqual(256, Capacity(default));
    }

    /// <summary>
    /// Text PostgreSQL cannot store is rejected before anything is written.
    /// </summary>
    /// <param name="value">The rejected string.</param>
    [TestMethod]
    [DataRow("a\0b")]
    [DataRow("lone \uD800 surrogate")]
    public void UnstorableTextIsRejected(string value)
        => Assert.Throws<ArgumentException>(() => NativeValue.FromResultString(value));

    private static byte Terminator(NativeValue value) => Data(ref value)[Length(ref value)];

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_data")]
    private static extern ref byte* Data(ref NativeValue value);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_length")]
    private static extern ref int Length(ref NativeValue value);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "s_resultTextCapacity")]
    private static extern ref int Capacity(NativeValue type);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_release")]
    private static extern ref delegate* unmanaged[Cdecl]<void*, void> Release(ref NativeValue value);
}
