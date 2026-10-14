namespace Ankus.TestExtension;

/// <summary>
/// Exercises PostgreSQL logging and managed unwinding through ordinary extension functions.
/// </summary>
public static class PgLogFunctions
{
    /// <summary>
    /// Calls the literal severity helpers and proves the same backend remains usable after nonterminal reporting.
    /// </summary>
    /// <param name="level">The helper's PostgreSQL reporting severity.</param>
    /// <param name="message">Literal text, including percent signs and Unicode.</param>
    /// <returns>The query result after a nonterminal report.</returns>
    [PgFunction]
    public static int LogHelper(int level, string message)
    {
        Action<string> report = (PgLogLevel)level switch
        {
            PgLogLevel.Debug5 => PgLog.Debug5,
            PgLogLevel.Debug4 => PgLog.Debug4,
            PgLogLevel.Debug3 => PgLog.Debug3,
            PgLogLevel.Debug2 => PgLog.Debug2,
            PgLogLevel.Debug1 => PgLog.Debug1,
            PgLogLevel.Log => PgLog.Log,
            PgLogLevel.ServerOnly => PgLog.ServerOnly,
            PgLogLevel.Info => PgLog.Info,
            PgLogLevel.Notice => PgLog.Notice,
            PgLogLevel.Warning => PgLog.Warning,
            PgLogLevel.Error => PgLog.Error,
            PgLogLevel.Fatal => PgLog.Fatal,
            PgLogLevel.Panic => PgLog.Panic,
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };
        report(message);
        return Spi.ExecuteScalar<int>("SELECT 42");
    }

    /// <summary>
    /// Catches default-code errors from both convenience overloads without losing fields or backend access.
    /// </summary>
    /// <param name="structured">Whether to provide optional detail and hint fields.</param>
    /// <returns>The original error and continued query result.</returns>
    [PgFunction]
    public static string LogCatchHelperError(bool structured)
    {
        try
        {
            if (structured)
            {
                PgLog.Error(new PgDiagnostic("helper error") { Detail = "detail", Hint = "hint" });
            }
            else
            {
                PgLog.Error("helper error");
            }
        }
        catch (PgException error)
        {
            return error.SqlState + "|" + error.Message + "|" + error.Detail + "|" + error.Hint + "|" + Spi.ExecuteScalar<int>("SELECT 42");
        }

        throw new InvalidOperationException("The ERROR helper returned normally.");
    }

    /// <summary>
    /// Observes inherited holdoffs after the native reporter fails and its subtransaction rolls back.
    /// </summary>
    /// <returns>The original diagnostic and native holdoff counts before managed callback exit.</returns>
    [PgFunction]
    public static string LogCatchNativeReportError()
    {
        try
        {
            PgLog.Write(PgLogLevel.Notice, "native hook report");
        }
        catch (PgException error)
        {
            return $"{error.SqlState}|{error.Message}|{Spi.ExecuteScalar<long>("SELECT tests.raw_call_holdoffs()")}";
        }

        throw new InvalidOperationException("The armed native reporter did not fail.");
    }

    /// <summary>
    /// Reaches a second report before explicitly processing cancellation raised during the first report.
    /// </summary>
    /// <param name="level">The first report's nonterminal severity.</param>
    /// <returns>A value only if PostgreSQL did not cancel the statement.</returns>
    [PgFunction]
    public static int LogDeferredCancellation(int level)
    {
        PgLog.Write((PgLogLevel)level, "pending report cancellation");
        PgLog.Write(PgLogLevel.Notice, "continued after report");
        PgInterrupts.Check();
        return 42;
    }

    /// <summary>
    /// Reports literal text and returns normally for nonterminal levels.
    /// </summary>
    /// <param name="level">The managed severity.</param>
    /// <param name="message">The primary text.</param>
    /// <returns>The backend value after logging.</returns>
    [PgFunction]
    public static int LogMessage(int level, string message)
    {
        PgLog.Write((PgLogLevel)level, message);
        return Spi.ExecuteScalar<int>("SELECT 42");
    }

