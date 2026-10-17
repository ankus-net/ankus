namespace Ankus.TestExtension;

/// <summary>
/// Exercises settings scopes against PostgreSQL's configuration nest levels.
/// </summary>
public static class SettingsScopeFunctions
{
    /// <summary>
    /// Changes <c>lock_timeout</c> for a callback that ends as <paramref name="outcome"/> requests.
    /// </summary>
    /// <param name="value">The scoped value.</param>
    /// <param name="outcome">
    /// <c>return</c>, <c>throw</c> for a managed exception, <c>error</c> for an uncaught SQL error, <c>caught</c> for a
    /// SQL error caught inside the callback, or <c>atomic</c> for a SQL error inside an atomic subtransaction scope.
    /// </param>
    /// <returns>The value inside, the scope's outcome, and the value afterwards, separated by vertical bars.</returns>
    [PgFunction]
    public static string SettingsScopeLockTimeout(string value, string outcome)
    {
        string inside = "none";
        string result;
        try
        {
            if (outcome == "atomic")
            {
                PgTransaction.RunInSubtransaction(() => PgSettings.Run("lock_timeout", value, () =>
                {
                    inside = LockTimeout();
                    _ = Spi.Execute("SELECT 1 / 0");
                }), PgSubtransactionMode.Atomic);
                result = "ok";
            }
            else
            {
                result = PgSettings.Run("lock_timeout", value, () =>
                {
                    inside = LockTimeout();
                    switch (outcome)
                    {
                        case "throw":
                            throw new InvalidOperationException("scoped failure");
                        case "error":
                            _ = Spi.Execute("SELECT 1 / 0");
                            break;
                        case "caught":
                            try
                            {
                                _ = Spi.Execute("SELECT 1 / 0");
                            }
                            catch (PgException exception)
                            {
                                inside += ":" + exception.SqlState + ":" + LockTimeout();
                            }

                            break;
                        default:
                            break;
                    }

                    return "ok";
                });
            }
        }
        catch (PgException exception)
        {
            result = exception.SqlState;
        }
        catch (InvalidOperationException exception)
        {
            result = exception.Message;
        }

        return inside + "|" + result + "|" + LockTimeout();
    }

    /// <summary>
    /// Nests one scope inside another, with several settings in the outer one.
    /// </summary>
    /// <returns>Each observation of <c>lock_timeout</c> and <c>statement_timeout</c>, separated by vertical bars.</returns>
    [PgFunction]
    public static string SettingsScopeNested()
    {
        List<string> seen = [];
        PgSettings.Run([new("lock_timeout", "1s"), new("statement_timeout", "2min")], () =>
        {
            seen.Add(LockTimeout() + "," + StatementTimeout());
            PgSettings.Run("lock_timeout", "3s", () => seen.Add(LockTimeout() + "," + StatementTimeout()));
            seen.Add(LockTimeout() + "," + StatementTimeout());
        });
        seen.Add(LockTimeout() + "," + StatementTimeout());
        return string.Join('|', seen);
    }

    /// <summary>
    /// Runs a scope that sets <c>work_mem</c> with a plain <c>SET</c>, which outlasts the scope.
    /// </summary>
    /// <returns><c>work_mem</c> after the scope.</returns>
    [PgFunction]
    public static string SettingsScopePlainSet()
    {
        PgSettings.Run("lock_timeout", "1s", static () => Spi.Execute("SET work_mem = '7MB'"));
        return Spi.ExecuteScalar<string>("SELECT current_setting('work_mem')")!;
    }

    /// <summary>
    /// Attempts a scope that PostgreSQL rejects before its callback runs.
    /// </summary>
    /// <param name="name">The setting's name.</param>
    /// <param name="value">The value.</param>
    /// <returns>Whether the callback ran, the SQLSTATE, and a query run afterwards, separated by vertical bars.</returns>
    [PgFunction]
    public static string SettingsScopeRejected(string name, string value)
    {
        bool ran = false;
        string state = "none";
        try
        {
            PgSettings.Run(name, value, () => ran = true);
        }
        catch (PgException exception)
        {
            state = exception.SqlState;
        }

        return (ran ? "ran" : "skipped") + "|" + state + "|" + LockTimeout();
    }

    private static string LockTimeout() => Spi.ExecuteScalar<string>("SELECT current_setting('lock_timeout')")!;

    private static string StatementTimeout() => Spi.ExecuteScalar<string>("SELECT current_setting('statement_timeout')")!;
}
