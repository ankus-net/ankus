using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Compiler support for SQL interpolation that binds values with their declared managed types.
/// Each interpolation is a bound value that is never formatted into the text, so it cannot supply an identifier or
/// SQL fragment.
/// </summary>
/// <param name="literalLength">The compiler's literal character count.</param>
/// <param name="formattedCount">The compiler's interpolated value count.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
[InterpolatedStringHandler]
public readonly ref struct SpiSqlInterpolatedStringHandler(int literalLength, int formattedCount)
{
    private readonly StringBuilder? _command = new(literalLength);
    private readonly List<SpiParameter>? _parameters = new(formattedCount);
    private readonly List<int>? _positions = new(formattedCount);

    /// <summary>
    /// Gets the initialized SQL builder, rejecting a default handler before any backend operation.
    /// </summary>
    private StringBuilder Command => _command ?? throw new InvalidOperationException("The SQL interpolation handler is not initialized.");

    /// <summary>
    /// Gets the initialized declared bindings.
    /// </summary>
    private List<SpiParameter> Parameters => _parameters ?? throw new InvalidOperationException("The SQL interpolation handler is not initialized.");

    /// <summary>
    /// Gets the dollar-token positions owned by this interpolation builder.
    /// </summary>
    private List<int> Positions => _positions ?? throw new InvalidOperationException("The SQL interpolation handler is not initialized.");

    /// <summary>
    /// Appends literal SQL without changing its quoting or statement structure.
    /// </summary>
    /// <param name="value">The compiler-provided SQL literal.</param>
    public void AppendLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Command.Append(value);
    }

    /// <summary>
    /// Binds a value with the same declared type, NULL identity and conversion contract as SpiParameter.Create.
    /// </summary>
    /// <typeparam name="T">The declared managed parameter type.</typeparam>
    /// <param name="value">The value to bind without text formatting.</param>
    public void AppendFormatted<[PgConvertible] T>(T value) => AppendFormatted(SpiParameter.Create(value));

    /// <summary>
    /// Binds text or an untyped null interpolation as PostgreSQL text.
    /// </summary>
    /// <param name="value">The text value or SQL NULL.</param>
    public void AppendFormatted(string? value) => AppendFormatted(SpiParameter.Create(value));

    /// <summary>
    /// Preserves an explicitly supplied parameter, including custom, raw and composite type identity.
    /// </summary>
    /// <param name="value">The declared positional parameter.</param>
    /// <exception cref="ArgumentException">The parameter has no PostgreSQL type identity.</exception>
    public void AppendFormatted(SpiParameter value)
    {
        if (value.TypeOid == 0)
        {
            throw new ArgumentException("An interpolated SQL parameter must have a PostgreSQL type identity.", nameof(value));
        }

        Parameters.Add(value);
        Positions.Add(Command.Length + 1);
        Command.Append("($").Append(Parameters.Count.ToString(CultureInfo.InvariantCulture)).Append(')');
    }

    /// <summary>
    /// Copies SQL and its binding vector so later handler changes cannot alter the command.
    /// </summary>
    /// <returns>The independent command with existing parameter-value lifetime requirements.</returns>
    internal SpiCommand Build()
    {
        string command = Command.ToString();
        SpiSqlInterpolation.Validate(command, Positions);
        return new(command, [.. Parameters]);
    }
}
