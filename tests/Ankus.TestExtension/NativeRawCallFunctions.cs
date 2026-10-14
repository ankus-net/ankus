using System.Globalization;
using System.Text;

namespace Ankus.TestExtension;

/// <summary>
/// Executes actual generated native bodies through a published Native AOT callback and its native error guard.
/// </summary>
public static unsafe partial class NativeRawCallFunctions
{
    [ThreadStatic]
    private static nint s_parseBody;

    /// <summary>
    /// Preserves every bit of a native PostgreSQL aggregate result and executes a zero-argument void call.
    /// </summary>
    /// <param name="body">The generated FullTransactionId body address.</param>
    /// <param name="voidBody">The generated ProcessInterrupts body address.</param>
    /// <param name="hex">The input's exact unsigned hexadecimal representation.</param>
    /// <returns>The aggregate result's exact unsigned hexadecimal representation.</returns>
    [PgFunction]
    public static string RawCallAggregate(long body, long voidBody, string hex)
    {
        ulong value = ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ulong result = 0;
        NativeRawCall.Invoke((nint)body, [new((nint)(&value), sizeof(ulong))], (nint)(&result), sizeof(ulong));
        NativeRawCall.Invoke((nint)voidBody, [], 0, 0);
        return result.ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rejects malformed native storage before changing the result and then executes a valid call.
    /// </summary>
    /// <param name="body">The generated integer-parser body.</param>
    /// <param name="mode">The malformed frame partition.</param>
    /// <returns>The exact contract error, original result and recovered value.</returns>
    [PgFunction]
    public static string RawCallRejectedStorage(long body, int mode)
    {
        ReadOnlySpan<byte> text = "42\0"u8;
        int result = 12345;
        string? failure = null;
        fixed (byte* encoded = text)
        {
            nint address = (nint)encoded;
            Span<NativeCallArgument> arguments = [new((nint)(&address), (nuint)sizeof(nint)), new((nint)(&address), (nuint)sizeof(nint))];
            int count = 1;
            nint destination = (nint)(&result);
            nuint size = sizeof(int);
            switch (mode)
            {
                case 0:
                    count = 0;
                    break;
                case 1:
                    count = 2;
                    break;
                case 2:
                    arguments[0] = new(0, (nuint)sizeof(nint));
                    break;
                case 3:
                    arguments[0] = new((nint)(&address), (nuint)sizeof(nint) - 1);
                    break;
                case 4:
                    arguments[0] = new((nint)((byte*)&address + 1), (nuint)sizeof(nint));
                    break;
                case 5:
                    destination = 0;
                    break;
                case 6:
                    size--;
                    break;
                case 7:
                    size++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }

            try
            {
                NativeRawCall.Invoke((nint)body, arguments[..count], destination, size);
            }
            catch (InvalidOperationException exception)
            {
                failure = exception.Message;
            }
        }

        if (failure is null)
        {
            throw new InvalidOperationException("The malformed native frame unexpectedly succeeded.");
        }

        return $"{failure}|{result}|{Parse((nint)body, "42")}";
    }

    /// <summary>
    /// Recovers from native or nested managed errors with owned diagnostics and restored native state.
    /// </summary>
    /// <param name="body">The generated OidFunctionCall1Coll body.</param>
    /// <param name="functionOid">The native or managed callback's PostgreSQL function OID.</param>
    /// <param name="parseBody">The generated parser used inside a nested managed callback.</param>
    /// <returns>Exact diagnostics, untouched failed result, state restoration and successful retry.</returns>
    [PgFunction]
    public static string RawCallErrors(long body, uint functionOid, long parseBody)
    {
        nint previous = s_parseBody;
        s_parseBody = (nint)parseBody;
        try
        {
            PgMemoryContext owner = PgMemoryContext.Current;
            long holdoffs = Spi.ExecuteScalar<long>("SELECT tests.raw_call_holdoffs()");
            nuint result = 12345;
            PgException? failure = null;
            try
            {
                PgTransaction.RunInSubtransaction(() => InvokeFunction((nint)body, functionOid, 0, ref result));
            }
            catch (PgException exception)
            {
                failure = exception;
            }

            if (failure is null)
            {
                throw new InvalidOperationException("The deliberate native error did not occur.");
            }

            bool untouched = result == 12345;
            bool restored = owner.Id == PgMemoryContext.Current.Id;
            bool interrupts = holdoffs == Spi.ExecuteScalar<long>("SELECT tests.raw_call_holdoffs()");
            InvokeFunction((nint)body, functionOid, 25, ref result);
            return $"{failure.SqlState}|{failure.Message}|{failure.Detail}|{failure.Hint}|{untouched}|{restored}|{interrupts}|{result}";
        }
        finally
        {
            s_parseBody = previous;
        }
    }

    /// <summary>
    /// Reenters the raw native guard before either returning a value or raising a managed PostgreSQL exception.
    /// </summary>
    /// <param name="value">Zero selects the deliberate managed failure.</param>
    /// <returns>The parsed value plus a fixed independent offset.</returns>
    [PgFunction]
    public static int RawCallNestedTarget(int value)
    {
        int parsed = Parse(s_parseBody, value.ToString(CultureInfo.InvariantCulture));
        if (value == 0)
        {
            throw new PgException("22023", "nested raw failure", "owned managed detail", "retry managed callback");
        }

        return parsed + 17;
    }

    /// <summary>
    /// Proves worker threads cannot inherit the native capability and leaves the backend thread usable.
    /// </summary>
    /// <param name="body">The generated parser address.</param>
    /// <returns>The worker rejection and exact subsequent backend result.</returns>
    [PgFunction]
    public static string RawCallWorker(long body)
    {
        try
        {
            // A long-running task gets a thread of its own, which has no backend callback.
            Task.Factory.StartNew(() => Parse((nint)body, "42"), CancellationToken.None, TaskCreationOptions.LongRunning,
                TaskScheduler.Default).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException rejected) when (rejected.Message.Contains("active backend callback", StringComparison.Ordinal))
        {
            return $"True|{Parse((nint)body, "42")}";
        }

        throw new InvalidOperationException("The worker did not reject its missing native capability.");
    }

    /// <summary>
    /// Retains intentional native interrupt-state changes after successful calls and restores them explicitly.
    /// </summary>
    /// <param name="body">The generated OidFunctionCall1Coll body.</param>
    /// <param name="functionOid">The native interrupt-control fixture's OID.</param>
    /// <returns>The incremented, retained and explicitly restored native state.</returns>
    [PgFunction]
    public static string RawCallHoldoffEffects(long body, uint functionOid)
    {
        nuint before = 0;
        InvokeFunction((nint)body, functionOid, 0, ref before);
        nuint held = 0;
        nuint retained = 0;
        nuint resumed = 0;
        try
        {
            InvokeFunction((nint)body, functionOid, 1, ref held);
            InvokeFunction((nint)body, functionOid, 0, ref retained);
        }
        finally
        {
            InvokeFunction((nint)body, functionOid, -1, ref resumed);
        }

        return $"{held == before + 0x100000001UL}|{retained == held}|{resumed == before}";
    }

    /// <summary>
    /// Retains a successful native context switch until the caller explicitly restores the original context.
    /// </summary>
    /// <param name="body">The generated OidFunctionCall1Coll body.</param>
    /// <param name="functionOid">The native state-control fixture's OID.</param>
    /// <returns>The observed switched context and exact restoration of the original managed context identity.</returns>
    [PgFunction]
    public static string RawCallContextEffects(long body, uint functionOid)
    {
        PgMemoryContext before = PgMemoryContext.Current;
        nuint result = 0;
        bool switched;
        try
        {
            InvokeFunction((nint)body, functionOid, 2, ref result);
            switched = PgMemoryContext.Current.Id == PgMemoryContext.Get(PgMemoryContextKind.Top)!.Id;
        }
        finally
        {
            InvokeFunction((nint)body, functionOid, -2, ref result);
        }

        return $"{switched}|{PgMemoryContext.Current.Id == before.Id}";
    }

    private static int Parse(nint body, string value)
    {
        byte[] text = Encoding.UTF8.GetBytes(value + "\0");
        int result = 0;
        fixed (byte* encoded = text)
        {
            nint address = (nint)encoded;
            NativeRawCall.Invoke(body, [new((nint)(&address), (nuint)sizeof(nint))], (nint)(&result), sizeof(int));
        }

        return result;
    }

    private static void InvokeFunction(nint body, uint functionOid, int value, ref nuint result)
    {
        uint collation = 0;
        nuint argument = unchecked((nuint)(nint)value);
        fixed (nuint* destination = &result)
        {
            NativeRawCall.Invoke(body,
                [new((nint)(&functionOid), sizeof(uint)), new((nint)(&collation), sizeof(uint)), new((nint)(&argument), (nuint)sizeof(nuint))],
                (nint)destination, (nuint)sizeof(nuint));
        }
    }
}
