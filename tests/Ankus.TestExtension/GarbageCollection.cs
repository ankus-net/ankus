namespace Ankus.TestExtension;

/// <summary>
/// Collects garbage completely, so a test can tell whether Ankus released a value by watching a weak reference or a
/// finalizer.
/// </summary>
internal static class GarbageCollection
{
    /// <summary>
    /// Collects every generation, runs the finalizers that collection queued, and collects what they released.
    /// </summary>
    internal static void Collect()
    {
        // codeql[cs/call-to-gc] Whether a value was released can only be observed after a full collection.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // codeql[cs/call-to-gc] Objects that finalizers released are only reclaimed by a second collection.
        GC.Collect();
    }
}
