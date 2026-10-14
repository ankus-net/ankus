namespace Ankus.TestExtension;

/// <summary>
/// Supplies independent managed errors, process witnesses and collection pressure for real custom-scan plans.
/// </summary>
public static class CustomScanProbeFunctions
{
    /// <summary>
    /// Rejects one child row with exact owned diagnostics after earlier rows have executed.
    /// </summary>
    /// <param name="value">The actual scanned value.</param>
    /// <returns>True for every value except the controlled failure sentinel.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe)]
    public static bool CustomScanAccept(int value)
    {
        if (value == 2)
        {
            throw new PgException("P7521", "managed custom scan child failure", "trace child detail", "trace child hint");
        }

        return true;
    }

    /// <summary>
    /// Produces a process witness from a parallel-safe expression evaluated over real input rows.
    /// </summary>
    /// <param name="value">The scanned input establishing the expression's relation dependency.</param>
    /// <returns>The process actually evaluating this expression.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe, Volatility = PgVolatility.Stable)]
    public static int CustomScanProcess(int value)
    {
        _ = value;
        return Environment.ProcessId;
    }

    /// <summary>
    /// Forces collection between executions of a cached custom-scan plan.
    /// </summary>
    [PgFunction]
    public static void CustomScanCollect()
    {
        GarbageCollection.Collect();
    }
}
