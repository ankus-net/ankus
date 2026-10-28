using System;

namespace Ankus;

/// <summary>
/// Owns SQL text and declared positional bindings created by parameterized interpolation.
/// Native datum parameters retain their existing owner and callback lifetime requirements.
/// </summary>
public readonly struct SpiCommand
{
    private readonly string? _commandText;
    private readonly SpiParameter[]? _parameters;

    /// <summary>
    /// Takes an independent binding vector from the interpolation builder.
    /// </summary>
    /// <param name="commandText">SQL text containing positional placeholders.</param>
    /// <param name="parameters">The owned, statically typed positional bindings.</param>
    internal SpiCommand(string commandText, SpiParameter[] parameters)
    {
        _commandText = commandText;
        _parameters = parameters;
    }

    /// <summary>
    /// Gets SQL text that refers to each bound parameter value through a positional placeholder.
    /// </summary>
    /// <exception cref="InvalidOperationException">The command was not created with Spi.Sql.</exception>
    public string CommandText => _commandText ?? throw new InvalidOperationException("Create the SQL command with Spi.Sql before executing it.");

    /// <summary>
    /// Gets the ordered typed bindings. Mutable managed values and native owners follow SpiParameter's lifetime rules.
    /// </summary>
    public ReadOnlySpan<SpiParameter> Parameters => _parameters;
}
