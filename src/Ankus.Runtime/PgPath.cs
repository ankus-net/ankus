using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Ankus;

/// <summary>
/// Owns an immutable ordered sequence of points and an open/closed flag for PostgreSQL path.
/// Empty paths can be constructed and exchanged even though PostgreSQL's text parser requires points.
/// </summary>
public sealed partial class PgPath : IReadOnlyList<PgPoint>
{
    private readonly PgPoint[] _points;

    /// <summary>
    /// Copies the supplied points and records whether the last point connects back to the first.
    /// </summary>
    /// <param name="points">The ordered vertices.</param>
    /// <param name="isClosed">Whether the path is closed.</param>
    public PgPath(IEnumerable<PgPoint> points, bool isClosed = false)
    {
        ArgumentNullException.ThrowIfNull(points);
        _points = [.. points];
        PgGeometry.ValidateCount(_points.Length);
        IsClosed = isClosed;
    }

    /// <summary>
    /// Gets whether the path is closed.
    /// </summary>
    public bool IsClosed { get; }

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
    /// Copies this path with a new open/closed flag.
    /// </summary>
    /// <param name="isClosed">The new flag.</param>
    /// <returns>The independent path.</returns>
    public PgPath WithClosed(bool isClosed) => new(_points, isClosed);

    /// <summary>
    /// Parses a path using PostgreSQL on the active backend.
    /// </summary>
    /// <param name="text">The path text.</param>
    /// <returns>The owned path.</returns>
    public static PgPath Parse(string text) => PgGeometry.Parse<PgPath>(text);

    /// <summary>
    /// Tries PostgreSQL parsing without swallowing backend-access or operational errors.
    /// </summary>
    /// <param name="text">The path text.</param>
    /// <param name="value">The result, or null.</param>
    /// <returns>Whether parsing succeeded.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PgPath? value) => PgGeometry.TryParse(text, out value);

    /// <summary>
    /// Formats vertices with the path's opening and closing delimiters without backend access.
    /// </summary>
    /// <returns>The path text.</returns>
    public override string ToString() => PgGeometry.FormatPoints(Points, IsClosed ? '(' : '[', IsClosed ? ')' : ']');

    /// <summary>
    /// Enumerates vertices in order.
    /// </summary>
    /// <returns>The vertex enumerator.</returns>
    public IEnumerator<PgPoint> GetEnumerator() => ((IEnumerable<PgPoint>)_points).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
