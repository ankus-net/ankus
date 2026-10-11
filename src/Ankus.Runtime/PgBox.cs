namespace Ankus;

/// <summary>
/// An axis-aligned PostgreSQL box with normalized high and low corners. NaN sorts above finite values and infinity.
/// Equality compares corners exactly rather than using PostgreSQL's area-based equality operator.
/// </summary>
public readonly partial record struct PgBox
{
    /// <summary>
    /// Normalizes two corners using PostgreSQL floating-point ordering, preserving equal-coordinate bit patterns.
    /// </summary>
    /// <param name="first">The first corner, used as the high corner when coordinates compare equal.</param>
    /// <param name="second">The second corner.</param>
    public PgBox(PgPoint first, PgPoint second)
    {
        bool swapX = PgGeometry.Compare(first.X, second.X) < 0;
        bool swapY = PgGeometry.Compare(first.Y, second.Y) < 0;
        High = new(swapX ? second.X : first.X, swapY ? second.Y : first.Y);
        Low = new(swapX ? first.X : second.X, swapY ? first.Y : second.Y);
    }

    /// <summary>
    /// Gets the maximum coordinate in each dimension.
    /// </summary>
    public PgPoint High { get; }

    /// <summary>
    /// Gets the minimum coordinate in each dimension.
    /// </summary>
    public PgPoint Low { get; }

    /// <summary>
    /// Parses a box using PostgreSQL on the active backend.
    /// </summary>
    /// <param name="text">The box text.</param>
    /// <returns>The normalized box.</returns>
    public static PgBox Parse(string text) => PgGeometry.Parse<PgBox>(text);

    /// <summary>
    /// Tries PostgreSQL parsing without swallowing backend-access or operational errors.
    /// </summary>
    /// <param name="text">The box text.</param>
    /// <param name="value">The result, or default.</param>
    /// <returns>Whether parsing succeeded.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgBox value) => PgGeometry.TryParse(text, out value);

    /// <summary>
    /// Formats normalized corners without backend access.
    /// </summary>
    /// <returns>The box text.</returns>
    public override string ToString() => High + "," + Low;
}
