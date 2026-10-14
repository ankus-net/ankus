using System.Globalization;
using Ankus.Postgres;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises successful SQL scopes and nonrecoverable errors inside actual parallel workers.
/// </summary>
public static unsafe class ParallelRecoveryFunctions
{
    private static readonly Dictionary<int, string[]> s_sessions = [];
    private static readonly HashSet<string> s_completed = [];

    /// <summary>
    /// Executes nested sessions and a retained prepared cursor once per worker and input partition.
    /// </summary>
    /// <param name="value">The table-derived input partition.</param>
    /// <returns>The worker identity and independently computed native SQL values.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe, Volatility = PgVolatility.Stable)]
    public static string[] ParallelSpiSession(int value)
    {
        if (s_sessions.TryGetValue(value, out string[]? existing))
        {
            return existing;
        }

        int nested = 0;
        using SpiPreparedStatement plan = Spi.Connect(session =>
        {
            nested = Spi.Connect(inner => inner.Query("SELECT $1::integer + 10", readOnly: true, limit: 1,
                SpiParameter.Create(value))[0].Get<int>(0));
            return session.Prepare("SELECT $1::integer + 40", typeof(int)).Keep();
        });
        int prepared = plan.Query(readOnly: true, limit: 1, SpiParameter.Create(value))[0].Get<int>(0);
        using SpiCursor cursor = plan.OpenCursor(readOnly: true, SpiParameter.Create(value + 1));
        SpiResult first = cursor.Fetch(1);
        SpiResult end = cursor.Fetch(1);
        string[] result =
        [
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            value.ToString(CultureInfo.InvariantCulture),
            nested.ToString(CultureInfo.InvariantCulture),
            prepared.ToString(CultureInfo.InvariantCulture),
            first[0].Get<int>(0).ToString(CultureInfo.InvariantCulture),
            end.Count.ToString(CultureInfo.InvariantCulture),
        ];
        s_sessions.Add(value, result);
        return result;
    }

    /// <summary>
    /// Catches native failures, probes subsequent access and records managed cleanup before returning or replacing the error.
    /// </summary>
    /// <param name="value">The table row, preventing planner constant folding.</param>
    /// <param name="directory">The isolated test cluster's receipt directory.</param>
    /// <param name="identity">The test-owned receipt identity.</param>
    /// <param name="mode">Division, raw provider lookup, or nested full diagnostic transport.</param>
    /// <param name="replace">Whether managed code throws a replacement after catching the native error.</param>
    /// <returns>The original row only when the server permits operation-level recovery.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe)]
    public static int ParallelSpiFailure(int value, string directory, string identity, int mode, bool replace)
    {
        if (s_completed.Contains(identity))
        {
            return value;
        }

        var observations = new List<string>();
        int cleanupCount = 0;
        try
        {
            try
            {
                // The owner's reset runs and consumes this callback, after the statement and allocation are released.
                using PgMemoryContext owner = PgMemoryContext.Create("parallel recovery cleanup", PgMemoryContext.Get(PgMemoryContextKind.Top));
                _ = owner.RegisterResetCallback(() => cleanupCount++);
                using PgAllocation allocation = owner.Allocate(8);
                using SpiPreparedStatement retained = Spi.Prepare("SELECT 42");
                owner.Run(() =>
                {
                    try
                    {
                        if (mode == 1)
                        {
                            fixed (byte* name = "ankus_missing_parallel_provider\0"u8)
                            {
                                _ = NativeMethods.GetCustomScanMethods((sbyte*)name, false);
                            }
                        }
                        else
                        {
                            Spi.Connect(session => session.Query(mode == 0 ? "SELECT 1 / ($1::integer - $1)" :
                                "SELECT datatype.parallel_detailed_error($1)", readOnly: true, limit: 1,
                                SpiParameter.Create(value)));
                        }

                        throw new InvalidOperationException("The deliberate worker failure did not occur.");
                    }
                    catch (PgException error)
                    {
                        RecordError(observations, error);
                    }
                });

                try
                {
                    observations.Add(Spi.Query("SELECT 42", readOnly: true, limit: 1)[0].Get<int>(0).ToString(CultureInfo.InvariantCulture));
                    observations.AddRange(["<success>", "<success>", "<success>"]);
                }
                catch (PgException error)
                {
                    RecordError(observations, error);
                }

                try
                {
                    observations.Add(NativeMethods.GetCurrentTransactionNestLevel().ToString(CultureInfo.InvariantCulture));
                    observations.AddRange(["<success>", "<success>", "<success>"]);
                }
                catch (PgException error)
                {
                    RecordError(observations, error);
                }

                if (replace)
                {
                    throw new PgException("P7801", "Managed replacement after parallel failure.");
                }

                s_completed.Add(identity);
                return value;
            }
            finally
            {
                observations.Add($"disposed:{cleanupCount}");
            }
        }
        finally
        {
            observations.Add("finally");
            string path = System.IO.Path.Join(directory, $"ankus-parallel-recovery-{identity}-{Environment.ProcessId}.done");
            File.WriteAllLines(path, observations);
        }
    }

    /// <summary>
    /// Supplies a full owned diagnostic through a nested managed callback and its native SQL caller.
    /// </summary>
    /// <param name="value">A positive table row supplied by the worker.</param>
    /// <returns>No value; every valid input deliberately reports the same complete diagnostic.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe)]
    public static int ParallelDetailedError(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        throw new PgException("P7802", new string('x', 4096) + " owned 🐘", "Worker détail", "Retain the original diagnostic.");
    }

    /// <summary>
    /// Contains a failure after entering parallel mode inside a real enclosing recovery subtransaction.
    /// </summary>
    /// <returns>The recovery outcome, restored parallel state and successful subsequent SQL.</returns>
    [PgFunction]
    public static string ParallelScopeRecovery()
    {
        string outcome = "committed";
        try
        {
            PgTransaction.RunInSubtransaction(() =>
            {
                NativeMethods.EnterParallelMode();
                try
                {
                    Spi.Query("SELECT 1 / 0", readOnly: true, limit: 1);
                }
                catch (PgException error) when (error.SqlState == "22012")
                {
                    // Older servers require rollback of the enclosing recovery scope.
                }
                finally
                {
                    try
                    {
                        NativeMethods.ExitParallelMode();
                    }
                    catch (PgException error) when (error.SqlState == "22012")
                    {
                        // Native rollback restores the parallel nesting after managed return.
                    }
                }
            });
        }
        catch (PgException error) when (error.SqlState == "22012")
        {
            outcome = "rolled-back";
        }

        return $"{outcome}|{NativeMethods.IsInParallelMode()}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    private static void RecordError(List<string> observations, PgException error)
    {
        observations.Add(error.SqlState);
        observations.Add(error.Message);
        observations.Add(error.Detail ?? "<null>");
        observations.Add(error.Hint ?? "<null>");
    }
}
