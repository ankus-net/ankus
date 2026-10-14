using System.Globalization;

namespace Ankus.TestExtension;

public static partial class CStringFunctions
{
    private static PgCStringView? s_savedView;
    private static PgDatum? s_savedDatum;
    private static IEnumerator<byte>? s_savedCursor;
    private static PgCString? s_savedCopy;

    /// <summary>
    /// Compares direct borrowing against storage captured by an independent native caller.
    /// </summary>
    /// <param name="value">The exact native input, including a possible null address.</param>
    /// <param name="address">The address independently supplied by the native caller.</param>
    /// <returns>Whether nullable conversion or exact storage and payload identity is preserved.</returns>
    [PgFunction]
    public static unsafe bool BorrowedStorage(PgCStringView? value, long address)
    {
        if (address == 0)
        {
            return value is null;
        }

        if (value is null)
        {
            return false;
        }

        fixed (byte* bytes = value.DangerousGetNullTerminatedSpan())
        {
            return unchecked((long)bytes) == address && value.Datum.DangerousGetBits() == unchecked((nuint)address)
                && value.DangerousGetNullTerminatedSpan().SequenceEqual<byte>([1, 128, 255, 0]);
        }
    }

    /// <summary>
    /// Verifies independent owned conversion of exact bytes or PostgreSQL's null C-string address.
    /// </summary>
    /// <param name="value">The copied input or null address.</param>
    /// <param name="address">The native caller's original address.</param>
    /// <returns>Whether the native input was converted without encoding loss.</returns>
    [PgFunction]
    public static bool OwnedStorage(PgCString? value, long address)
        => address == 0 ? value is null : value is not null && value.AsNullTerminatedSpan().SequenceEqual<byte>([1, 128, 255, 0]);

