namespace Ankus.TestExtension;

/// <summary>
/// Releases values whose owners a test function holds outside a single using scope.
/// </summary>
internal static class Disposal
{
    /// <summary>
    /// Disposes every value in order, skipping SQL NULL cells.
    /// </summary>
    /// <param name="values">The values the caller owns.</param>
    internal static void DisposeAll<T>(IEnumerable<T?> values) where T : class, IDisposable
    {
        foreach (T? value in values)
        {
            value?.Dispose();
        }
    }

    /// <summary>
    /// Disposes a retained value and then stops retaining it.
    /// </summary>
    /// <param name="value">The retained value, or null.</param>
    internal static void Release<T>(ref T? value) where T : class, IDisposable
    {
        value?.Dispose();
        value = null;
    }
}
