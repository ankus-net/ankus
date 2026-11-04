using System.Diagnostics;
using System.Globalization;

namespace Ankus.Examples.TryCatch;

/// <summary>
/// Ports pgrx's <c>PgTryBuilder</c> examples to C# <c>try</c>, exception-filtered <c>catch</c> and <c>finally</c> blocks.
/// </summary>
/// <remarks>
/// <c>catch_when(code, ...)</c> becomes a <see cref="PgException"/> filter on <see cref="PgException.SqlState"/>,
/// <c>catch_rust_panic</c> becomes a filter for managed exceptions, <c>rethrow()</c> becomes <c>throw;</c>, and
/// <c>finally</c> runs after the selected catch block, including when that block rethrows. An error raised by
/// PostgreSQL itself is recoverable only after rollback, so the protected backend work runs inside
/// <see cref="PgTransaction.RunInSubtransaction{TResult}(Func{TResult})"/>.
/// </remarks>
public static class TryCatchFunctions
{
    /// <summary>
    /// Returns numbers from 42 upward and rethrows the range error raised for smaller numbers.
    /// </summary>
    /// <param name="i">The number to validate.</param>
    /// <returns>The validated number.</returns>
    [PgFunction]
    public static int IsValidNumber(int i)
    {
        int result;
        bool finished;
        try
        {
            if (i < 42)
            {
                throw new PgException(PgSqlStates.NumericValueOutOfRange, "number too small");
            }

            result = i;
        }
        catch (PgException error) when (error.SqlState == PgSqlStates.NumericValueOutOfRange)
        {
            // Mirrors catch_when(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE, |cause| cause.rethrow()).
            throw;
        }
        finally
        {
            finished = true;
        }

        Debug.Assert(finished, "The finally block runs before control leaves the protected region.");
        return result;
    }

    /// <summary>
    /// Opens a relation by OID and returns its name, or a fallback description when PostgreSQL cannot open it.
    /// </summary>
    /// <param name="oid">The relation OID.</param>
    /// <returns>The relation name, or <c>&lt;oid is not a relation&gt;</c>.</returns>
    [PgFunction]
    public static string GetRelationName(uint oid)
    {
        try
        {
            // relation_open raises XX000 for an OID that is not a relation. The subtransaction rolls back that
            // backend error before the catch block continues, and disposal closes an opened relation.
            return PgTransaction.RunInSubtransaction(() =>
            {
                using PgRelation relation = PgRelation.Open(oid);
                return relation.Name;
            });
        }
        catch (PgException error) when (error.SqlState == PgSqlStates.InternalError)
        {
            // Like pgrx's example, this is for demonstration: ignoring internal errors is rarely appropriate.
            return string.Create(CultureInfo.InvariantCulture, $"<{oid} is not a relation>");
        }
        finally
        {
            PgLog.Warning("FINALLY!");
        }
    }

    /// <summary>
    /// Optionally throws a managed exception, then either reports it as warnings or lets it fail the statement.
    /// </summary>
    /// <param name="panic">Whether the protected block throws.</param>
    /// <param name="trapIt">Whether the managed exception is caught instead of reaching PostgreSQL.</param>
    /// <param name="message">Text included in the exception message.</param>
    [PgFunction]
    public static void MaybePanic(bool panic, bool trapIt, string message)
    {
        try
        {
            if (panic)
            {
                throw new InvalidOperationException($"panic says: {message}");
            }
        }
        catch (Exception error) when (trapIt && error is not (PgException or PgQueryCanceledException))
        {
            // pgrx reports its internal panic report first; a .NET exception's type and message identify it.
            PgLog.Warning($"{error.GetType().FullName}: {error.Message}");
            PgLog.Warning(error.Message);
        }
        finally
        {
            // Runs after a trapped exception, and before an untrapped exception becomes a PostgreSQL error.
            PgLog.Warning("FINALLY!");
        }
    }
}