    /// <summary>
    /// Copies raw null-address storage while retaining its independent SQL NULL flag.
    /// </summary>
    /// <param name="value">The raw C-string argument.</param>
    /// <param name="address">The address before managed conversion.</param>
    /// <returns>Whether copying and typed reads preserve both contracts.</returns>
    [PgFunction]
    public static bool RawStorage([PgSqlType("cstring", Schema = "pg_catalog")] PgDatum value, long address)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("C-string raw copy");
        PgDatum copy = value.CopyTo(owner);
        using PgCStringView? view = copy.Read<PgCStringView>();
        PgCString? owned = copy.Read<PgCString>();
        return !value.IsNull && !copy.IsNull && copy.TypeOid == 2275
            && (address == 0 ? copy.DangerousGetBits() == 0 && view is null && owned is null
                : view is not null && owned is not null && view.DangerousGetSpan().SequenceEqual<byte>([1, 128, 255])
                    && owned.AsSpan().SequenceEqual<byte>([1, 128, 255]));
    }

    /// <summary>
    /// Saves borrowed aliases and an owned copy before successful return or explicit UTF-8 rejection.
    /// </summary>
    /// <param name="value">The borrowed bytes.</param>
    /// <param name="decode">Whether to request strict UTF-8 decoding after saving aliases.</param>
    /// <returns>Decoded text or exact hexadecimal bytes.</returns>
    [PgFunction]
    public static string SaveView(PgCStringView value, bool decode)
    {
        s_savedView = value;
        s_savedDatum = value.Datum;
        s_savedCursor = value.GetEnumerator();
        _ = s_savedCursor.MoveNext();
        s_savedCopy = value.ToOwned();
        return decode ? value.ToUtf8String() : Convert.ToHexString(value.DangerousGetSpan());
    }

    /// <summary>
    /// Reads every saved alias from a later callback and compares native cleanup with the surviving copy.
    /// </summary>
    /// <returns>Exact failures, remaining owner count and independent bytes.</returns>
    [PgFunction]
    public static string[] ExpiredView()
        => [Failure(() => _ = s_savedView![0]), Failure(() => s_savedView!.ToUtf8String()),
            Failure(() => s_savedDatum!.DangerousGetBits()), Failure(() => s_savedCursor!.MoveNext()),
            Failure(() => _ = s_savedCursor!.Current), OwnerCount().ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(s_savedCopy!.AsSpan())];

    /// <summary>
    /// Invalidates original storage and observes checked aliases and independently copied bytes.
    /// </summary>
    /// <param name="mode">Zero resets only the source, one resets descendants, two deletes it, and three disposes the first view.</param>
    /// <returns>Owner counts, expiry failures and independent payloads.</returns>
    [PgFunction]
    public static string[] SourceReset(int mode)
    {
        using PgMemoryContext source = PgMemoryContext.Create("C-string source");
        using PgMemoryContext destination = PgMemoryContext.Create("C-string destination");
        using SpiRawResult raw = Spi.QueryRaw("SELECT cstrings.create_cstring('\\x80ff'::bytea)");
        using var first = new PgCStringView(raw[0][0].CopyTo(source));
        using var nested = new PgCStringView(first.Datum);
        using IEnumerator<byte> cursor = nested.GetEnumerator();
        _ = cursor.MoveNext();
        PgDatum escaped = nested.Datum;
        PgDatum independent = escaped.CopyTo(destination);
        PgCString copy = nested.ToOwned();
        long before = OwnerCount();
        switch (mode)
        {
            case 0:
                source.ResetOnly();
                break;
            case 1:
                source.Reset();
                break;
            case 2:
                source.Dispose();
                break;
            case 3:
                first.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return [before.ToString(CultureInfo.InvariantCulture), OwnerCount().ToString(CultureInfo.InvariantCulture),
            Failure(() => _ = first[0]), Failure(() => _ = nested[0]), Failure(() => escaped.DangerousGetBits()),
            Failure(() => cursor.MoveNext()), Failure(() => _ = cursor.Current), Convert.ToHexString(copy.AsSpan()),
            Convert.ToHexString(independent.Read<PgCString>().AsSpan()), nested.Count.ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Counts actual native private contexts independently of managed view registrations.
    /// </summary>
    private static long OwnerCount()
        => Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'Ankus borrowed buffer'");

    /// <summary>
    /// Reports exact rejection types without converting a missing rejection into success.
    /// </summary>
    private static string Failure(Action action)
    {
        try
        {
            action();
            return "no error";
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidCastException)
        {
            return exception.GetType().Name;
        }
    }

    /// <summary>
    /// Retains the first non-NULL C-string snapshot across aggregate callbacks.
    /// </summary>
    [PgAggregate(Name = "first_bytes")]
    public sealed class FirstBytes : IPgAggregate<PgAggregateState<PgCStringView>?, PgCStringView?>,
        IPgFinalizingAggregate<PgAggregateState<PgCStringView>?, ValueTuple, byte[]?>
    {
        /// <summary>
        /// Keeps the first native snapshot and releases later unused views.
        /// </summary>
        /// <param name="context">The aggregate invocation and owner.</param>
        /// <param name="state">The retained first value.</param>
        /// <param name="value">The next input snapshot.</param>
        /// <returns>The first present value or no state.</returns>
        public static PgAggregateState<PgCStringView>? Transition(PgAggregateContext context, PgAggregateState<PgCStringView>? state, PgCStringView? value)
        {
            if (state is null && value is not null)
            {
                return new(value);
            }

            value?.Dispose();
            return state;
        }

        /// <summary>
        /// Observes retained bytes after their transition callback has returned.
        /// </summary>
        /// <param name="context">The aggregate invocation and owner.</param>
        /// <param name="state">The first value or no state.</param>
        /// <param name="arguments">The empty direct argument group.</param>
        /// <returns>The exact payload or SQL NULL.</returns>
        public static byte[]? Final(PgAggregateContext context, PgAggregateState<PgCStringView>? state, ValueTuple arguments) => state?.Value.ToArray();
    }
}
