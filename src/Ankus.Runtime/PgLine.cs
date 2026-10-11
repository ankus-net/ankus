namespace Ankus;

/// <summary>
/// PostgreSQL line coefficients for Ax + By + C = 0. Equality compares coefficients exactly.
/// PostgreSQL validates coefficients when converting to a datum; the all-zero default is not a valid SQL line.
/// </summary>
/// <param name="A">The horizontal coefficient.</param>
/// <param name="B">The vertical coefficient.</param>
/// <param name="C">The constant coefficient.</param>
public readonly partial record struct PgLine(double A, double B, double C)
{
    /// <summary>
    /// Parses coefficients or two points using PostgreSQL on the active backend.
    /// </summary>
    /// <param name="text">The line text.</param>
    /// <returns>The parsed line.</returns>
    public static PgLine Parse(string text) => PgGeometry.Parse<PgLine>(text);

    /// <summary>
    /// Tries PostgreSQL parsing without swallowing backend-access or operational errors.
    /// </summary>
    /// <param name="text">The line text.</param>
    /// <param name="value">The result, or default.</param>
    /// <returns>Whether parsing succeeded.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgLine value) => PgGeometry.TryParse(text, out value);

    /// <summary>
    /// Formats coefficients without backend access.
    /// </summary>
    /// <returns>The coefficient text.</returns>
    public override string ToString() => "{" + PgGeometry.Format(A) + "," + PgGeometry.Format(B) + "," + PgGeometry.Format(C) + "}";
}
