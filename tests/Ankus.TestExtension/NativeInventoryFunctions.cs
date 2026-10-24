using Ankus.Postgres;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises exported and inline functions added by the expanded upstream header inventory.
/// </summary>
[PgSchema("native_inventory")]
public static class NativeInventoryFunctions
{
    /// <summary>
    /// Reads PostgreSQL's configured prepared-transaction capacity from native global storage.
    /// </summary>
    /// <returns>The configured maximum number of prepared transactions.</returns>
    [PgFunction]
    public static int MaxPreparedTransactions()
    {
        unsafe
        {
            return NativeGlobals.max_prepared_xacts;
        }
    }

    /// <summary>
    /// Passes a C long argument as a <c>long</c>, which compiles on every platform, and returns PostgreSQL's
    /// <c>FETCH_ALL</c>. On Windows, where C long has 32 bits, a larger value is rejected before PostgreSQL is called.
    /// </summary>
    /// <returns><c>FETCH_ALL</c> with the platform's C long width, or -1 when Windows accepts a value it cannot hold.</returns>
    [PgFunction]
    public static long CLongArguments()
    {
        unsafe
        {
            long microseconds = 1;
            NativeMethods.pg_usleep(microseconds);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    NativeMethods.pg_usleep(long.MaxValue);
                    return -1;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // The overload rejects the value before PostgreSQL could sleep for it.
                }
            }

            return NativeMethods.FETCH_ALL();
        }
    }

    /// <summary>
    /// Hashes the exact integer bits with PostgreSQL's native unsigned helper.
    /// </summary>
    /// <param name="value">The integer bit pattern.</param>
    /// <returns>The hash in PostgreSQL's signed SQL representation.</returns>
    [PgFunction]
    public static int Hash(int value)
    {
        unsafe
        {
            return unchecked((int)NativeMethods.hash_bytes_uint32((uint)value));
        }
    }

    /// <summary>
    /// Preserves all bits of the native seed and extended hash result.
    /// </summary>
    /// <param name="value">The integer bit pattern.</param>
    /// <param name="seed">The 64-bit seed.</param>
    /// <returns>The extended hash in PostgreSQL's signed SQL representation.</returns>
    [PgFunction]
    public static long ExtendedHash(int value, long seed)
    {
        unsafe
        {
            return unchecked((long)NativeMethods.hash_bytes_uint32_extended((uint)value, (ulong)seed));
        }
    }

    /// <summary>
    /// Calls the installed headers' inline overflow helper with real writable result storage.
    /// </summary>
    /// <param name="left">The first addend.</param>
    /// <param name="right">The second addend.</param>
    /// <returns>The exact sum, or SQL NULL when the native helper reports overflow.</returns>
    [PgFunction]
    public static unsafe int? Add(int left, int right)
    {
        int result;
        return NativeMethods.pg_add_s32_overflow(left, right, &result) ? null : result;
    }
}
