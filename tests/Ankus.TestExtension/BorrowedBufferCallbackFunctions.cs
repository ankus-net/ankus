using System.Globalization;

namespace Ankus.TestExtension;

public static partial class BorrowedBufferFunctions
{
    private static PgByteaView? s_savedBytes;
    private static PgTextView? s_savedText;
    private static PgDatum? s_savedDatum;
    private static IEnumerator<byte>? s_savedCursor;

    /// <summary>
    /// Captures borrowed aliases so a later callback can verify cleanup after success or failure.
    /// </summary>
    /// <param name="binary">The directly borrowed binary input.</param>
    /// <param name="text">The directly borrowed text input.</param>
    /// <param name="fail">Whether to fail after storing the aliases.</param>
    /// <returns>Exact contents before callback expiry.</returns>
    [PgFunction]
    public static string BufferSave(PgByteaView binary, PgTextView text, bool fail)
    {
        s_savedBytes = binary;
        s_savedText = text;
        s_savedDatum = text.Datum;
        s_savedCursor = binary.GetEnumerator();
        _ = s_savedCursor.MoveNext();
        if (fail)
        {
            throw new InvalidOperationException("Borrowed buffer callback failed.");
        }

        return Convert.ToHexString(binary.DangerousGetSpan()) + ":" + text;
    }

