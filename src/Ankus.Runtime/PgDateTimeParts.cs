namespace Ankus;

/// <summary>
/// Gives PostgreSQL's spelling of date and time field names.
/// </summary>
public static class PgDateTimeParts
{
    /// <summary>
    /// Gets the field name PostgreSQL's <c>date_part</c>, <c>extract</c> and <c>date_trunc</c> accept, such as
    /// <c>isoyear</c> or <c>dow</c>, for use as a SQL argument.
    /// </summary>
    /// <param name="part">The field.</param>
    /// <returns>The lowercase PostgreSQL name.</returns>
    public static string ToPostgresName(this PgDateTimePart part) => PgTemporal.PartName(part);
}
