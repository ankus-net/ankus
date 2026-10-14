using System.Globalization;
using System.Text;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises PostgreSQL StringInfo bytes, native interoperability, and checked lifetimes.
/// </summary>
public static unsafe class StringInfoFunctions
{
    private static PgStringInfoStream? s_saved;

    /// <summary>
    /// Appends exact binary and Unicode bytes, replaces an interior range, and detects malformed UTF-8.
    /// </summary>
    [PgFunction]
    public static string StringInfoBytes()
    {
        using PgStringInfoStream buffer = PgStringInfoStream.Create([0, 128, 255]);
        buffer.Write("café");
        buffer.Write(new Rune(0x1F418));
        buffer.Write('\0');
        byte[] copy = buffer.ToArray();
        string invalid;
        try
        {
            invalid = buffer.ToString();
        }
        catch (DecoderFallbackException)
        {
            invalid = "strict";
        }

        buffer.WriteAt(1, [17, 34]);
        byte[] interior = new byte[3];
        buffer.CopyTo(interior, 1);
        string decoded = buffer.ToString();
        using PgStringInfoStream malformed = PgStringInfoStream.Create([255]);
        return $"{Convert.ToHexString(copy)}|{Convert.ToHexString(buffer.ToArray())}|{Convert.ToHexString(interior)}|" +
            $"{buffer.Length}|{buffer.DangerousGetDataPointer()[buffer.Length]}|{invalid}|{malformed.ToStringLossy()}|{decoded[0] == '\0' && decoded[^1] == '\0'}";
    }

    /// <summary>
    /// Uses ordinary StreamWriter formatting and proves malformed UTF-16 cannot partially append.
    /// </summary>
    [PgFunction]
    public static string StringInfoWriter()
    {
        using PgStringInfoStream buffer = PgStringInfoStream.Create();
        using (var writer = new StreamWriter(buffer, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"café 🐘:{-42}:{1.25}"));
        }

        string before = buffer.ToString();
        bool rejected = false;
        try
        {
            buffer.Write("prefix\uD800");
        }
        catch (EncoderFallbackException)
        {
            rejected = true;
        }

