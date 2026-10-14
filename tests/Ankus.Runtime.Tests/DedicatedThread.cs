namespace Ankus.Runtime.Tests;

/// <summary>
/// Runs test code on threads of its own, for assertions about thread affinity and concurrency.
/// </summary>
/// <remarks>
/// The default scheduler gives a long-running task a thread of its own, and a waiting thread never runs that task
/// inline, so the code never runs on the thread that started it. A failure surfaces on the waiting thread as the
/// exception the code threw.
/// </remarks>
internal static class DedicatedThread
{
    /// <summary>
    /// Runs an action on a new thread, waits for it, and rethrows what it threw.
    /// </summary>
    /// <param name="action">The code to run away from the calling thread.</param>
    internal static void Run(Action action) => Start(action).GetAwaiter().GetResult();

    /// <summary>
    /// Runs an action on several new threads at once, waits for all of them, and rethrows the first failure.
    /// </summary>
    /// <param name="count">The number of threads.</param>
    /// <param name="timeout">How long the threads may take together before the wait fails.</param>
    /// <param name="action">The code each thread runs.</param>
    internal static void RunConcurrently(int count, TimeSpan timeout, Action action)
        => Task.WhenAll(Enumerable.Range(0, count).Select(_ => Start(action))).WaitAsync(timeout).GetAwaiter().GetResult();

    private static Task Start(Action action)
        => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
