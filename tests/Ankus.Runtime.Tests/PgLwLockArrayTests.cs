using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ankus.Runtime.Tests;

public sealed unsafe partial class PgSharedMemoryTests
{
    /// <summary>
    /// A shared array registers its header, the elements after the next 8-byte boundary, and its length; the
    /// initializer writes the header and every element; and registering again keeps the original length.
    /// </summary>
    [TestMethod]
    public void SharedArraysRegisterTheirLengthAndInitializeEveryElement()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<ArrayHeader, ArraySlot>("slots");
        Assert.AreEqual(0, storage.Length);
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Share());
        PgSharedMemory.Initialize(storage, 3, static () => new ArrayHeader(1, 2, 3), static index => new ArraySlot(index * 10, (short)-index));
        Assert.AreEqual(3, storage.Length);
        Assert.AreEqual((nuint)(8 + (3 * sizeof(ArraySlot))), fixture.Definition._size);
        Assert.AreSequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(
            typeof(ArrayHeader).AssemblyQualifiedName + "[" + typeof(ArraySlot).AssemblyQualifiedName + "]")), fixture.Identity);
        PgSharedMemory.Initialize(storage, 3);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgSharedMemory.Initialize(storage, 4));
        Assert.HasCount(1, fixture.Memory.Requests);

        byte[] bytes = GC.AllocateArray<byte>(8 + (3 * sizeof(ArraySlot)), pinned: true);
        NativeCallError error = default;
        fixed (byte* destination = bytes)
        {
            Assert.AreEqual(0, fixture.Initialize((nint)destination, (nuint)bytes.Length, &error));
        }

        Assert.AreEqual(new ArrayHeader(1, 2, 3), MemoryMarshal.Read<ArrayHeader>(bytes));
        Assert.AreSequenceEqual([new ArraySlot(0, 0), new ArraySlot(10, -1), new ArraySlot(20, -2)],
            MemoryMarshal.Cast<byte, ArraySlot>(bytes.AsSpan(8)).ToArray());
    }

    /// <summary>
    /// Lengths that are negative or do not fit in 2 GB are rejected before PostgreSQL is asked to reserve anything.
    /// </summary>
    [TestMethod]
    public void SharedArraysRejectImpossibleLengths()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<ArrayHeader, ArraySlot>("impossible");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgSharedMemory.Initialize(storage, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgSharedMemory.Initialize(storage, int.MaxValue));
        Assert.IsEmpty(fixture.Memory.Requests);
        Assert.AreEqual(0, storage.Length);
    }

    /// <summary>
    /// Guards copy the header and single elements, pass spans of the whole array to callbacks, write through an
    /// exclusive guard only, and reject an index outside the array before calling PostgreSQL.
    /// </summary>
    [TestMethod]
    public void SharedArrayGuardsCopyHeadersAndElementsAndBoundIndexes()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<ArrayHeader, ArraySlot>("guarded");
        PgSharedMemory.Initialize(storage, 2);
        fixture.Bytes = new byte[8 + (2 * sizeof(ArraySlot))];
        using (PgLwLockExclusiveGuard<ArrayHeader, ArraySlot> writer = storage.Exclusive())
        {
            Assert.AreEqual(2, writer.Length);
            writer.Header = new ArrayHeader(7, 8, 9);
            writer[1] = new ArraySlot(42, 3);
            Assert.AreEqual(new ArraySlot(42, 3), writer[1]);
            Assert.AreEqual(5, writer.Mutate((ref header, slots) =>
            {
                header = header with { First = 5 };
                slots[0] = new ArraySlot(1, 1);
                return header.First;
            }));
            int requests = fixture.Memory.Requests.Count;
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer[2]);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => writer[-1] = default);
            Assert.HasCount(requests, fixture.Memory.Requests);
        }

        using (PgLwLockShareGuard<ArrayHeader, ArraySlot> reader = storage.Share())
        {
            Assert.AreEqual(new ArrayHeader(5, 8, 9), reader.Header);
            Assert.AreEqual(new ArraySlot(1, 1), reader[0]);
            Assert.AreEqual("1:1,42:3", reader.Read(static (in header, slots) =>
            {
                Assert.AreEqual(5, header.First);
                return string.Join(',', slots.ToArray().Select(static slot => slot.Process + ":" + slot.State));
            }));
            reader.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => reader.Header);
        }

        Assert.AreEqual(new ArrayHeader(5, 8, 9), MemoryMarshal.Read<ArrayHeader>(fixture.Bytes));
        Assert.AreEqual(new ArraySlot(42, 3), MemoryMarshal.Read<ArraySlot>(fixture.Bytes.AsSpan(8 + sizeof(ArraySlot))));
        foreach (NativeMemoryRequest request in fixture.Memory.Requests.Where(static request => request._flags is 6 or 7))
        {
            Assert.AreEqual((nuint)fixture.Bytes.Length, request._length);
        }
    }

    /// <summary>
    /// A three-byte header, so the elements start at the next 8-byte boundary rather than right after it.
    /// </summary>
    private readonly record struct ArrayHeader(byte First, byte Second, byte Third);

    /// <summary>
    /// An element with internal padding.
    /// </summary>
    private readonly record struct ArraySlot(int Process, short State);
}
