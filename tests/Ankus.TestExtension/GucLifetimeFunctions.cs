using System.Globalization;
using System.Runtime.InteropServices;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises copied configuration payloads without retaining growing managed histories.
/// </summary>
public static partial class GucLifetimeFunctions
{
    private const int PayloadLength = 65536;
    private static readonly WeakReference?[] s_observed = new WeakReference?[8];
    private static readonly long[] s_calls = new long[4];

    /// <summary>
    /// Gets the requested bounded failure or logging mode.
    /// </summary>
    [PgGucInt("ankus_lifetime.mode", 0, "Lifetime probe mode")]
    public static partial int Mode { get; }

    /// <summary>
    /// Gets the large native string whose hooks copy equally large flat extra data.
    /// </summary>
    [PgGucString("ankus_lifetime.text", "a", "Lifetime probe text",
        Check = nameof(Check), Assign = nameof(Assign), Show = nameof(Show))]
    public static partial string Text { get; }

    /// <summary>
    /// Records an independently materialized typed read and returns its bounded value summary.
    /// </summary>
    /// <returns>The length and first and last characters of the current native string.</returns>
    [PgFunction]
    public static string GucLifetimeValue()
    {
        string value = Text;
        Observe(7, value);
        return string.Create(CultureInfo.InvariantCulture, $"{value.Length}:{value[0]}:{value[^1]}");
    }

    /// <summary>
    /// Collects objects after earlier hook frames have returned and reports observed and surviving slots.
    /// </summary>
    /// <returns>The observed-slot mask and surviving-object mask.</returns>
    [PgFunction]
    public static int[] GucLifetimeCollect()
    {
        GarbageCollection.Collect();
        int observed = 0;
        int alive = 0;
        for (int index = 0; index < s_observed.Length; index++)
        {
            if (s_observed[index] is WeakReference reference)
            {
                observed |= 1 << index;
                if (reference.IsAlive)
                {
                    alive |= 1 << index;
                }
            }
        }

        return [observed, alive];
    }

    /// <summary>
    /// Returns bounded counts for check, assignment, display, and validation-only check execution.
    /// </summary>
    /// <returns>An independent counter snapshot.</returns>
    [PgFunction]
    public static long[] GucLifetimeCalls() => [.. s_calls];

    /// <summary>
    /// Measures allocated glibc bytes, including allocations serviced through mmap.
    /// </summary>
    /// <returns>The sum of allocated arena bytes and mapped allocation bytes.</returns>
    [PgFunction]
    public static long GucLifetimeMallocBytes()
    {
        MallocInfo info = ReadMallocInfo();
        return checked((long)(info.AllocatedBytes + info.MappedBytes));
    }

    /// <summary>
    /// Confirms that the allocator metric observes a real retained allocation and its release.
    /// </summary>
    /// <param name="bytes">The size of the positive-control native allocation.</param>
    /// <returns>The allocated-byte measurements before allocation, while retained, and after release.</returns>
    [PgFunction]
    public static unsafe long[] GucLifetimeMallocWitness(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        long before = GucLifetimeMallocBytes();
        void* allocation = NativeMemory.Alloc((nuint)bytes);
        long retained;
        try
        {
            retained = GucLifetimeMallocBytes();
        }
        finally
        {
            NativeMemory.Free(allocation);
        }

        return [before, retained, GucLifetimeMallocBytes()];
    }

