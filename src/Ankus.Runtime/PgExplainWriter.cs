namespace Ankus;

/// <summary>
/// Adds properties to an EXPLAIN plan node, in every EXPLAIN format.
/// </summary>
/// <remarks>
/// The writer is valid only during the call that receives it. Text output shows each property as
/// <c>Label: value</c> under the node; JSON, XML and YAML output use the label as the property name.
/// </remarks>
public readonly ref struct PgExplainWriter
{
    private readonly List<Property> _properties;

    internal PgExplainWriter(List<Property> properties) => _properties = properties;

    /// <summary>
    /// Adds an integer property.
    /// </summary>
    /// <param name="label">The property label.</param>
    /// <param name="value">The value.</param>
    /// <param name="unit">An optional unit, such as <c>kB</c>, shown after the value in text output.</param>
    public void Add(string label, long value, string? unit = null) => _properties.Add(new(0, Check(label), CheckOptional(unit), value, 0, 0, null));

    /// <summary>
    /// Adds a floating-point property.
    /// </summary>
    /// <param name="label">The property label.</param>
    /// <param name="value">The value.</param>
    /// <param name="digits">The digits shown after the decimal point, from 0 to 15.</param>
    /// <param name="unit">An optional unit shown after the value in text output.</param>
    public void Add(string label, double value, int digits, string? unit = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 15);
        _properties.Add(new(1, Check(label), CheckOptional(unit), 0, value, digits, null));
    }

    /// <summary>
    /// Adds a text property.
    /// </summary>
    /// <param name="label">The property label.</param>
    /// <param name="value">The value.</param>
    public void Add(string label, string value) => _properties.Add(new(2, Check(label), null, 0, 0, 0, Check(value)));

    /// <summary>
    /// Adds a Boolean property.
    /// </summary>
    /// <param name="label">The property label.</param>
    /// <param name="value">The value.</param>
    public void Add(string label, bool value) => _properties.Add(new(3, Check(label), null, value ? 1 : 0, 0, 0, null));

    private static string Check(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Contains('\0', StringComparison.Ordinal)
            ? throw new ArgumentException("EXPLAIN text cannot contain a zero character.", nameof(value))
            : value;
    }

    private static string? CheckOptional(string? value) => value is null ? null : Check(value);

    /// <summary>
    /// Holds one property until the native caller writes it.
    /// </summary>
    /// <param name="Kind">0 for an integer, 1 for a floating-point value, 2 for text and 3 for a Boolean.</param>
    /// <param name="Label">The property label.</param>
    /// <param name="Unit">The optional unit.</param>
    /// <param name="Integer">The integer or Boolean value.</param>
    /// <param name="Real">The floating-point value.</param>
    /// <param name="Digits">The floating-point digits.</param>
    /// <param name="Text">The text value.</param>
    internal readonly record struct Property(int Kind, string Label, string? Unit, long Integer, double Real, int Digits, string? Text);
}
