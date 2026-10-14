[assembly: PgSql("report-table-creation", """
    CREATE EVENT TRIGGER ankus_report_table_creation ON ddl_command_end
    WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION report_table_creation();
    """, Requires = ["report-table-creation-function"], Relocatable = true)]

namespace Ankus.Examples.EventTriggers;

/// <summary>
/// Reports table creation using owned PostgreSQL DDL command metadata.
/// </summary>
public static class DdlEvents
{
    /// <summary>
    /// Sends a notice for each table created by the current command, including its exact PostgreSQL identity.
    /// </summary>
    /// <param name="context">The current ddl_command_end invocation.</param>
    [PgEventTrigger]
    [PgFunction(Id = "report-table-creation-function")]
    public static void ReportTableCreation(PgEventTriggerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (PgDdlCommand command in context.GetDdlCommands().Where(static command => command.ObjectType == "table"))
        {
            PgLog.Write(PgLogLevel.Notice, $"{command.CommandTag}: {command.ObjectIdentity}");
        }
    }
}
