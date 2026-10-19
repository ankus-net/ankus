namespace Ankus;

/// <summary>
/// A PostgreSQL circle with double-precision center and radius. PostgreSQL validates the radius on datum conversion.
/// Equality compares the stored center and radius exactly, without geometric tolerance.
/// </summary>
/// <param name="Center">The center point.</param>
/// <param name="Radius">The radius. Negative radii cannot be stored in PostgreSQL.</param>
public readonly partial record struct PgCircle(PgPoint Center, double Radius)
{
    /// <summary>
    /// Parses a circle using PostgreSQL on the active backend.
    /// </summary>
    /// <param name="text">The circle text.</param>
    /// <returns>The parsed circle.</returns>
    public static PgCircle Parse(string text) => PgGeometry.Parse<PgCircle>(text);

    /// <summary>
    /// Tries PostgreSQL parsing without swallowing backend-access or operational errors.
    /// </summary>
    /// <param name="text">The circle text.</param>
    /// <param name="value">The result, or default.</param>
    /// <returns>Whether parsing succeeded.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgCircle value) => PgGeometry.TryParse(text, out value);

    /// <summary>
    /// Formats the center and radius without backend access.
    /// </summary>
    /// <returns>The circle text.</returns>
    public override string ToString() => "<" + Center + "," + PgGeometry.Format(Radius) + ">";
}
