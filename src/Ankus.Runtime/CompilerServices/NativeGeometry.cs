using System;
using System.Buffers.Binary;

namespace Ankus.CompilerServices;

public unsafe partial struct NativeValue
{
    /// <summary>
    /// Reads a point from PostgreSQL's network-order double coordinates.
    /// </summary>
    /// <returns>The point.</returns>
    public readonly PgPoint ReadPoint() => ReadGeoPoint(GeometryData(16));

    /// <summary>
    /// Reads a line's three coefficients.
    /// </summary>
    /// <returns>The line.</returns>
    public readonly PgLine ReadLine()
    {
        ReadOnlySpan<byte> data = GeometryData(24);
        return new(ReadGeoDouble(data), ReadGeoDouble(data[8..]), ReadGeoDouble(data[16..]));
    }

    /// <summary>
    /// Reads an ordered pair of segment endpoints.
    /// </summary>
    /// <returns>The segment.</returns>
    public readonly PgLineSegment ReadLineSegment()
    {
        ReadOnlySpan<byte> data = GeometryData(32);
        return new(ReadGeoPoint(data), ReadGeoPoint(data[16..]));
    }

    /// <summary>
    /// Reads the high and low corners of a box.
    /// </summary>
    /// <returns>The normalized box.</returns>
    public readonly PgBox ReadBox()
    {
        ReadOnlySpan<byte> data = GeometryData(32);
        return new(ReadGeoPoint(data), ReadGeoPoint(data[16..]));
    }

    /// <summary>
    /// Reads a center point and radius.
    /// </summary>
    /// <returns>The circle.</returns>
    public readonly PgCircle ReadCircle()
    {
        ReadOnlySpan<byte> data = GeometryData(24);
        return new(ReadGeoPoint(data), ReadGeoDouble(data[16..]));
    }

    /// <summary>
    /// Copies path vertices from the borrowed binary transport into owned storage.
    /// </summary>
    /// <returns>The owned path.</returns>
    public readonly PgPath ReadPath()
    {
        PgPoint[] points = ReadGeoPoints(path: true, out bool closed);
        return new(points, closed);
    }

    /// <summary>
    /// Copies polygon vertices and recomputes the bounding box from their coordinates.
    /// </summary>
    /// <returns>The owned polygon.</returns>
    public readonly PgPolygon ReadPolygon() => new(ReadGeoPoints(path: false, out _));

    /// <summary>
    /// Copies a point to an owned binary transport buffer.
    /// </summary>
    /// <param name="value">The point.</param>
    /// <returns>The transport, which the caller must release.</returns>
    public static NativeValue FromPoint(PgPoint value) => FromGeoDoubles([value.X, value.Y]);

    /// <summary>
    /// Copies line coefficients to an owned binary transport buffer.
    /// </summary>
    /// <param name="value">The line.</param>
    /// <returns>The transport, which the caller must release.</returns>
    public static NativeValue FromLine(PgLine value) => FromGeoDoubles([value.A, value.B, value.C]);

    /// <summary>
    /// Copies segment endpoints to an owned binary transport buffer.
    /// </summary>
    /// <param name="value">The segment.</param>
    /// <returns>The transport, which the caller must release.</returns>
    public static NativeValue FromLineSegment(PgLineSegment value) => FromGeoDoubles([value.Start.X, value.Start.Y, value.End.X, value.End.Y]);

    /// <summary>
    /// Copies box corners to an owned binary transport buffer.
    /// </summary>
    /// <param name="value">The box.</param>
    /// <returns>The transport, which the caller must release.</returns>
    public static NativeValue FromBox(PgBox value) => FromGeoDoubles([value.High.X, value.High.Y, value.Low.X, value.Low.Y]);

    /// <summary>
    /// Copies circle coordinates to an owned binary transport buffer.
    /// </summary>
    /// <param name="value">The circle.</param>
    /// <returns>The transport, which the caller must release.</returns>
    public static NativeValue FromCircle(PgCircle value) => FromGeoDoubles([value.Center.X, value.Center.Y, value.Radius]);

    /// <summary>
    /// Copies path vertices and closure into an owned binary transport buffer.
    /// </summary>
    /// <param name="value">The path.</param>
    /// <returns>The transport, which the caller must release.</returns>
    public static NativeValue FromPath(PgPath value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return FromGeoPoints(value.Points, true, value.IsClosed);
    }

    /// <summary>
    /// Copies polygon vertices into an owned binary transport buffer.
    /// </summary>
    /// <param name="value">The polygon.</param>
    /// <returns>The transport, which the caller must release.</returns>
    public static NativeValue FromPolygon(PgPolygon value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return FromGeoPoints(value.Points, false, false);
    }

    private readonly ReadOnlySpan<byte> GeometryData(int length)
    {
        if (_isNull != 0 || _data == null || _length != length)
        {
            throw new InvalidOperationException("Invalid geometry transport length or NULL value.");
        }

        return new ReadOnlySpan<byte>(_data, length);
    }

    private readonly PgPoint[] ReadGeoPoints(bool path, out bool closed)
    {
        int header = path ? 5 : 4;
        if (_length < header)
        {
            throw new InvalidOperationException("Truncated geometry header.");
        }

        ReadOnlySpan<byte> data = GeometryData(_length);
        closed = path && data[0] != 0;
        int count = BinaryPrimitives.ReadInt32BigEndian(data[(path ? 1 : 0)..]);
        if (count < 0 || (long)count * 16 + header != data.Length || path && data[0] > 1)
        {
            throw new InvalidOperationException("Invalid geometry point count or closure flag.");
        }

        PgGeometry.ValidateCount(count);
        var points = new PgPoint[count];
        for (int i = 0; i < count; i++)
        {
            points[i] = ReadGeoPoint(data[(header + 16 * i)..]);
        }

        return points;
    }

    private static double ReadGeoDouble(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadDoubleBigEndian(bytes);
    private static PgPoint ReadGeoPoint(ReadOnlySpan<byte> bytes) => new(ReadGeoDouble(bytes), ReadGeoDouble(bytes[8..]));

    private static NativeValue FromGeoDoubles(ReadOnlySpan<double> values)
    {
        NativeValue result = Allocate(values.Length * 8);
        Span<byte> bytes = new(result._data, result._length);
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleBigEndian(bytes[(8 * i)..], values[i]);
        }

        return result;
    }

    private static NativeValue FromGeoPoints(ReadOnlySpan<PgPoint> points, bool path, bool closed)
    {
        PgGeometry.ValidateCount(points.Length);
        int header = path ? 5 : 4;
        NativeValue result = Allocate(header + 16 * points.Length);
        Span<byte> bytes = new(result._data, result._length);
        if (path)
        {
            bytes[0] = (byte)(closed ? 1 : 0);
        }

        BinaryPrimitives.WriteInt32BigEndian(bytes[(path ? 1 : 0)..], points.Length);
        for (int i = 0; i < points.Length; i++)
        {
            BinaryPrimitives.WriteDoubleBigEndian(bytes[(header + 16 * i)..], points[i].X);
            BinaryPrimitives.WriteDoubleBigEndian(bytes[(header + 16 * i + 8)..], points[i].Y);
        }

        return result;
    }
}