    /// <summary>
    /// Raises an ERROR with its own message domain, as pgrx's <c>ereport_domain!</c> test does.
    /// </summary>
    [PgFunction]
    public static void RaiseDomain()
        => throw new PgException(PgSqlStates.InternalError, "ereport error") { Domain = "test_extension_domain" };

    /// <summary>
    /// Catches errors reported through PostgreSQL and returns the message domain recorded in their ErrorData.
    /// </summary>
    /// <param name="sql">The statement that fails.</param>
    /// <returns>The SQLSTATE, message and domain.</returns>
    [PgFunction]
    public static string CatchDomain(string sql)
    {
        try
        {
            Spi.Execute(sql);
        }
        catch (PgException error)
        {
            return $"{error.SqlState}|{error.Message}|{error.Domain}";
        }

        return "no error";
    }

    /// <summary>
    /// Logs an interpolated message whose expression counts its evaluations, as pgrx's argument-evaluation tests do.
    /// </summary>
    /// <param name="level">The managed severity.</param>
    /// <returns>How many times the interpolated expression was evaluated.</returns>
    [PgFunction]
    public static int LogLazy(int level)
    {
        int evaluations = 0;
        PgLog.Write((PgLogLevel)level, $"lazy {++evaluations}");
        return evaluations;
    }

    /// <summary>
    /// Tests active server and client reporting thresholds.
    /// </summary>
    /// <param name="level">The managed severity.</param>
    /// <returns>Whether the level is enabled.</returns>
    [PgFunction]
    public static bool LogEnabled(int level) => PgLog.IsEnabled((PgLogLevel)level);

    /// <summary>
    /// Reports all structured fields through a scoped connection, retaining ownership of the session.
    /// </summary>
    /// <param name="message">The primary text.</param>
    /// <param name="helper">Whether to use the structured warning helper.</param>
    /// <returns>The result from the existing session after reporting.</returns>
    [PgFunction]
    public static int LogDiagnostic(string message, bool helper = false)
        => Spi.Connect(session =>
        {
            var diagnostic = new PgDiagnostic(message)
            {
                SqlState = PgSqlStates.WarningDeprecatedFeature,
                Detail = "client détail",
                DetailLog = "server-only détail",
                Hint = "retry 🐘",
                Context = "managed context",
                SchemaName = "schéma",
                TableName = "t",
                ColumnName = "c",
                DataTypeName = "custom_type",
                ConstraintName = "constraint_name",
                Position = 3,
                InternalPosition = 2,
                InternalQuery = "SELECT 1",
                File = "logging.cs",
                Line = 42,
                Routine = "LogDiagnostic",
            };
            if (helper)
            {
                PgLog.Warning(diagnostic);
            }
            else
            {
                PgLog.Write(PgLogLevel.Warning, diagnostic);
            }

            return session.ExecuteScalar<int>("SELECT 42");
        });

    /// <summary>
    /// Catches an ERROR before the native boundary and proves normal SPI work remains possible.
    /// </summary>
    /// <returns>The structured error and continued query result.</returns>
    [PgFunction]
    public static string LogCatchError()
    {
        try
        {
            PgLog.Write(PgLogLevel.Error, new PgDiagnostic("caught error") { SqlState = PgSqlStates.InvalidParameterValue, Detail = "detail", Hint = "hint" });
        }
        catch (PgException error)
        {
            return error.SqlState + "|" + error.Message + "|" + error.Detail + "|" + error.Hint + "|" + Spi.ExecuteScalar<int>("SELECT 42");
        }

        throw new InvalidOperationException("ERROR returned normally.");
    }

