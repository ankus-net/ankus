namespace Ankus;

/// <summary>
/// An ordered pair of endpoints representing PostgreSQL lseg. Equality preserves endpoint order.
/// </summary>
/// <param name="Start">The first endpoint.</param>
/// <param name="End">The second endpoint.</param>
public readonly partial record struct PgLineSegment(PgPoint Start, PgPoint End)
{
    /// <summary>
    /// Parses a segment using PostgreSQL on the active backend.
    /// </summary>
    /// <param name="text">The segment text.</param>
    /// <returns>The parsed segment.</returns>
    public static PgLineSegment Parse(string text) => PgGeometry.Parse<PgLineSegment>(text);

    /// <summary>
    /// Tries PostgreSQL parsing without swallowing backend-access or operational errors.
    /// </summary>
    /// <param name="text">The segment text.</param>
    /// <param name="value">The result, or default.</param>
    /// <returns>Whether parsing succeeded.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgLineSegment value) => PgGeometry.TryParse(text, out value);

    /// <summary>
    /// Formats the endpoints without backend access.
    /// </summary>
    /// <returns>The segment text.</returns>
    public override string ToString() => "[" + Start + "," + End + "]";
}
