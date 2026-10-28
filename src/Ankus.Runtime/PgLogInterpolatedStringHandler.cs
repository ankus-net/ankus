using System;
using System.Runtime.CompilerServices;

namespace Ankus;

/// <summary>
/// Formats an interpolated log message only when PostgreSQL would report its level, as pgrx's logging macros skip
/// evaluating their arguments for disabled levels.
/// </summary>
/// <remarks>
/// The C# compiler uses this handler for <see cref="PgLog.Write(PgLogLevel, ref PgLogInterpolatedStringHandler)"/>.
/// When the level is below both of PostgreSQL's reporting thresholds, no interpolated expression is evaluated and no
/// text is built. ERROR and higher levels are always formatted.
/// </remarks>
[InterpolatedStringHandler]
public ref struct PgLogInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _builder;

    /// <summary>
    /// Initializes the handler for one message, deciding whether to format it.
    /// </summary>
    /// <param name="literalLength">The number of literal characters in the interpolated string.</param>
    /// <param name="formattedCount">The number of interpolated expressions.</param>
    /// <param name="level">The reporting severity.</param>
    /// <param name="isEnabled">Receives whether the compiler should evaluate and append the interpolated expressions.</param>
    public PgLogInterpolatedStringHandler(int literalLength, int formattedCount, PgLogLevel level, out bool isEnabled)
    {
        isEnabled = level >= PgLogLevel.Error || PgLog.IsEnabled(level);
        IsEnabled = isEnabled;
        _builder = isEnabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>
    /// Gets whether the message is formatted and reported.
    /// </summary>
    internal bool IsEnabled { get; }

    /// <summary>
    /// Appends literal text.
    /// </summary>
    /// <param name="value">The literal text.</param>
    public void AppendLiteral(string value) => _builder.AppendLiteral(value);

    /// <summary>
    /// Appends a formatted value.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

    /// <summary>
    /// Appends a value with a format string.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="format">The format string.</param>
    public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);

    /// <summary>
    /// Appends a value with an alignment.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="alignment">The minimum width; negative values align left.</param>
    public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);

    /// <summary>
    /// Appends a value with an alignment and a format string.
    /// </summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="alignment">The minimum width; negative values align left.</param>
    /// <param name="format">The format string.</param>
    public void AppendFormatted<T>(T value, int alignment, string? format) => _builder.AppendFormatted(value, alignment, format);

    /// <summary>
    /// Appends characters.
    /// </summary>
    /// <param name="value">The characters.</param>
    public void AppendFormatted(scoped ReadOnlySpan<char> value) => _builder.AppendFormatted(value);

    /// <summary>
    /// Appends a string.
    /// </summary>
    /// <param name="value">The string, or null for nothing.</param>
    public void AppendFormatted(string? value) => _builder.AppendFormatted(value);

    /// <summary>
    /// Returns the formatted message and releases its buffer.
    /// </summary>
    /// <returns>The message text.</returns>
    internal string ToStringAndClear() => _builder.ToStringAndClear();
}
