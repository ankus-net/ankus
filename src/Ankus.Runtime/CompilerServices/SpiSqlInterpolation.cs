using System;
using System.Collections.Generic;

namespace Ankus.CompilerServices;

/// <summary>
/// Keeps interpolation-owned PostgreSQL parameters separate from literal positional tokens and quoted SQL text.
/// PostgreSQL remains responsible for parsing and planning the statement.
/// </summary>
internal static class SpiSqlInterpolation
{
    /// <summary>
    /// Validates parameter ownership and positions under both ordinary-string escape settings.
    /// </summary>
    /// <param name="command">The complete SQL text.</param>
    /// <param name="positions">The interpolation's exact generated positional-token locations.</param>
    /// <exception cref="ArgumentException">A parameter belongs to literal text, or an interpolation is inside another SQL token.</exception>
    internal static void Validate(string command, IReadOnlyList<int> positions)
    {
        SqlInterpolationFailure failure = SpiSqlTokenLayout.Check(command, positions);
        if (failure == SqlInterpolationFailure.ForeignParameter)
        {
            throw new ArgumentException("Spi.Sql owns its positional parameters. Use interpolations instead of literal $n tokens.");
        }

        if (failure == SqlInterpolationFailure.InsideSqlToken)
        {
            throw new ArgumentException("SQL interpolations must occur outside strings, quoted identifiers and comments.");
        }
    }
}