    /// <summary>
    /// Builds fresh copied strings and extras or requests a selected diagnostic failure.
    /// </summary>
    /// <param name="value">The proposed text.</param>
    /// <param name="source">The native configuration source.</param>
    /// <returns>An accepted normalized value and extra, or an owned rejection.</returns>
    internal static PgGucCheckResult<string> Check(string value, PgGucSource source)
    {
        s_calls[0]++;
        if (source == PgGucSource.Test)
        {
            s_calls[3]++;
        }

        int mode = Mode;
        if (mode is 1 or 5)
        {
            return new PgGucCheckResult<string>(new PgGucCheckError(
                mode == 5 ? "Lifetime rejected 🐘" : "Lifetime rejected.", new string('x', PayloadLength), "Use an accepted value.", "22003"));
        }

        if (mode == 2)
        {
            throw new InvalidOperationException("Lifetime check exception.");
        }

        if (mode is 6 or 7)
        {
            PgLog.Write(PgLogLevel.Notice, new PgDiagnostic(mode == 6 ? "Lifetime log 🐘" : "Lifetime log.")
            {
                Detail = new string('l', PayloadLength),
            });
        }

        string normalized = mode == 3 ? "🐘" + new string('x', PayloadLength - 2) : new string(value[0], PayloadLength);
        byte[] bytes = new byte[PayloadLength];
        bytes.AsSpan().Fill((byte)value[0]);
        var extra = new PgGucExtra(bytes);
        Observe(0, normalized);
        Observe(1, extra);
        return new PgGucCheckResult<string>(normalized, extra);
    }

    /// <summary>
    /// Checks every copied extra byte during assignment and stack restoration.
    /// </summary>
    /// <param name="value">The accepted or restored native value.</param>
    /// <param name="extra">The independently copied native payload.</param>
    internal static void Assign(string value, PgGucExtra? extra)
    {
        s_calls[1]++;
        Validate(value, extra);
        Observe(2, value);
        Observe(3, extra!);
    }

    /// <summary>
    /// Checks copied payload contents and returns a fresh large display string.
    /// </summary>
    /// <param name="value">The current native value.</param>
    /// <param name="extra">The independently copied current native payload.</param>
    /// <returns>A new display string or an intentionally unrepresentable result.</returns>
    internal static string Show(string value, PgGucExtra? extra)
    {
        s_calls[2]++;
        Validate(value, extra);
        Observe(4, value);
        Observe(5, extra!);
        string output = Mode == 4 ? "🐘" + new string('x', PayloadLength - 2) : new string(value.AsSpan());
        Observe(6, output);
        return output;
    }

    /// <summary>
    /// Rejects truncated, corrupted, or mismatched native string and payload copies.
    /// </summary>
    private static void Validate(string value, PgGucExtra? extra)
    {
        if (value.Length != PayloadLength || value.AsSpan().IndexOfAnyExcept(value[0]) >= 0 ||
            extra is null || extra.Length != PayloadLength || extra.AsSpan().IndexOfAnyExcept((byte)value[0]) >= 0)
        {
            throw new InvalidOperationException("Native configuration payload was corrupted.");
        }
    }

    /// <summary>
    /// Replaces one bounded weak slot without retaining the observed object.
    /// </summary>
    private static void Observe(int slot, object value) => s_observed[slot] = new WeakReference(value);

    /// <summary>
    /// Reads the glibc allocation accounting structure by value.
    /// </summary>
    [LibraryImport("libc.so.6", EntryPoint = "mallinfo2")]
    private static partial MallocInfo ReadMallocInfo();

    /// <summary>
    /// Matches the ten size_t fields of glibc's mallinfo2 structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MallocInfo
    {
        /// <summary>
        /// Contains non-mapped arena bytes.
        /// </summary>
        public nuint ArenaBytes;

        /// <summary>
        /// Contains the number of free chunks.
        /// </summary>
        public nuint FreeChunks;

        /// <summary>
        /// Contains the number of fastbin chunks.
        /// </summary>
        public nuint FastChunks;

        /// <summary>
        /// Contains the number of mapped allocations.
        /// </summary>
        public nuint MappedChunks;

        /// <summary>
        /// Contains bytes in mapped allocations.
        /// </summary>
        public nuint MappedBytes;

        /// <summary>
        /// Preserves glibc's unused compatibility field.
        /// </summary>
        public nuint Unused;

        /// <summary>
        /// Contains free fastbin bytes.
        /// </summary>
        public nuint FastFreeBytes;

        /// <summary>
        /// Contains allocated arena bytes.
        /// </summary>
        public nuint AllocatedBytes;

        /// <summary>
        /// Contains free arena bytes.
        /// </summary>
        public nuint FreeBytes;

        /// <summary>
        /// Contains potentially releasable top-chunk bytes.
        /// </summary>
        public nuint ReleasableBytes;
    }
}
