namespace Ankus.Generators;

/// <summary>
/// Runs a program from a backend and waits for it, as pglogical runs <c>pg_dump</c> and <c>pg_restore</c>, without
/// .NET's process support and its <c>SIGCHLD</c> handling.
/// </summary>
internal static class NativeChildProcessBridge
{
    /// <summary>
    /// Implements operation 49: runs the UTF-8 program path in <c>pointer</c> with <c>value</c> NUL-terminated UTF-8
    /// arguments in <c>data</c>, and returns the exit code, or 128 plus the signal number that ended it.
    /// </summary>
    /// <remarks>
    /// Unix starts the program with <c>posix_spawn</c>, because <c>fork</c> is unsafe in a process with runtime
    /// threads, and resets the signals PostgreSQL handles or ignores to their defaults in the child. Windows uses
    /// <c>CreateProcessW</c> with the C runtime's quoting rules. The wait sleeps on the latch, so a cancellation or
    /// termination stops the child and reaps it before PostgreSQL raises the interrupt.
    /// </remarks>
    internal const string Source = """

        #ifndef WIN32
        #include <spawn.h>
        #include <sys/wait.h>
        extern char **environ;
        #endif

        #ifndef WIN32
        typedef pid_t AnkusChild;
        #else
        typedef HANDLE AnkusChild;
        #endif

        static void
        ankus_child_stop(AnkusChild child)
        {
        #ifndef WIN32
            int status;
            (void) kill(child, SIGTERM);
            while (waitpid(child, &status, 0) < 0 && errno == EINTR)
                ;
        #else
            (void) TerminateProcess(child, 1);
            (void) WaitForSingleObject(child, INFINITE);
            CloseHandle(child);
        #endif
        }

        /* Returns whether the child has ended, with its exit code. */
        static bool
        ankus_child_poll(AnkusChild child, const char *path, int64 *code)
        {
        #ifndef WIN32
            int status = 0;
            pid_t done = waitpid(child, &status, WNOHANG);
            if (done == 0 || (done < 0 && errno == EINTR))
                return false;
            if (done < 0)
                ereport(ERROR, (errcode_for_file_access(), errmsg("could not wait for \"%s\": %m", path)));
            *code = WIFEXITED(status) ? WEXITSTATUS(status) : WIFSIGNALED(status) ? 128 + WTERMSIG(status) : 255;
            return true;
        #else
            DWORD exit_code = 0;
            if (WaitForSingleObject(child, 0) != WAIT_OBJECT_0)
                return false;
            if (!GetExitCodeProcess(child, &exit_code))
                exit_code = 255;
            CloseHandle(child);
            *code = (int64) exit_code;
            return true;
        #endif
        }

        #ifdef WIN32
        /* Quotes one argument for the C runtime's command-line parser. */
        static void
        ankus_child_quote(StringInfo line, const char *argument)
        {
            if (argument[0] != '\0' && strpbrk(argument, " \t\n\v\"") == NULL)
            {
                appendStringInfoString(line, argument);
                return;
            }

            appendStringInfoChar(line, '"');
            for (const char *character = argument;; character++)
            {
                int backslashes = 0;
                while (*character == '\\')
                {
                    character++;
                    backslashes++;
                }

                if (*character == '\0')
                {
                    for (int index = 0; index < backslashes * 2; index++)
                        appendStringInfoChar(line, '\\');
                    break;
                }

                for (int index = 0; index < (*character == '"' ? backslashes * 2 + 1 : backslashes); index++)
                    appendStringInfoChar(line, '\\');
                appendStringInfoChar(line, *character);
            }

            appendStringInfoChar(line, '"');
        }

        static wchar_t *
        ankus_child_wide(const char *utf8)
        {
            int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8, -1, NULL, 0);
            wchar_t *wide;
            if (length <= 0)
                ereport(ERROR, (errcode(ERRCODE_CHARACTER_NOT_IN_REPERTOIRE), errmsg("invalid program text")));
            wide = palloc(sizeof(wchar_t) * length);
            (void) MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8, -1, wide, length);
            return wide;
        }
        #endif

        static AnkusChild
        ankus_child_start(const char *path, const char *arguments, int count)
        {
        #ifndef WIN32
            static const int defaults[] = { SIGPIPE, SIGINT, SIGTERM, SIGQUIT, SIGHUP, SIGUSR1, SIGUSR2, SIGCHLD, SIGALRM,
                SIGTTIN, SIGTTOU, SIGWINCH };
            char **argv = palloc0(sizeof(char *) * (count + 2));
            posix_spawnattr_t attributes;
            sigset_t mask;
            sigset_t reset;
            pid_t child;
            int error;
            argv[0] = (char *) path;
            for (int index = 0; index < count; index++)
            {
                argv[index + 1] = (char *) arguments;
                arguments += strlen(arguments) + 1;
            }

            sigemptyset(&mask);
            sigemptyset(&reset);
            for (size_t index = 0; index < lengthof(defaults); index++)
                sigaddset(&reset, defaults[index]);
            posix_spawnattr_init(&attributes);
            posix_spawnattr_setsigmask(&attributes, &mask);
            posix_spawnattr_setsigdefault(&attributes, &reset);
            posix_spawnattr_setflags(&attributes, POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF);
            fflush(stdout);
            fflush(stderr);
            error = posix_spawn(&child, path, NULL, &attributes, argv, environ);
            posix_spawnattr_destroy(&attributes);
            pfree(argv);
            if (error != 0)
            {
                errno = error;
                ereport(ERROR, (errcode_for_file_access(), errmsg("could not execute \"%s\": %m", path)));
            }

            return child;
        #else
            StringInfoData line;
            STARTUPINFOW startup;
            PROCESS_INFORMATION process;
            wchar_t *wide_path;
            wchar_t *wide_line;
            initStringInfo(&line);
            ankus_child_quote(&line, path);
            for (int index = 0; index < count; index++)
            {
                appendStringInfoChar(&line, ' ');
                ankus_child_quote(&line, arguments);
                arguments += strlen(arguments) + 1;
            }

            wide_path = ankus_child_wide(path);
            wide_line = ankus_child_wide(line.data);
            memset(&startup, 0, sizeof(startup));
            startup.cb = sizeof(startup);
            fflush(stdout);
            fflush(stderr);
            if (!CreateProcessW(wide_path, wide_line, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &process))
            {
                _dosmaperr(GetLastError());
                ereport(ERROR, (errcode_for_file_access(), errmsg("could not execute \"%s\": %m", path)));
            }

            CloseHandle(process.hThread);
            pfree(wide_path);
            pfree(wide_line);
            pfree(line.data);
            return process.hProcess;
        #endif
        }

        static void
        ankus_memory_child_process(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            const char *path = (const char *) request->pointer;
            int count = (int) request->value;
            volatile AnkusChild child;
            volatile bool running = false;
            int64 code = 0;
            if (path == NULL || path[0] == '\0' || request->value < 0 || request->value > 4096 || (count > 0 && request->data == 0))
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid program or arguments")));
            child = ankus_child_start(path, (const char *) request->data, count);
            running = true;
            PG_TRY();
            {
                while (!ankus_child_poll(child, path, &code))
                {
                    /* A termination exits without unwinding, so the child stops first. */
                    if (ProcDiePending)
                    {
                        running = false;
                        ankus_child_stop(child);
                    }

                    CHECK_FOR_INTERRUPTS();
                    if (!running)
                        ereport(ERROR, (errcode(ERRCODE_ADMIN_SHUTDOWN), errmsg("\"%s\" was stopped", path)));
                    (void) WaitLatch(MyLatch, WL_LATCH_SET | WL_TIMEOUT | WL_EXIT_ON_PM_DEATH, 50L, PG_WAIT_EXTENSION);
                    ResetLatch(MyLatch);
                }

                running = false;
                result->value = (intptr_t) code;
            }
            PG_CATCH();
            {
                if (running)
                    ankus_child_stop(child);
                PG_RE_THROW();
            }
            PG_END_TRY();
        }

        """;
}
