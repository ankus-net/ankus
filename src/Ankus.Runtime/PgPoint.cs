namespace Ankus;

/// <summary>
/// A PostgreSQL point with double-precision coordinates.
/// Equality uses .NET value semantics, without geometric tolerance.
/// </summary>
/// <param name="X">The horizontal coordinate.</param>
/// <param name="Y">The vertical coordinate.</param>
public readonly partial record struct PgPoint(double X, double Y)
{
    /// <summary>
    /// Parses a point using PostgreSQL on the active backend.
    /// </summary>
    /// <param name="text">The point text.</param>
    /// <returns>The parsed point.</returns>
    public static PgPoint Parse(string text) => PgGeometry.Parse<PgPoint>(text);

    /// <summary>
    /// Tries PostgreSQL parsing without swallowing backend-access or operational errors.
    /// </summary>
    /// <param name="text">The point text.</param>
    /// <param name="value">The result, or default.</param>
    /// <returns>Whether parsing succeeded.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgPoint value) => PgGeometry.TryParse(text, out value);

    /// <summary>
    /// Formats round-trippable coordinates without backend access.
    /// </summary>
    /// <returns>The point text.</returns>
    public override string ToString() => "(" + PgGeometry.Format(X) + "," + PgGeometry.Format(Y) + ")";
}
