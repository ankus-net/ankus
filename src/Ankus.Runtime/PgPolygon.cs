using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Ankus;

/// <summary>
/// Owns an immutable ordered sequence of polygon vertices and its PostgreSQL-compatible bounding box.
/// Empty polygons have a zero bounding box and can be constructed without backend access.
/// </summary>
public sealed partial class PgPolygon : IReadOnlyList<PgPoint>
{
    private readonly PgPoint[] _points;

    /// <summary>
    /// Copies the vertices and computes coordinate extrema using PostgreSQL floating-point ordering.
    /// </summary>
    /// <param name="points">The ordered polygon vertices.</param>
    public PgPolygon(IEnumerable<PgPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = [.. points];
        PgGeometry.ValidateCount(_points.Length);
        if (_points.Length == 0)
        {
            return;
        }

        PgPoint low = _points[0];
        PgPoint high = low;
        foreach (PgPoint point in _points.AsSpan(1))
        {
            low = new(PgGeometry.Compare(point.X, low.X) < 0 ? point.X : low.X,
                PgGeometry.Compare(point.Y, low.Y) < 0 ? point.Y : low.Y);
            high = new(PgGeometry.Compare(point.X, high.X) > 0 ? point.X : high.X,
                PgGeometry.Compare(point.Y, high.Y) > 0 ? point.Y : high.Y);
        }

        BoundingBox = new(high, low);
    }

    /// <summary>
    /// Gets the minimum bounding rectangle, or the zero box for an empty polygon.
    /// </summary>
    public PgBox BoundingBox { get; }

    /// <summary>
    /// Gets the number of vertices.
    /// </summary>
    public int Count => _points.Length;

    /// <summary>
    /// Gets the vertices without exposing mutable storage.
    /// </summary>
    public ReadOnlySpan<PgPoint> Points => _points;

    /// <summary>
    /// Gets a vertex by its zero-based index.
    /// </summary>
    /// <param name="index">The vertex index.</param>
    /// <returns>The point.</returns>
    public PgPoint this[int index] => _points[index];

    /// <summary>
    /// Parses a polygon using PostgreSQL on the active backend.
    /// </summary>
    /// <param name="text">The polygon text.</param>
    /// <returns>The owned polygon.</returns>
    public static PgPolygon Parse(string text) => PgGeometry.Parse<PgPolygon>(text);

    /// <summary>
    /// Tries PostgreSQL parsing without swallowing backend-access or operational errors.
    /// </summary>
    /// <param name="text">The polygon text.</param>
    /// <param name="value">The result, or null.</param>
    /// <returns>Whether parsing succeeded.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PgPolygon? value) => PgGeometry.TryParse(text, out value);

    /// <summary>
    /// Formats vertices without backend access.
    /// </summary>
    /// <returns>The polygon text.</returns>
    public override string ToString() => PgGeometry.FormatPoints(Points, '(', ')');

    /// <summary>
    /// Enumerates vertices in order.
    /// </summary>
    /// <returns>The vertex enumerator.</returns>
    public IEnumerator<PgPoint> GetEnumerator() => ((IEnumerable<PgPoint>)_points).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
