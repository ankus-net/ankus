using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies inline atomic cells without a backend or shared-memory descriptor.
/// </summary>
[TestClass]
public sealed unsafe class PgAtomicValueTests
{
    /// <summary>
    /// Every supported width and enum exchanges exact bits without changing adjacent bytes.
    /// </summary>
    [TestMethod]
    public void AtomicValuesPreserveExactScalarBits()
    {
        CheckScalar(false, true);
        CheckScalar((byte)0x81, byte.MaxValue);
        CheckScalar(sbyte.MinValue, sbyte.MaxValue);
        CheckScalar(short.MinValue, short.MaxValue);
        CheckScalar((ushort)0x8173, ushort.MaxValue);
        CheckScalar(int.MinValue, int.MaxValue);
        CheckScalar(0xFEDCBA98u, uint.MaxValue);
        CheckScalar(long.MinValue, long.MaxValue);
        CheckScalar(0xFEDCBA9876543210UL, ulong.MaxValue);
        CheckScalar(nint.MinValue, nint.MaxValue);
        CheckScalar(nuint.MinValue, nuint.MaxValue);
        CheckScalar('\u8173', '\uffff');
        CheckScalar(Marker.Initial, Marker.Next);
        CheckScalar(BitConverter.Int32BitsToSingle(0x7FC00073), BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)));
        CheckScalar(BitConverter.Int64BitsToDouble(0x7FF8000000000073), BitConverter.Int64BitsToDouble(long.MinValue));
    }

    /// <summary>
    /// Comparisons distinguish separate NaN payloads and signed zero rather than using value equality.
    /// </summary>
    [TestMethod]
    public void AtomicValuesCompareFloatingPointBits()
    {
        var wide = new PgAtomicValue<double>(BitConverter.Int64BitsToDouble(0x7FF8000000000073));
        double other = BitConverter.Int64BitsToDouble(0x7FF8000000000074);
        Assert.AreEqual(0x7FF8000000000073, BitConverter.DoubleToInt64Bits(wide.CompareExchange(1, other)));
        Assert.AreEqual(0x7FF8000000000073, BitConverter.DoubleToInt64Bits(wide.Value));
        wide.Exchange(BitConverter.Int64BitsToDouble(long.MinValue));
        Assert.AreEqual(long.MinValue, BitConverter.DoubleToInt64Bits(wide.CompareExchange(1, 0)));
        Assert.AreEqual(long.MinValue, BitConverter.DoubleToInt64Bits(wide.Value));
        Assert.AreEqual(long.MinValue, BitConverter.DoubleToInt64Bits(wide.CompareExchange(1, BitConverter.Int64BitsToDouble(long.MinValue))));
        Assert.AreEqual(1d, wide.Value);

        var narrow = new PgAtomicValue<float>(BitConverter.Int32BitsToSingle(0x7FC00073));
        Assert.AreEqual(0x7FC00073, BitConverter.SingleToInt32Bits(narrow.CompareExchange(1, BitConverter.Int32BitsToSingle(0x7FC00074))));
        Assert.AreEqual(0x7FC00073, BitConverter.SingleToInt32Bits(narrow.Value));
        narrow.Exchange(BitConverter.Int32BitsToSingle(int.MinValue));
        Assert.AreEqual(int.MinValue, BitConverter.SingleToInt32Bits(narrow.CompareExchange(1, 0)));
        Assert.AreEqual(int.MinValue, BitConverter.SingleToInt32Bits(narrow.Value));
    }

    /// <summary>
    /// Integer updates wrap and keep .NET return-new arithmetic and return-old bitwise conventions.
    /// </summary>
    [TestMethod]
    public void AtomicValuesPreserveArithmeticContracts()
    {
        CheckInteger<sbyte>();
        CheckInteger<byte>();
        CheckInteger<short>();
        CheckInteger<ushort>();
        CheckInteger<int>();
        CheckInteger<uint>();
        CheckInteger<long>();
        CheckInteger<ulong>();
        CheckInteger<nint>();
        CheckInteger<nuint>();
        var flag = new PgAtomicValue<bool>(true);
        Assert.IsTrue(flag.And(false));
        Assert.IsFalse(flag.Value);
        Assert.IsFalse(flag.Or(true));
        Assert.IsTrue(flag.Value);
        Assert.IsTrue(flag.Xor(true));
        Assert.IsFalse(flag.Value);
        Assert.IsFalse(flag.Xor(false));
        Assert.IsFalse(flag.Value);
    }

    /// <summary>
    /// Unsupported defaults and unaligned embedded fields fail before touching their bytes.
    /// </summary>
    [TestMethod]
    public void AtomicValuesRejectInvalidStorage()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => new PgAtomicValue<decimal>(1));
        Assert.ThrowsExactly<NotSupportedException>(() => default(PgAtomicValue<Guid>).Value);
        Assert.ThrowsExactly<NotSupportedException>(() => default(PgAtomicValue<Int128>).Exchange(1));
        Assert.ThrowsExactly<NotSupportedException>(() => default(PgAtomicValue<Pair>).CompareExchange(default, default));
        byte* bytes = (byte*)NativeMemory.Alloc(24);
        try
        {
            new Span<byte>(bytes, 24).Fill(0xA5);
            var unaligned = (PgAtomicValue<long>*)(bytes + 1);
            Assert.ThrowsExactly<InvalidOperationException>(() => unaligned->Value);
            Assert.ThrowsExactly<InvalidOperationException>(() => unaligned->Exchange(73));
            Assert.ThrowsExactly<InvalidOperationException>(() => unaligned->CompareExchange(73, 0));
            Assert.ThrowsExactly<InvalidOperationException>(() => unaligned->Add(1));
            Assert.AreEqual(-1, new ReadOnlySpan<byte>(bytes, 24).IndexOfAnyExcept((byte)0xA5));
        }
        finally
        {
            NativeMemory.Free(bytes);
        }
    }

    /// <summary>
    /// Readonly members update the original field; an explicit copy has independent storage.
    /// </summary>
    [TestMethod]
    public void AtomicValuesUpdateReadonlyAggregateFields()
    {
        var owner = new Owner();
        Assert.AreEqual(73L, owner._counter.Value);
        Assert.AreEqual(74L, owner._counter.Increment());
        Assert.AreEqual(74L, owner._counter.Exchange(91));
        Assert.AreEqual(91L, owner._counter.Value);
        PgAtomicValue<long> copy = owner._counter;
        Assert.AreEqual(92L, copy.Increment());
        Assert.AreEqual(91L, owner._counter.Value);
        Assert.AreEqual(92L, copy.Value);
    }

    private static void CheckScalar<T>(T initial, T next) where T : unmanaged
    {
        Assert.AreEqual(8, sizeof(PgAtomicValue<T>));
        PgAtomicValue<T> zero = default;
        CheckBits(default, zero.Value);
        CheckBits(default, zero.Exchange(initial));
        CheckBits(initial, zero.Value);
        byte* bytes = (byte*)NativeMemory.Alloc(24);
        try
        {
            new Span<byte>(bytes, 24).Fill(0xA5);
            ref PgAtomicValue<T> cell = ref Unsafe.AsRef<PgAtomicValue<T>>(bytes + 8);
            cell = new PgAtomicValue<T>(initial);
            CheckBits(initial, cell.Value);
            CheckBits(initial, cell.Exchange(next));
            CheckBits(next, cell.Value);
            CheckBits(next, cell.CompareExchange(initial, initial));
            CheckBits(next, cell.Value);
            CheckBits(next, cell.CompareExchange(initial, next));
            CheckBits(initial, cell.Value);
            Assert.AreEqual(-1, new ReadOnlySpan<byte>(bytes, 8).IndexOfAnyExcept((byte)0xA5));
            Assert.AreEqual(-1, new ReadOnlySpan<byte>(bytes + 16, 8).IndexOfAnyExcept((byte)0xA5));
            Assert.AreEqual(-1, new ReadOnlySpan<byte>(bytes + 8 + sizeof(T), 8 - sizeof(T)).IndexOfAnyExcept((byte)0));
        }
        finally
        {
            NativeMemory.Free(bytes);
        }
    }

    private static void CheckBits<T>(T expected, T actual) where T : unmanaged
        => Assert.AreSequenceEqual(new ReadOnlySpan<byte>(&expected, sizeof(T)).ToArray(), new ReadOnlySpan<byte>(&actual, sizeof(T)).ToArray());

    private static void CheckInteger<T>() where T : unmanaged, IBinaryInteger<T>, IMinMaxValue<T>
    {
        var value = new PgAtomicValue<T>(T.MaxValue);
        Assert.AreEqual(T.MinValue, value.Increment(), typeof(T).Name);
        Assert.AreEqual(T.MinValue, value.Value, typeof(T).Name);
        Assert.AreEqual(T.MaxValue, value.Decrement(), typeof(T).Name);
        value.Exchange(T.CreateChecked(12));
        Assert.AreEqual(T.CreateChecked(42), value.Add(T.CreateChecked(30)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(42), value.Value, typeof(T).Name);
        Assert.AreEqual(T.Zero, value.Subtract(T.CreateChecked(42)), typeof(T).Name);
        Assert.AreEqual(T.Zero, value.Value, typeof(T).Name);
        value.Exchange(T.CreateChecked(0x5A));
        Assert.AreEqual(T.CreateChecked(0x5A), value.And(T.CreateChecked(0x0F)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x0A), value.Value, typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x0A), value.Or(T.CreateChecked(0x30)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x3A), value.Value, typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x3A), value.Xor(T.CreateChecked(0x66)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x5C), value.Value, typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x5C), value.Max(T.CreateChecked(0x10)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x5C), value.Min(T.CreateChecked(0x10)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x10), value.Max(T.CreateChecked(0x20)), typeof(T).Name);
        Assert.AreEqual(T.CreateChecked(0x20), value.Value, typeof(T).Name);
    }

    private sealed class Owner
    {
        internal readonly PgAtomicValue<long> _counter = new(73);
    }

    private enum Marker : ulong
    {
        Initial = 0xFEDCBA9876543210,
        Next = ulong.MaxValue,
    }

    private readonly record struct Pair(int First, int Second);
}
