namespace Ankus.PreloadExtension;

/// <summary>
/// Leaves an explicitly created thread running when the postmaster's initializer returns.
/// </summary>
public static partial class PreloadLateThread
{
    /// <summary>
    /// Gets how long the thread runs, in milliseconds. Zero starts no thread.
    /// </summary>
    [PgGucInt("ankus_preload.late_thread_ms", 0, "Run time of a thread the initializer leaves running",
        Minimum = 0, Context = PgGucContext.Postmaster)]
    public static partial int Milliseconds { get; }

    /// <summary>
    /// Starts the thread when the setting is positive.
    /// </summary>
    internal static void Start()
    {
        int milliseconds = Milliseconds;
        if (milliseconds > 0)
        {
            new Thread(() => Thread.Sleep(milliseconds)).Start();
        }
    }
}
