namespace Ankus.TestExtension;

public static partial class NativeRawCallFunctions
{
    [ThreadStatic]
    private static int s_errorObservations;

    /// <summary>
    /// Attempts to swallow an unrecovered native failure through ordinary catch blocks and nested SPI sessions.
    /// </summary>
    /// <param name="body">The generated native function caller.</param>
    /// <param name="functionOid">The native error fixture's function identity.</param>
    /// <param name="mode">Zero for a direct call, one or two for nested sessions, or three for explicit recovery.</param>
    /// <param name="replace">Whether to replace the failure after catching it.</param>
    /// <returns>A result that is allowed only after explicit native rollback.</returns>
    [PgFunction]
    public static int RawCallCaught(long body, uint functionOid, int mode, bool replace)
    {
        int Fail()
        {
            nuint result = 0;
            return CatchUnrecovered(() => InvokeFunction((nint)body, functionOid, 0, ref result), replace);
        }

        return mode switch
        {
            0 => Fail(),
            1 => Spi.Connect(_ => Fail()),
            2 => Spi.Connect(_ => Spi.Connect(_ => Fail())),
            3 => RecoverUnrecovered(Fail),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>
    /// Attempts to swallow a native memory error, with or without an explicit recovery transaction.
    /// </summary>
    /// <param name="recover">Whether an explicit subtransaction owns the failing work.</param>
    /// <returns>A result that is allowed only after native rollback.</returns>
    [PgFunction]
    public static int MemoryErrorCaught(bool recover)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("unrecovered memory test");
        int Fail() => CatchUnrecovered(() =>
        {
            using PgAllocation allocation = owner.Allocate(0x40000000);
        }, false);

        return recover ? RecoverUnrecovered(Fail) : Fail();
    }

    /// <summary>
    /// Reads and clears observations retained by managed cleanup in the same PostgreSQL session.
    /// </summary>
    /// <returns>Original error, blocked later SQL, finally execution and explicit rollback observations.</returns>
    [PgFunction]
    public static int UnrecoveredErrorObservations()
    {
        int result = s_errorObservations;
        s_errorObservations = 0;
        return result;
    }

    private static int CatchUnrecovered(Action action, bool replace)
    {
        try
        {
            action();
        }
        catch (PgException original)
        {
            s_errorObservations |= 1;
            try
            {
                Spi.Execute("SELECT 1");
            }
            catch (PgException pending) when (pending.SqlState == original.SqlState && pending.Message == original.Message &&
                pending.Detail == original.Detail && pending.Hint == original.Hint)
            {
                s_errorObservations |= 2;
            }

            if (replace)
            {
                throw new InvalidOperationException("replacement must not hide unrecovered native failure");
            }
        }
        finally
        {
            s_errorObservations |= 4;
        }

        return 42;
    }

    private static int RecoverUnrecovered(Func<int> action)
    {
        try
        {
            return PgTransaction.RunInSubtransaction(action);
        }
        catch (PgException)
        {
            int result = Spi.ExecuteScalar<int>("SELECT 42");
            s_errorObservations |= 8;
            return result;
        }
    }
}
