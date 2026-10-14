namespace Ankus.Examples.MemoryContexts;

/// <summary>
/// Ports pgrx's memory-context basics: create a child context, select it for scoped work, then reset it.
/// </summary>
/// <remarks>
/// Values that must survive a reset belong to the caller's context or to managed code. Ankus checks native
/// allocation lifetimes, so the use-after-reset pattern that pgrx documents as undefined behavior is rejected.
/// </remarks>
public static class Basics
{
    /// <summary>
    /// Sums the present array elements while a temporary scratch context is current.
    /// </summary>
    /// <param name="arr">The integer cells; SQL NULL cells are skipped, as with pgrx's flattened iterator.</param>
    /// <returns>The 64-bit sum, held by managed code rather than the reset context.</returns>
    [PgFunction]
    public static long SumWithScratch(int?[] arr)
    {
        using PgMemoryContext scratch = PgMemoryContext.Create("sum_with_scratch");

        // Native allocations made inside Run belong to the scratch context. The sum is a managed value.
        long total = scratch.Run(() =>
        {
            long sum = 0;
            foreach (int? value in arr)
            {
                if (value is not int present)
                {
                    continue;
                }

                sum = checked(sum + present);
            }

            return sum;
        });

        // Reclaims everything allocated inside the scratch context while keeping the context reusable.
        scratch.Reset();
        return total;
    }

    /// <summary>
    /// Counts a managed list built while the scratch context is current.
    /// </summary>
    /// <param name="n">The number of values to collect; zero or a negative value produces an empty list.</param>
    /// <returns>The number of collected values.</returns>
    [PgFunction]
    public static int ScratchCount(int n)
    {
        using PgMemoryContext scratch = PgMemoryContext.Create("scratch_count");
        int collected = scratch.Run(() =>
        {
            // The managed heap owns this list. Selecting a PostgreSQL context does not move it into native memory.
            List<int> values = new(Math.Max(n, 0));
            for (int value = 0; value < n; value++)
            {
                values.Add(value);
            }

            return values.Count;
        });
        scratch.Reset();
        return collected;
    }

    /// <summary>
    /// Shows that a native allocation made in the scratch context cannot be used after the context resets.
    /// </summary>
    /// <returns>True when the stale allocation is rejected before PostgreSQL memory is accessed.</returns>
    [PgFunction]
    public static bool ResetRejectsStaleAllocation()
    {
        using PgMemoryContext scratch = PgMemoryContext.Create("reset_rejects_stale_allocation");
        using PgAllocation allocation = scratch.Run(() => PgMemoryContext.Current.AllocateZeroed(64));
        allocation.Write<byte>(42);
        scratch.Reset();
        try
        {
            // pgrx documents this write after reset as a use-after-free. Ankus checks the chunk's lifetime first.
            allocation.Write<byte>(0);
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