    /// <summary>
    /// Checks every saved alias and the independent native private-context inventory.
    /// </summary>
    /// <returns>Exact expiry failures followed by the remaining private context count.</returns>
    [PgFunction]
    public static string[] BufferExpired()
        => [Failure(() => _ = s_savedBytes![0]), Failure(() => s_savedText!.ToString()),
            Failure(() => s_savedDatum!.DangerousGetBits()), Failure(() => s_savedCursor!.MoveNext()),
            Failure(() => _ = s_savedCursor!.Current), OwnerCount().ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// A nested callback expires its own views while the original scalar arguments remain readable.
    /// </summary>
    /// <param name="binary">The enclosing binary input.</param>
    /// <param name="text">The enclosing text input.</param>
    /// <returns>Nested expiry and surviving outer values.</returns>
    [PgFunction]
    public static string[] BufferNested(PgByteaView binary, PgTextView text)
    {
        string nested = Spi.ExecuteScalar<string>("SELECT borrowed_buffers.buffer_save('\\x7f'::bytea,'nested',false)");
        return [nested, Failure(() => _ = s_savedBytes![0]), Failure(() => s_savedText!.ToString()),
            Convert.ToHexString(binary.DangerousGetSpan()), text.ToString(), OwnerCount().ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Observes private view cleanup when a later column cannot convert to its requested managed type.
    /// </summary>
    /// <returns>The exact failure, remaining private context count and same-session recovery value.</returns>
    [PgFunction]
    public static string[] BufferConversionFailure()
    {
        string error = Failure(() => Spi.ExecuteScalars<PgTextView, PgByteaView, int>(
            "SELECT 'owned'::text,'\\x00ff'::bytea,'not an integer'::text"));
        return [error, OwnerCount().ToString(CultureInfo.InvariantCulture),
            Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Invalidates original storage or its views, then proves copies remain independent.
    /// </summary>
    /// <param name="mode">Zero resets only the source, one resets its children too, two deletes it, and three disposes the original views.</param>
    /// <returns>Exact expiry observations, native child counts and surviving copied values.</returns>
    [PgFunction]
    public static string[] BufferSourceReset(int mode)
    {
        using PgMemoryContext source = PgMemoryContext.Create("borrowed buffer source");
        using PgMemoryContext destination = PgMemoryContext.Create("borrowed buffer destination");
        using SpiRawResult raw = Spi.QueryRaw("SELECT 'café'::text,'\\x007fff'::bytea");
        using var firstText = new PgTextView(raw[0][0].CopyTo(source));
        using var firstBytes = new PgByteaView(raw[0][1].CopyTo(source));
        using var text = new PgTextView(firstText.Datum);
        using var binary = new PgByteaView(firstBytes.Datum);
        using IEnumerator<byte> cursor = binary.GetEnumerator();
        _ = cursor.MoveNext();
        PgDatum escaped = text.Datum;
        PgDatum independent = binary.Datum.CopyTo(destination);
        string copied = text.ToString();
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
                firstText.Dispose();
                firstBytes.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return [before.ToString(CultureInfo.InvariantCulture), OwnerCount().ToString(CultureInfo.InvariantCulture),
            Failure(() => firstText.ToString()), Failure(() => _ = firstBytes[0]), Failure(() => text.ToString()),
            Failure(() => _ = binary[0]), Failure(() => escaped.DangerousGetBits()),
            Failure(() => cursor.MoveNext()), Failure(() => _ = cursor.Current), copied,
            Convert.ToHexString(independent.Read<byte[]>()), text.Utf8Length.ToString(CultureInfo.InvariantCulture),
            binary.Count.ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Counts native buffer contexts without using the managed view registry as the oracle.
    /// </summary>
    private static long OwnerCount()
        => Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'Ankus borrowed buffer'");

    /// <summary>
    /// Returns the exact rejection type, preserving native diagnostic identity when present.
    /// </summary>
    private static string Failure(Action action)
    {
        try
        {
            action();
            return "no error";
        }
        catch (PgException exception)
        {
            return exception.SqlState + ":" + exception.Message;
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidCastException)
        {
            return exception.GetType().Name;
        }
    }

    /// <summary>
    /// Retains the first present text snapshot across aggregate transition and final callbacks.
    /// </summary>
    [PgAggregate(Name = "text_first")]
    public sealed class FirstText : IPgAggregate<PgAggregateState<PgTextView>?, PgTextView?>,
        IPgFinalizingAggregate<PgAggregateState<PgTextView>?, ValueTuple, string?>
    {
        /// <summary>
        /// Keeps the first present input and releases all later unused private views.
        /// </summary>
        /// <param name="context">The aggregate invocation and owner.</param>
        /// <param name="state">The prior retained input.</param>
        /// <param name="value">The next independently owned snapshot.</param>
        /// <returns>The first present value or no state.</returns>
        public static PgAggregateState<PgTextView>? Transition(PgAggregateContext context, PgAggregateState<PgTextView>? state, PgTextView? value)
        {
            if (state is null && value is not null)
            {
                return new(value);
            }

            value?.Dispose();
            return state;
        }

        /// <summary>
        /// Reads native text after the transition callbacks that supplied it have ended.
        /// </summary>
        /// <param name="context">The aggregate invocation and owner.</param>
        /// <param name="state">The first value or no present input.</param>
        /// <param name="arguments">The empty direct argument group.</param>
        /// <returns>The copied text or SQL NULL.</returns>
        public static string? Final(PgAggregateContext context, PgAggregateState<PgTextView>? state, ValueTuple arguments) => state?.Value.ToString();
    }

    /// <summary>
    /// Retains the first present binary snapshot across aggregate transition and final callbacks.
    /// </summary>
    [PgAggregate(Name = "bytea_first")]
    public sealed class FirstBytea : IPgAggregate<PgAggregateState<PgByteaView>?, PgByteaView?>,
        IPgFinalizingAggregate<PgAggregateState<PgByteaView>?, ValueTuple, byte[]?>
    {
        /// <summary>
        /// Keeps the first present input and releases all later unused private views.
        /// </summary>
        /// <param name="context">The aggregate invocation and owner.</param>
        /// <param name="state">The prior retained input.</param>
        /// <param name="value">The next independently owned snapshot.</param>
        /// <returns>The first present value or no state.</returns>
        public static PgAggregateState<PgByteaView>? Transition(PgAggregateContext context, PgAggregateState<PgByteaView>? state, PgByteaView? value)
        {
            if (state is null && value is not null)
            {
                return new(value);
            }

            value?.Dispose();
            return state;
        }

        /// <summary>
        /// Copies native bytes after the transition callbacks that supplied them have ended.
        /// </summary>
        /// <param name="context">The aggregate invocation and owner.</param>
        /// <param name="state">The first value or no present input.</param>
        /// <param name="arguments">The empty direct argument group.</param>
        /// <returns>The exact bytes or SQL NULL.</returns>
        public static byte[]? Final(PgAggregateContext context, PgAggregateState<PgByteaView>? state, ValueTuple arguments)
            => state is null ? null : state.Value.DangerousGetSpan().ToArray();
    }
}
