using System.Buffers.Binary;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks exact C-string transport bytes and independent native-array envelopes.
/// </summary>
[TestClass]
public sealed class PgCStringTransportTests
{
    /// <summary>
    /// Managed snapshots outlive native transport and writers preserve every uninterpreted payload byte.
    /// </summary>
    /// <param name="hex">The independently supplied payload, including empty and invalid UTF-8 bytes.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("0180FF")]
    [DataRow("C3A9")]
    public void ExactBytesOutliveNativeTransport(string hex)
    {
        byte[] bytes = Convert.FromHexString(hex);
        NativeValue input = NativeValue.FromBytes(bytes);
        PgCString copy;
        try
        {
            copy = input.ReadCString();
        }
        finally
        {
            input.Release();
        }

        Assert.AreEqual(hex, Convert.ToHexString(copy.AsSpan()));
        Assert.AreEqual(hex + "00", Convert.ToHexString(copy.AsNullTerminatedSpan()));
        NativeValue output = NativeValue.FromCString(copy);
        try
        {
            Assert.AreEqual((byte)0, output.IsNull);
            Assert.AreEqual(hex, Convert.ToHexString(output.ReadBytes()));
        }
        finally
        {
            output.Release();
        }
    }

    /// <summary>
    /// Absent, malformed and embedded-zero input envelopes fail before a lossy managed value can escape.
    /// </summary>
    [TestMethod]
    public void InvalidCStringTransportsRejectLoss()
    {
        NativeValue absent = new() { IsNull = 1 };
        Assert.ThrowsExactly<InvalidOperationException>(absent.ReadCString);
        Assert.ThrowsExactly<InvalidOperationException>(absent.ReadBorrowedCString);
        Assert.ThrowsExactly<InvalidOperationException>(absent.ReadOwnedCStringView);
        NativeValue missing = default;
        Length(ref missing) = 1;
        Assert.ThrowsExactly<InvalidOperationException>(() => missing.ReadCString());
        Length(ref missing) = -1;
        Assert.ThrowsExactly<InvalidOperationException>(() => missing.ReadCString());
        NativeValue zeroAddress = default;
        Assert.ThrowsExactly<InvalidOperationException>(zeroAddress.ReadBorrowedCString);
        Assert.ThrowsExactly<InvalidOperationException>(zeroAddress.ReadOwnedCStringView);
        Assert.AreEqual("value", Assert.ThrowsExactly<ArgumentNullException>(() => NativeValue.FromCString(null!)).ParamName);
        NativeValue terminated = NativeValue.FromBytes([65, 0, 66]);
        try
        {
            Assert.AreEqual("bytes", Assert.ThrowsExactly<ArgumentException>(terminated.ReadCString).ParamName);
        }
        finally
        {
            terminated.Release();
        }
    }

    /// <summary>
    /// A hand-authored array envelope distinguishes empty and NULL cells without relying on the array writer.
    /// </summary>
    [TestMethod]
    public void CStringArraysReadIndependentBytesAndExactShape()
    {
        byte[] bytes = new byte[106];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 2275);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), -7);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(100), 2);
        bytes[104] = 128;
        bytes[105] = 255;
        NativeValue input = NativeValue.FromBytes(bytes);
        ArrayFlag(ref input) = -1;
        PgArray<PgCString?> copy;
        try
        {
            copy = input.ReadArray<PgCString?>();
            Assert.ThrowsExactly<InvalidCastException>(() => input.ReadArray<string?>());
        }
        finally
        {
            input.Release();
        }

        Assert.AreEqual(2275U, copy.ElementTypeOid);
        Assert.AreEqual(1263U, SpiParameter.Create(copy).TypeOid);
        Assert.AreEqual(1263U, SpiParameter.Create<PgCString?[]>([null, new([])]).TypeOid);
        Assert.AreSequenceEqual<int>([3], copy.Lengths.ToArray());
        Assert.AreSequenceEqual<int>([-7], copy.LowerBounds.ToArray());
        Assert.IsNull(copy[0]);
        Assert.IsNotNull(copy[1]);
        Assert.IsEmpty(copy[1]!);
        Assert.AreSequenceEqual<byte>([128, 255], copy[2]!);
        NativeValue output = NativeValue.FromArray(copy);
        try
        {
            Assert.AreSequenceEqual(bytes, output.ReadBytes());
        }
        finally
        {
            output.Release();
        }
    }

    /// <summary>
    /// Sets deliberately invalid transport lengths without weakening production visibility.
    /// </summary>
    private static ref int Length(ref NativeValue value) => ref value._length;

    /// <summary>
    /// Marks the independently authored native envelope as an array.
    /// </summary>
    private static ref int ArrayFlag(ref NativeValue value) => ref value._auxiliary1;
}