        return $"{before}|{buffer}|{rejected}|{buffer.CanWrite}";
    }

    /// <summary>
    /// Retains bytes and native owner across growth in another current context, self-append, and reset.
    /// </summary>
    [PgFunction]
    public static string StringInfoGrowth()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("StringInfo owner");
        using PgMemoryContext ambient = PgMemoryContext.Create("StringInfo ambient");
        byte[] initial = new byte[900];
        for (int index = 0; index < initial.Length; index++)
        {
            initial[index] = (byte)(index % 251);
        }

        using PgStringInfoStream buffer = PgStringInfoStream.Create(initial, owner);
        int minimum = buffer.Capacity;
        long address = (long)buffer.DangerousGetPointer();
        _ = Spi.ExecuteScalar<int>("SELECT tests.stringinfo_cursor($1, 23)", SpiParameter.Create(address));
        ambient.Run(() => buffer.DangerousAppend(buffer.DangerousGetDataPointer() + 17, 800));
        byte[] result = buffer.ToArray();
        bool exact = result.AsSpan(0, 900).SequenceEqual(initial) && result.AsSpan(900).SequenceEqual(initial.AsSpan(17, 800));
        int cursor = Spi.ExecuteScalar<int>("SELECT tests.stringinfo_cursor($1, -1)", SpiParameter.Create(address));
        bool sameOwner = buffer.LifetimeContext.Id == owner.Id;
        int grown = buffer.Capacity;
        buffer.Enlarge(4000);
        bool additional = buffer.Capacity >= 5700;
        int retained = buffer.EnsureCapacity(12000);
        buffer.Reset();
        int resetCursor = Spi.ExecuteScalar<int>("SELECT tests.stringinfo_cursor($1, -1)", SpiParameter.Create(address));
        bool reset = buffer.IsEmpty && buffer.Capacity == retained && buffer.DangerousGetDataPointer()[0] == 0;
        buffer.Write("again");
        ambient.Dispose();
        return $"{minimum >= 900}|{grown >= 1700}|{exact}|{cursor}|{sameOwner}|{additional}|{retained >= 12000}|{reset}|{resetCursor}|{buffer}";
    }

    /// <summary>
    /// Returns a borrowed native buffer's bytes and mutates only writable storage before relinquishing the wrapper.
    /// </summary>
    /// <param name="state">The native stack StringInfo supplied by the C fixture.</param>
    /// <param name="mode">Mutable, read-only binary, or read-only empty input.</param>
    [PgFunction]
    public static string StringInfoBorrow(PgInternal state, int mode)
    {
        using PgStringInfoStream buffer = PgStringInfoStream.DangerousBorrow((void*)state.Datum.DangerousGetBits(), PgMemoryContext.Current)!;
        string initial = Convert.ToHexString(buffer.ToArray());
        bool readOnly = buffer.IsReadOnly;
        string result;
        if (mode == 0)
        {
            buffer.WriteAt(1, [17]);
            buffer.WriteByte(42);
            result = Convert.ToHexString(buffer.ToArray());
            buffer.Dispose();
        }
        else if (mode is 3 or 4)
        {
            try
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    buffer.DangerousDetachCString();
                });
                result = "transferred";
            }
            catch (PgException error)
            {
                result = error.SqlState;
            }

            result += ":" + Convert.ToHexString(buffer.ToArray());
            if (mode == 4)
            {
                buffer.Write([]);
                byte* restored = buffer.DangerousDetachCString();
                result += $":{restored[3]}";
            }
        }
        else
        {
            try
            {
                buffer.WriteByte(42);
                result = "mutated";
            }
            catch (NotSupportedException)
            {
                result = "read-only";
            }

            byte* data = buffer.DangerousDetachData();
            result += mode == 2 ? $":{data == null}" : $":{data[0]},{data[1]},{data[2]}";
        }

        return $"{initial}|{readOnly}|{result}|{buffer.CanWrite}";
    }

    /// <summary>
    /// Transfers struct or data ownership without freeing the transferred payload, and rejects invalid C strings atomically.
    /// </summary>
    /// <param name="mode">Whole struct, data-only, or C-string transfer.</param>
    [PgFunction]
    public static string StringInfoTransfer(int mode)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("StringInfo transfer");
        using PgStringInfoStream buffer = PgStringInfoStream.Create([97, 0, 98], owner);
        string rejection = "none";
        byte* data;
        if (mode == 0)
        {
            void* pointer = buffer.DangerousDetach();
            using PgStringInfoStream borrowed = PgStringInfoStream.DangerousBorrow(pointer, owner)!;
            borrowed.WriteByte(99);
            data = borrowed.DangerousDetachData();
        }
        else if (mode == 1)
        {
            data = buffer.DangerousDetachData();
        }
        else
        {
            try
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    buffer.DangerousDetachCString();
                });
            }
            catch (PgException error)
            {
                rejection = error.SqlState;
            }

            buffer.WriteAt(1, [120]);
            data = buffer.DangerousDetachCString();
        }

        buffer.Dispose();
        string bytes = Convert.ToHexString(new ReadOnlySpan<byte>(data, mode == 0 ? 5 : 4));
        using PgAllocation adopted = owner.DangerousAdopt(data, mode == 0 ? 5U : 4U);
        bool nativeOwner = adopted.Context.Id == owner.Id;
        return $"{rejection}|{bytes}|{nativeOwner}|{buffer.CanWrite}";
    }

    /// <summary>
    /// Rejects oversized growth and byte ranges while retaining bytes, native context, and managed finally execution.
    /// </summary>
    [PgFunction]
    public static string StringInfoErrors()
    {
        using PgStringInfoStream buffer = PgStringInfoStream.Create("ok");
        PgMemoryContext caller = PgMemoryContext.Current;
        var states = new List<string>();
        string? detail = null;
        int finalized = 0;
        foreach (Action operation in new Action[]
        {
            () => buffer.Enlarge(int.MaxValue),
            () => buffer.CopyTo(new byte[1], 2),
            () => buffer.WriteAt(1, [1, 2]),
            () => buffer.CopyTo([], 3),
            () => PgStringInfoStream.Create(int.MaxValue),
        })
        {
            try
            {
                PgTransaction.RunInSubtransaction(operation);
            }
            catch (PgException error)
            {
                states.Add(error.SqlState);
                detail ??= error.Detail;
            }
            finally
            {
                finalized++;
            }
        }

        buffer.WriteByte(33);
        return $"{string.Join(',', states)}|{finalized}|{buffer}|{caller.Id == PgMemoryContext.Current.Id}|{Spi.ExecuteScalar<int>("SELECT 42")}|{detail}";
    }

    /// <summary>
    /// Invalidates owned and borrowed handles before reclaimed pointers can be read, including empty access.
    /// </summary>
    /// <param name="delete">Whether to delete the context instead of retaining it after reset.</param>
    [PgFunction]
    public static string StringInfoLifetime(bool delete)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("StringInfo lifetime");
        using PgStringInfoStream owned = PgStringInfoStream.Create("live", owner);
        using PgStringInfoStream borrowed = PgStringInfoStream.DangerousBorrow(owned.DangerousGetPointer(), owner)!;
        if (delete)
        {
            owner.Dispose();
        }
        else
        {
            owner.Reset();
        }

        int stale = 0;
        foreach (PgStringInfoStream buffer in new[] { owned, borrowed })
        {
            try
            {
                buffer.CopyTo([]);
            }
            catch (ObjectDisposedException)
            {
                stale++;
            }
        }

        owned.Dispose();
        borrowed.Dispose();
        string replacement = "deleted";
        if (!delete)
        {
            using PgStringInfoStream fresh = PgStringInfoStream.Create("fresh", owner);
            replacement = fresh.ToString();
        }

        return $"{stale}|{replacement}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Retains a buffer under a transaction or subtransaction owner for later callbacks.
    /// </summary>
    /// <param name="subtransaction">Whether to select the current subtransaction owner.</param>
    [PgFunction]
    public static string StringInfoSave(bool subtransaction)
    {
        s_saved?.Dispose();
        PgMemoryContext owner = PgMemoryContext.Get(subtransaction ? PgMemoryContextKind.CurTransaction : PgMemoryContextKind.TopTransaction)
            ?? throw new InvalidOperationException("No transaction context.");
        s_saved = PgStringInfoStream.Create("saved", owner);
        return s_saved.ToString();
    }

    /// <summary>
    /// Reads or releases a retained buffer after the original callback or native transaction lifetime ends.
    /// </summary>
    /// <param name="release">Whether to release the remaining owned handle.</param>
    [PgFunction]
    public static string StringInfoSaved(bool release)
    {
        PgStringInfoStream buffer = s_saved ?? throw new InvalidOperationException("No saved StringInfo.");
        string result;
        try
        {
            buffer.WriteByte(33);
            result = buffer.ToString();
        }
        catch (ObjectDisposedException)
        {
            result = "stale";
        }

        if (release)
        {
            buffer.Dispose();
            s_saved = null;
        }

        return result;
    }
}
