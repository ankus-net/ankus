using System.Globalization;

namespace Ankus.Build;

internal static partial class NativeBindingCache
{
    /// <summary>
    /// Reads the byte budget for each generated-source or managed-companion store.
    /// </summary>
    internal static long ReadMaximumBytes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 2L * 1024 * 1024 * 1024;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) || bytes < 1)
        {
            throw new FormatException("ANKUS_BINDING_CACHE_MAX_BYTES must be a positive integer number of bytes.");
        }

        return bytes;
    }

    /// <summary>
    /// Evicts the least recently used idle entries and abandoned staging without disturbing active leases.
    /// </summary>
    internal static async Task TrimAsync(string root, long maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        await using FileStream? maintenance = TryLock(Path.Join(root, ".retention.lock"));
        if (maintenance is null)
        {
            return;
        }

        var entries = new List<(string Path, string Key, DateTime Used, long Bytes)>();
        long total = 0;
        foreach (string path in Directory.GetDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (name.Length < 64 || name[..64].Any(static value => !char.IsAsciiHexDigitUpper(value)))
            {
                continue;
            }

            string key = name[..64];
            bool abandoned = (name.StartsWith(key + ".stage-", StringComparison.Ordinal) ||
                name.StartsWith(key + ".replaced-", StringComparison.Ordinal)) &&
                Guid.TryParseExact(name[(name.LastIndexOf('-') + 1)..], "N", out _);
            if (name.Length != 64 && !abandoned)
            {
                continue;
            }

            await using FileStream? ownership = TryLock(Path.Join(root, key + ".lock"));
            if (ownership is null || !Directory.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (abandoned)
            {
                await NativeBuildDirectory.DeleteAsync(path);
                continue;
            }

            long size = Directory.GetFiles(path).Sum(static file => new FileInfo(file).Length);
            entries.Add((path, key, Directory.GetLastWriteTimeUtc(path), size));
            total = checked(total + size);
        }

        foreach ((string path, string key, DateTime used, long bytes) in entries.OrderBy(static entry => entry.Used))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total <= maximumBytes)
            {
                break;
            }

            await using FileStream? ownership = TryLock(Path.Join(root, key + ".lock"));
            if (ownership is null || !Directory.Exists(path) || Directory.GetLastWriteTimeUtc(path) != used)
            {
                continue;
            }

            await NativeBuildDirectory.DeleteAsync(path);
            total -= bytes;
        }
    }

    private static FileStream? TryLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 ||
            error.HResult == (OperatingSystem.IsMacOS() ? 35 : 11))
        {
            return null;
        }
    }
}
