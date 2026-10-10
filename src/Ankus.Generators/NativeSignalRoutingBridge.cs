namespace Ankus.Generators;

/// <summary>
/// Keeps PostgreSQL's signal handlers on the backend thread while runtime threads share its process.
/// </summary>
internal static class NativeSignalRoutingBridge
{
    /// <summary>
    /// Gets the handler routing shared by every Ankus library loaded into a process.
    /// </summary>
    /// <remarks>
    /// The kernel delivers a signal sent to a process to any thread that leaves it unblocked. Runtime threads start with
    /// these signals blocked, but on macOS <c>sigprocmask</c> changes every thread's mask, so PostgreSQL unblocks them
    /// whenever it restores its own. A handler entered on another thread therefore hands the signal to the backend
    /// thread, which holds it until it can run the handler itself, as a single-threaded backend would.
    /// </remarks>
    internal const string Source = """
        #ifdef __APPLE__
        #include <pthread.h>
        #include <signal.h>
        #include <unistd.h>

        typedef struct AnkusSignalRouting
        {
            int version;
            void (*ensure)(void);
        } AnkusSignalRouting;

        static const int ankus_routed_signals[] = {SIGHUP, SIGINT, SIGQUIT, SIGALRM, SIGTERM, SIGUSR1, SIGUSR2, SIGURG, SIGCHLD};
        static struct sigaction ankus_routed_actions[NSIG];
        static volatile sig_atomic_t ankus_routed_pid[NSIG];
        static volatile sig_atomic_t ankus_routed_uid[NSIG];
        static pthread_t ankus_backend_thread;
        static AnkusSignalRouting *ankus_signal_routing;

        extern void RhSetThreadCreationCallback(void (*callback)(void));

        /* Hidden, so each library calls its own definition even when PostgreSQL loads libraries globally. */
        __attribute__((visibility("hidden"))) void ankus_signal_routing_initialize(void);
        __attribute__((visibility("hidden"))) void ankus_signal_routing_ensure(void);

        static void
        ankus_signal_route(int signo, siginfo_t *info, void *context)
        {
            if (!pthread_main_np())
            {
                int save_errno = errno;
                if (info != NULL && info->si_pid != 0)
                {
                    ankus_routed_uid[signo] = (sig_atomic_t) info->si_uid;
                    ankus_routed_pid[signo] = info->si_pid;
                }

                pthread_kill(ankus_backend_thread, signo);
                errno = save_errno;
                return;
            }

            /* Report the original sender, as PostgreSQL logs it for some signals. */
            siginfo_t routed;
            if (info != NULL && ankus_routed_pid[signo] != 0 && (info->si_pid == 0 || info->si_pid == getpid()))
            {
                routed = *info;
                routed.si_pid = ankus_routed_pid[signo];
                routed.si_uid = (uid_t) ankus_routed_uid[signo];
                info = &routed;
            }

            ankus_routed_pid[signo] = 0;
            if ((ankus_routed_actions[signo].sa_flags & SA_SIGINFO) != 0)
            {
                ankus_routed_actions[signo].sa_sigaction(signo, info, context);
            }
            else
            {
                ankus_routed_actions[signo].sa_handler(signo);
            }
        }

        /* Runs on the backend thread only, so it never races the handlers it replaces. */
        static void
        ankus_signal_routing_wrap(void)
        {
            ankus_backend_thread = pthread_self();
            for (size_t index = 0; index < lengthof(ankus_routed_signals); index++)
            {
                int signo = ankus_routed_signals[index];
                struct sigaction current;
                if (sigaction(signo, NULL, &current) != 0)
                {
                    continue;
                }

                if ((current.sa_flags & SA_SIGINFO) != 0
                    ? current.sa_sigaction == ankus_signal_route
                    : current.sa_handler == SIG_DFL || current.sa_handler == SIG_IGN)
                {
                    continue;
                }

                ankus_routed_actions[signo] = current;
                struct sigaction routed = current;
                routed.sa_sigaction = ankus_signal_route;
                routed.sa_flags |= SA_SIGINFO;
                sigaction(signo, &routed, NULL);
            }
        }

        static AnkusSignalRouting ankus_signal_routing_local = {1, ankus_signal_routing_wrap};

        /* The runtime calls this before it starts a thread, including in a child of a preloading postmaster. */
        static void
        ankus_signal_routing_thread_creating(void)
        {
            if (pthread_main_np() && ankus_signal_routing != NULL)
            {
                ankus_signal_routing->ensure();
            }
        }

        void
        ankus_signal_routing_initialize(void)
        {
            /* The first Ankus library in the process owns the wrapper, so libraries never wrap each other. */
            AnkusSignalRouting **shared = (AnkusSignalRouting **) find_rendezvous_variable("ankus_signal_routing");
            if (*shared == NULL)
            {
                *shared = &ankus_signal_routing_local;
            }

            ankus_signal_routing = *shared;
            RhSetThreadCreationCallback(ankus_signal_routing_thread_creating);
            ankus_signal_routing->ensure();
        }

        /* Rewraps handlers that Ankus itself replaced, such as a background worker's. */
        void
        ankus_signal_routing_ensure(void)
        {
            if (ankus_signal_routing != NULL)
            {
                ankus_signal_routing->ensure();
            }
        }

        #define ANKUS_SIGNAL_ROUTING_READY() \
            do \
            { \
                if (unlikely(ankus_signal_routing == NULL)) \
                { \
                    ankus_signal_routing_initialize(); \
                } \
            } while (0)
        #else
        /* Linux runtime threads keep these signals blocked; Windows PostgreSQL emulates them on the backend thread. */
        #define ankus_signal_routing_initialize() ((void) 0)
        #define ankus_signal_routing_ensure() ((void) 0)
        #define ANKUS_SIGNAL_ROUTING_READY() ((void) 0)
        #endif

        """;
}
