using System.Runtime.InteropServices;

namespace Ankus.TestExtension;

/// <summary>
/// Observes where the kernel leaves signals sent to a backend while managed runtime threads run beside it.
/// </summary>
public static class BackendSignalFunctions
{
    /// <summary>
    /// Apple's signal number for <c>SIGUSR1</c>; Linux uses 10.
    /// </summary>
    private const int AppleUser1 = 30;

    /// <summary>
    /// The <c>SIGALRM</c> number on both Linux and Apple.
    /// </summary>
    private const int Alarm = 14;

    /// <summary>
    /// Apple's <c>SA_SIGINFO</c> flag.
    /// </summary>
    private const int AppleSigInfo = 0x40;

    /// <summary>
    /// The offset of <c>sa_flags</c> in Apple's 16-byte <c>struct sigaction</c>.
    /// </summary>
    private const int AppleFlagsOffset = 12;

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

            return Observe([OperatingSystem.IsMacOS() ? AppleUser1 : 10, Alarm]);
        }
        finally
        {
            s_release.Set();
            parked.Join();
        }
    }

    /// <summary>
    /// Reinstalls the routed <c>SIGUSR1</c> handler as a plain function, as code that saves and restores handlers
    /// through <c>signal</c> or older <c>pqsignal</c> results does on macOS.
    /// </summary>
    /// <returns>
    /// Whether the installed handler took sender information, whether a blocked signal stayed pending and was then
    /// delivered, and whether the next runtime thread restored that information.
    /// </returns>
    [PgFunction]
    public static unsafe bool[] RestoredPlainBackendSignalHandler()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Signal routing applies to macOS backends.");
        }

        byte* action = stackalloc byte[16];
        Signals.Action(AppleUser1, null, action);
        bool informed = (*(int*)(action + AppleFlagsOffset) & AppleSigInfo) != 0;
        *(int*)(action + AppleFlagsOffset) &= ~AppleSigInfo;
        Signals.Action(AppleUser1, action, null);

        // The handler now runs without sender information on the backend thread.
        Signals.Kill(Signals.GetProcessId(), AppleUser1);
        bool[] observed = Observe([AppleUser1]);

        // The runtime asks Ankus to prepare before it starts a thread.
        var thread = new Thread(static () => { });
        thread.Start();
        thread.Join();
        Signals.Action(AppleUser1, null, action);
        bool restored = (*(int*)(action + AppleFlagsOffset) & AppleSigInfo) != 0;
        return [informed, observed[0], observed[1], restored];
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
    /// Blocks the signals on the backend thread, sends them to the process and inspects what remains pending.
    /// </summary>
    /// <param name="numbers">The signal numbers to send.</param>
    /// <returns>The held flags followed by the delivered flags.</returns>
    private static unsafe bool[] Observe(int[] numbers)
    {
        // Apple numbers the mask operations differently from Linux.
        bool apple = OperatingSystem.IsMacOS();
        int block = apple ? 1 : 0;
        int unblock = apple ? 2 : 1;

        // Large enough for Linux's 1024-bit set and Apple's 32-bit set.
        byte* set = stackalloc byte[128];
        byte* observed = stackalloc byte[128];
        bool[] result = new bool[numbers.Length * 2];
        Signals.Empty(set);
        foreach (int number in numbers)
        {
            Signals.Add(set, number);
        }

        if (Signals.Mask(block, set, null) != 0)
        {
            throw new InvalidOperationException("Could not block the backend signals.");
        }

        try
        {
            int process = Signals.GetProcessId();
            foreach (int number in numbers)
            {
                if (Signals.Kill(process, number) != 0)
                {
                    throw new InvalidOperationException($"Could not send signal {number}.");
                }
            }

            Thread.Sleep(200);
            Signals.Empty(observed);
            Signals.Pending(observed);
            for (int index = 0; index < numbers.Length; index++)
            {
                result[index] = Signals.Member(observed, numbers[index]) == 1;
            }
        }
        finally
        {
            // PostgreSQL's handlers run here, on the backend thread.
            Signals.Mask(unblock, set, null);
        }

        Signals.Empty(observed);
        Signals.Pending(observed);
        for (int index = 0; index < numbers.Length; index++)
        {
            result[numbers.Length + index] = Signals.Member(observed, numbers[index]) == 0;
        }

        return result;
    }

    /// <summary>
    /// Holds the C library's signal functions, resolved from the backend executable.
    /// </summary>
    private static unsafe class Signals
    {
        /// <summary>
        /// The backend executable, whose dependencies include the C library.
        /// </summary>
        private static readonly nint s_program = NativeLibrary.GetMainProgramHandle();

        /// <summary>
        /// Changes the calling thread's signal mask.
        /// </summary>
        internal static delegate* unmanaged<int, byte*, byte*, int> Mask { get; } = (delegate* unmanaged<int, byte*, byte*, int>)NativeLibrary.GetExport(s_program, "pthread_sigmask");

        /// <summary>
        /// Clears a signal set.
        /// </summary>
        internal static delegate* unmanaged<byte*, int> Empty { get; } = (delegate* unmanaged<byte*, int>)NativeLibrary.GetExport(s_program, "sigemptyset");

        /// <summary>
        /// Adds a signal to a set.
        /// </summary>
        internal static delegate* unmanaged<byte*, int, int> Add { get; } = (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(s_program, "sigaddset");

        /// <summary>
        /// Tests whether a set contains a signal.
        /// </summary>
        internal static delegate* unmanaged<byte*, int, int> Member { get; } = (delegate* unmanaged<byte*, int, int>)NativeLibrary.GetExport(s_program, "sigismember");

        /// <summary>
        /// Reads the calling thread's pending signals.
        /// </summary>
        internal static delegate* unmanaged<byte*, int> Pending { get; } = (delegate* unmanaged<byte*, int>)NativeLibrary.GetExport(s_program, "sigpending");

        /// <summary>
        /// Reads or replaces a signal disposition.
        /// </summary>
        internal static delegate* unmanaged<int, byte*, byte*, int> Action { get; } = (delegate* unmanaged<int, byte*, byte*, int>)NativeLibrary.GetExport(s_program, "sigaction");

        /// <summary>
        /// Sends a signal to a process.
        /// </summary>
        internal static delegate* unmanaged<int, int, int> Kill { get; } = (delegate* unmanaged<int, int, int>)NativeLibrary.GetExport(s_program, "kill");

        /// <summary>
        /// Gets the current process identifier.
        /// </summary>
        internal static delegate* unmanaged<int> GetProcessId { get; } = (delegate* unmanaged<int>)NativeLibrary.GetExport(s_program, "getpid");
    }
}
