using System.Runtime.InteropServices;

namespace Ankus.TestExtension;

/// <summary>
/// Observes where the kernel leaves signals sent to a backend while managed runtime threads run beside it.
/// </summary>
public static class BackendSignalFunctions
{
    /// <summary>
    /// Releases the parked managed and thread-pool threads.
    /// </summary>
    private static readonly ManualResetEventSlim s_release = new();

    /// <summary>
    /// Sends <c>SIGUSR1</c> and <c>SIGALRM</c> to this backend while its thread blocks them.
    /// </summary>
    /// <returns>
    /// Whether each signal stayed pending beside parked managed and thread-pool threads, then whether unblocking
    /// delivered each one.
    /// </returns>
    /// <remarks>
    /// The kernel delivers a signal sent to a process to any thread that does not block it, but PostgreSQL's handlers
    /// assume they interrupt the backend thread. Runtime threads must therefore leave both signals pending.
    /// </remarks>
    [PgFunction]
    public static bool[] HeldBackendSignals()
    {
        using var started = new CountdownEvent(2);
        s_release.Reset();
        var parked = new Thread(() => Park(started)) { IsBackground = true };
        parked.Start();
        ThreadPool.UnsafeQueueUserWorkItem(static state => Park((CountdownEvent)state!), started);
        try
        {
            if (!started.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException("The parked managed threads did not start.");
            }

            return Observe();
        }
        finally
        {
            s_release.Set();
            parked.Join();
        }
    }

    /// <summary>
    /// Reports that a thread started, then waits for release.
    /// </summary>
    /// <param name="started">The start countdown.</param>
    private static void Park(CountdownEvent started)
    {
        started.Signal();
        s_release.Wait();
    }

    /// <summary>
    /// Blocks both signals on the backend thread, sends them to the process and inspects what remains pending.
    /// </summary>
    /// <returns>The held flags followed by the delivered flags.</returns>
    private static unsafe bool[] Observe()
    {
        nint program = NativeLibrary.GetMainProgramHandle();
        var mask = (delegate* unmanaged<int, byte*, byte*, int>)NativeLibrary.GetExport(program, "pthread_sigmask");
        var empty = (delegate* unmanaged<byte*, int>)NativeLibrary.GetExport(program, "sigemptyset");
        var add = (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(program, "sigaddset");
        var member = (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(program, "sigismember");
        var pending = (delegate* unmanaged<byte*, int>)NativeLibrary.GetExport(program, "sigpending");
        var kill = (delegate* unmanaged<int, int, int>)NativeLibrary.GetExport(program, "kill");
        var getpid = (delegate* unmanaged<int>)NativeLibrary.GetExport(program, "getpid");

        // Apple numbers SIGUSR1 and the mask operations differently from Linux; SIGALRM is 14 on both.
        bool apple = OperatingSystem.IsMacOS();
        int[] signals = [apple ? 30 : 10, 14];
        int block = apple ? 1 : 0;
        int unblock = apple ? 2 : 1;

        // Large enough for Linux's 1024-bit set and Apple's 32-bit set.
        byte* set = stackalloc byte[128];
        byte* observed = stackalloc byte[128];
        bool[] result = new bool[signals.Length * 2];
        empty(set);
        foreach (int signal in signals)
        {
            add(set, signal);
        }

        if (mask(block, set, null) != 0)
        {
            throw new InvalidOperationException("Could not block the backend signals.");
        }

        try
        {
            int process = getpid();
            foreach (int signal in signals)
            {
                if (kill(process, signal) != 0)
                {
                    throw new InvalidOperationException($"Could not send signal {signal}.");
                }
            }

            Thread.Sleep(200);
            empty(observed);
            pending(observed);
            for (int index = 0; index < signals.Length; index++)
            {
                result[index] = member(observed, signals[index]) == 1;
            }
        }
        finally
        {
            // PostgreSQL's handlers run here, on the backend thread.
            mask(unblock, set, null);
        }

        empty(observed);
        pending(observed);
        for (int index = 0; index < signals.Length; index++)
        {
            result[signals.Length + index] = member(observed, signals[index]) == 0;
        }

        return result;
    }
}
