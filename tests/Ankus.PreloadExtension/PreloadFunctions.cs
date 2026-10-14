using System.Globalization;
using System.Runtime.CompilerServices;

namespace Ankus.PreloadExtension;

/// <summary>
/// Verifies managed postmaster initialization and continued runtime operation in forked backends.
/// </summary>
public static class PreloadFunctions
{
    private const int GraphValue = 17;
    private const int ChangedGraphValue = 731;
    private const int TaskValue = 42;
    private const int TimerValue = 91;
    private const int RetainedTimerValue = 191;
    private const int FinalizerValue = 73;
    private static readonly Completion s_retainedTimerCompletion = new();
    private static int s_initializerPid;
    private static int s_initializations;
    private static Guid s_token;
    private static int s_parentFinalization;
    private static int s_parentTask;
    private static int s_parentTimer;
    private static int[] s_cleanupState = [];
    private static Node? s_root;
    private static Timer? s_retainedTimer;

    /// <summary>
    /// Creates managed state and exercises runtime services before PostgreSQL forks its backends.
    /// </summary>
    [PgInitialize]
    public static void Initialize()
    {
        s_initializations++;
        s_initializerPid = Environment.ProcessId;
        s_token = Guid.NewGuid();
        s_cleanupState = PreloadCleanupProbe.Run();
        s_root = new Node(GraphValue);
        s_root.Next = s_root;
        s_parentFinalization = CollectAndFinalize();
        s_parentTask = RunTask();
        s_parentTimer = RunTimer();
        s_retainedTimer = new Timer(static state => ((Completion)state!).Complete(RetainedTimerValue),
            s_retainedTimerCompletion, TimeSpan.FromHours(1), Timeout.InfiniteTimeSpan);
        PgLog.Write(PgLogLevel.Notice, new PgDiagnostic("Ankus managed postmaster initializer ran.")
        {
            Detail = string.Create(CultureInfo.InvariantCulture,
                $"pid={s_initializerPid};count={s_initializations};token={s_token:D}"),
        });
    }

    /// <summary>
    /// Returns the managed state inherited from the postmaster.
    /// </summary>
    /// <returns>Process and runtime state separated by vertical bars.</returns>
    [PgFunction(Name = "preload_snapshot")]
    public static string Snapshot() => string.Create(CultureInfo.InvariantCulture,
        $"{s_initializerPid}|{Environment.ProcessId}|{s_initializations}|{s_token:D}|{s_parentFinalization}|{s_parentTask}|{s_parentTimer}|{s_root?.Marker ?? -1}");

    /// <summary>
    /// Returns native callback observations collected outside a transaction during shared preload.
    /// </summary>
    /// <returns>Transaction presence, callback count, payload, native result, pending registration and retained owner.</returns>
    [PgFunction(Name = "preload_cleanup_state")]
    public static int[] CleanupState() => s_cleanupState;