    /// <summary>
    /// Reports a terminal message after proving managed finally blocks execute.
    /// </summary>
    /// <param name="level">ERROR, FATAL, or PANIC.</param>
    /// <param name="marker">The unique primary message.</param>
    /// <param name="mode">Zero to propagate, one to swallow, or two to swallow across an explicit recovery scope.</param>
    /// <param name="helper">Whether to use the structured severity helper.</param>
    [PgFunction]
    public static void LogTerminal(int level, string marker, int mode = 0, bool helper = false)
    {
        try
        {
            void Report() => Spi.Connect(session =>
            {
                session.Execute("INSERT INTO log_rollback VALUES (99)");
                var diagnostic = new PgDiagnostic(marker) { SqlState = PgSqlStates.RaiseException, Detail = "terminal detail" };
                if (helper)
                {
                    Action<PgDiagnostic> report = (PgLogLevel)level switch
                    {
                        PgLogLevel.Error => PgLog.Error,
                        PgLogLevel.Fatal => PgLog.Fatal,
                        PgLogLevel.Panic => PgLog.Panic,
                        _ => throw new ArgumentOutOfRangeException(nameof(level)),
                    };
                    report(diagnostic);
                }
                else
                {
                    PgLog.Write((PgLogLevel)level, diagnostic);
                }
            });

            if (mode == 2)
            {
                PgTransaction.RunInSubtransaction(Report);
            }
            else
            {
                Report();
            }
        }
        catch (Exception) when (mode != 0)
        {
            // Deliberately swallow the managed exception; the native frame must retain the terminal report.
        }
        finally
        {
            PgLog.Write(PgLogLevel.Warning, "finally " + marker);
        }
    }

    /// <summary>
    /// Attempts logging on a managed worker thread.
    /// </summary>
    /// <returns>The rejection message.</returns>
    [PgFunction]
    public static string LogWorker()
        => Task.Run(() =>
        {
            try
            {
                PgLog.Write(PgLogLevel.Notice, "worker");
                return "unexpected success";
            }
            catch (InvalidOperationException error)
            {
                return error.Message;
            }
        }).GetAwaiter().GetResult();

    /// <summary>
    /// Exercises managed validation and native encoding failures before continuing on the same session.
    /// </summary>
    /// <param name="mode">The invalid input case.</param>
    /// <returns>The diagnostic and the continued query value.</returns>
    [PgFunction]
    public static string LogInvalid(int mode)
    {
        try
        {
            switch (mode)
            {
                case 0:
                    PgLog.Write((PgLogLevel)(-1), "bad");
                    break;
                case 1:
                    PgLog.IsEnabled((PgLogLevel)13);
                    break;
                case 2:
                    PgLog.Write(PgLogLevel.Warning, new PgDiagnostic("bad") { SqlState = "bad" });
                    break;
                case 3:
                    PgLog.Write(PgLogLevel.Error, new PgDiagnostic("bad") { SqlState = "00000" });
                    break;
                case 4:
                    PgLog.Write(PgLogLevel.Warning, "bad\0message");
                    break;
                case 5:
                    PgLog.Write(PgLogLevel.Warning, "\uD800");
                    break;
                case 6:
                    PgLog.Write(PgLogLevel.Warning, new PgDiagnostic("bad") { Detail = "\uD800" });
                    break;
                case 7:
                    PgLog.Write(PgLogLevel.Warning, (PgDiagnostic)null!);
                    break;
                case 8:
                    PgLog.Write(PgLogLevel.Warning, "🐘");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
        catch (Exception error) when (error is ArgumentException or PgException)
        {
            string kind = error is PgException postgres ? postgres.SqlState : error.GetType().Name;
            return kind + "|" + Spi.ExecuteScalar<int>("SELECT 42");
        }

        return "unexpected success";
    }

    /// <summary>
    /// Measures operation-context retention after repeated structured reports.
    /// </summary>
    /// <returns>The number of extra subtransaction contexts.</returns>
    [PgFunction]
    public static long LogContextGrowth()
    {
        const string count = "SELECT count(*) FROM ankus_test_memory.contexts WHERE name = 'CurTransactionContext'";
        var diagnostic = new PgDiagnostic(new string('x', 10000)) { File = "logging.cs", Routine = "repeat" };
        PgLog.Write(PgLogLevel.ServerOnly, diagnostic);
        long before = Spi.ExecuteScalar<long>(count);
        for (int index = 0; index < 100; index++)
        {
            PgLog.Write(PgLogLevel.ServerOnly, diagnostic);
        }

        return Spi.ExecuteScalar<long>(count) - before;
    }
}
