using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies in-place reads of flat varlena payloads and the cases that must take PostgreSQL's native path.
/// </summary>
[TestClass]
public sealed unsafe class NativeFlatVarlenaTests
{
    /// <summary>
    /// One- and four-byte headers give the payload in place; compressed and external values, a null address, and text
    /// PostgreSQL would reject are left to the native path.
    /// </summary>
    [TestMethod]
    public void FlatHeadersAreReadInPlace()
    {
        byte* buffer = (byte*)NativeMemory.AllocZeroed(16);
        try
        {
            *(uint*)buffer = (3 + 4) << 2;
            buffer[4] = (byte)'a';
            buffer[5] = (byte)'b';
            buffer[6] = (byte)'c';
            Assert.IsTrue(NativeBorrowedBuffer.TryGetFlatBytes((nuint)buffer, true, out nint data, out int length));
            Assert.AreEqual((nint)(buffer + 4), data);
            Assert.AreEqual(3, length);

            *(uint*)buffer = ((3 + 4) << 2) | 2;
            Assert.IsFalse(NativeBorrowedBuffer.TryGetFlatBytes((nuint)buffer, false, out _, out _));

            buffer[0] = ((2 + 1) << 1) | 1;
            buffer[1] = (byte)'x';
            buffer[2] = (byte)'y';
            Assert.IsTrue(NativeBorrowedBuffer.TryGetFlatBytes((nuint)buffer, true, out data, out length));
            Assert.AreEqual((nint)(buffer + 1), data);
            Assert.AreEqual(2, length);

            buffer[0] = 0x01;
            Assert.IsFalse(NativeBorrowedBuffer.TryGetFlatBytes((nuint)buffer, false, out _, out _));
            Assert.IsFalse(NativeBorrowedBuffer.TryGetFlatBytes(0, false, out _, out _));

            buffer[0] = ((2 + 1) << 1) | 1;
            buffer[1] = 0xC3;
            buffer[2] = 0x28;
            Assert.IsFalse(NativeBorrowedBuffer.TryGetFlatBytes((nuint)buffer, true, out _, out _));
            Assert.IsTrue(NativeBorrowedBuffer.TryGetFlatBytes((nuint)buffer, false, out _, out length));
            Assert.AreEqual(2, length);

            buffer[1] = (byte)'x';
            buffer[2] = 0;
            Assert.IsFalse(NativeBorrowedBuffer.TryGetFlatBytes((nuint)buffer, true, out _, out _));

            buffer[0] = (0 + 1) << 1 | 1;
            Assert.IsTrue(NativeBorrowedBuffer.TryGetFlatBytes((nuint)buffer, true, out _, out length));
            Assert.AreEqual(0, length);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }
}