    /// <summary>
    /// Exercises fresh and inherited runtime services in a forked backend.
    /// </summary>
    /// <returns>Exact task, timer, finalizer, graph, and exception results.</returns>
    [PgFunction(Name = "preload_exercise")]
    public static string Exercise()
    {
        Node root = s_root ?? throw new InvalidOperationException("The inherited graph is missing.");
        if (!ReferenceEquals(root, root.Next) || root.Marker != GraphValue)
        {
            throw new InvalidOperationException("The inherited graph was not preserved.");
        }

        root.Marker = ChangedGraphValue;
        int finallyCount = 0;
        try
        {
            throw new InvalidOperationException("Exercise managed exception handling.");
        }
        catch (InvalidOperationException)
        {
            // Handling the exception is what this checks.
        }
        finally
        {
            finallyCount++;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{RunTask()}|{RunTimer()}|{RunRetainedTimer()}|{CollectAndFinalize()}|{GraphValue}|{root.Marker}|{finallyCount}");
    }

    /// <summary>
    /// Runs one work item on the managed thread pool.
    /// </summary>
    /// <returns>The work item's fixed result.</returns>
    private static int RunTask()
    {
        Task<int> result = Task.Run(static () => TaskValue);
        if (!result.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The managed task did not execute.");
        }

        return result.Result;
    }

    /// <summary>
    /// Runs one new managed timer callback.
    /// </summary>
    /// <returns>The timer callback's fixed result.</returns>
    private static int RunTimer()
    {
        Completion completion = new();
        using Timer timer = new(static state => ((Completion)state!).Complete(TimerValue), completion, 25, Timeout.Infinite);
        if (!completion.Wait())
        {
            throw new TimeoutException("The managed timer did not execute.");
        }

        return completion.Value;
    }

    /// <summary>
    /// Rearms the timer created by the postmaster before this backend existed.
    /// </summary>
    /// <returns>The inherited timer callback's fixed result.</returns>
    private static int RunRetainedTimer()
    {
        Timer timer = s_retainedTimer ?? throw new InvalidOperationException("The inherited timer is missing.");
        if (s_retainedTimerCompletion.Value != 0)
        {
            throw new InvalidOperationException("The inherited timer fired before it was rearmed.");
        }

        if (!timer.Change(25, Timeout.Infinite) || !s_retainedTimerCompletion.Wait())
        {
            throw new TimeoutException("The inherited timer did not execute.");
        }

        timer.Dispose();
        s_retainedTimer = null;
        return s_retainedTimerCompletion.Value;
    }

    /// <summary>
    /// Forces collection and waits for an actual finalizer callback.
    /// </summary>
    /// <returns>The finalizer's fixed result.</returns>
    private static int CollectAndFinalize()
    {
        Completion completion = new();
        WeakReference weak = MakeFinalizable(completion);
        byte[][] retained = new byte[32][];
        for (int index = 0; index < retained.Length; index++)
        {
            retained[index] = new byte[16384];
            retained[index][0] = (byte)index;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (!completion.Wait() || completion.Value != FinalizerValue || weak.IsAlive)
        {
            throw new InvalidOperationException("Managed finalization did not complete.");
        }

        GC.KeepAlive(retained);
        GC.KeepAlive(s_root);
        return completion.Value;
    }

    /// <summary>
    /// Allocates a finalizable object outside the collection caller's frame.
    /// </summary>
    /// <param name="completion">The finalizer result holder.</param>
    /// <returns>A weak reference that collection must clear.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MakeFinalizable(Completion completion)
    {
        Finalizable target = new(completion);
        return new WeakReference(target);
    }

    /// <summary>
    /// Retains a cyclic object graph across PostgreSQL fork.
    /// </summary>
    /// <param name="marker">The initial marker.</param>
    private sealed class Node(int marker)
    {
        /// <summary>
        /// Gets or sets the process-local marker.
        /// </summary>
        internal int Marker
        {
            get;
            set;
        } = marker;

        /// <summary>
        /// Gets or sets the cyclic reference.
        /// </summary>
        internal Node? Next
        {
            get;
            set;
        }
    }

    /// <summary>
    /// Publishes callback results without using inherited wait handles.
    /// </summary>
    private sealed class Completion
    {
        private int _value;
        private int _completed;

        /// <summary>
        /// Gets the published result.
        /// </summary>
        internal int Value => Volatile.Read(ref _value);

        /// <summary>
        /// Publishes one callback result.
        /// </summary>
        /// <param name="value">The result value.</param>
        internal void Complete(int value)
        {
            Volatile.Write(ref _value, value);
            Volatile.Write(ref _completed, 1);
        }

        /// <summary>
        /// Waits up to two seconds for the callback.
        /// </summary>
        /// <returns>Whether the callback completed.</returns>
        internal bool Wait()
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (Volatile.Read(ref _completed) != 0)
                {
                    return true;
                }

                Thread.Sleep(10);
            }

            return Volatile.Read(ref _completed) != 0;
        }
    }

    /// <summary>
    /// Reports actual managed finalization.
    /// </summary>
    /// <param name="completion">The retained result holder.</param>
    private sealed class Finalizable(Completion completion)
    {
        /// <summary>
        /// Publishes the fixed finalizer result.
        /// </summary>
        ~Finalizable() => completion.Complete(FinalizerValue);
    }
}
