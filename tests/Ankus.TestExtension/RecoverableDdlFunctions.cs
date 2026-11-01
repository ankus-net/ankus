using System.Globalization;
using Ankus.Postgres;

namespace Ankus.TestExtension;

/// <summary>
/// Runs failing DDL in recovery scopes while a managed relation-cache callback handles the invalidations that the
/// statement's rollback processes.
/// </summary>
[PgSchema("recoverable_ddl")]
public static partial class RecoverableDdlFunctions
{
    private static bool s_registered;
    private static int s_invalidations;

    /// <summary>
    /// Supplies the relation-cache callback through the selected header's exact signature.
    /// </summary>
    /// <remarks>
    /// PostgreSQL 18 adds <c>RelSyncCallbackFunction</c> with the same signature, and it names the shared type there.
    /// </remarks>
    [PgNativeCallback(nameof(RelationChanged))]
#if ANKUS_PG13 || ANKUS_PG14 || ANKUS_PG15 || ANKUS_PG16 || ANKUS_PG17
    private static partial RelcacheCallbackFunction RelationCallback { get; }
#else
    private static partial RelSyncCallbackFunction RelationCallback { get; }
#endif

    /// <summary>
    /// Registers the relation-cache callback once in this backend and returns the invalidations it has handled.
    /// </summary>
    /// <returns>The number of relation-cache invalidations the callback has handled.</returns>
    [PgFunction]
    public static int RecoverableDdlWatch()
    {
        if (!s_registered)
        {
            unsafe
            {
                NativeMethods.CacheRegisterRelcacheCallback(RelationCallback, 0);
            }

            s_registered = true;
        }

        return s_invalidations;
    }

    /// <summary>
    /// Runs a failing statement in a recovery scope, catches it there and continues, then lets it escape a second scope.
    /// </summary>
    /// <param name="statement">The SQL that fails after it has registered catalog invalidations.</param>
    /// <returns>
    /// The SQLSTATE caught inside the scope, a query run after it in the same scope, the SQLSTATE that escaped the
    /// second scope, and whether the callback handled invalidations during the rollbacks, separated by vertical bars.
    /// </returns>
    [PgFunction]
    public static string RecoverableDdlRun(string statement)
    {
        int before = s_invalidations;
        string inner = PgTransaction.RunInSubtransaction(() =>
        {
            string caught = Attempt(statement);
            return caught + "|" + Spi.ExecuteScalar<int>("SELECT 6 * 7").ToString(CultureInfo.InvariantCulture);
        }, PgSubtransactionMode.Recoverable);
        string escaped;
        try
        {
            PgTransaction.RunInSubtransaction(() => _ = Spi.Execute(statement), PgSubtransactionMode.Recoverable);
            escaped = "ok";
        }
        catch (PgException exception)
        {
            escaped = exception.SqlState + ":" + exception.Message;
        }

        return inner + "|" + escaped + "|" + (s_invalidations > before).ToString(CultureInfo.InvariantCulture);
    }

    private static string Attempt(string statement)
    {
        try
        {
            _ = Spi.Execute(statement);
            return "ok";
        }
        catch (PgException exception)
        {
            return exception.SqlState;
        }
    }

    private static void RelationChanged(ulong arg, uint relid)
    {
        _ = arg;
        _ = relid;
        s_invalidations++;
    }
}
