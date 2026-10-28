using System;
using System.Diagnostics.CodeAnalysis;

namespace Ankus;

/// <summary>
/// Implements .NET's parsing contract over PostgreSQL's input function.
/// </summary>
public readonly partial record struct PgTimeTz : IParsable<PgTimeTz>
{
    /// <summary>
    /// Parses PostgreSQL text with the type's input function on the active backend thread, as
    /// <see cref="Parse(string)"/> does.
    /// </summary>
    /// <param name="s">The PostgreSQL text.</param>
    /// <param name="provider">Ignored: PostgreSQL's input function and session settings decide the syntax.</param>
    /// <returns>The parsed value.</returns>
    static PgTimeTz IParsable<PgTimeTz>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <summary>
    /// Tries to parse PostgreSQL text on the active backend thread, as <see cref="TryParse(string, out PgTimeTz)"/> does.
    /// </summary>
    /// <param name="s">The candidate text.</param>
    /// <param name="provider">Ignored: PostgreSQL's input function and session settings decide the syntax.</param>
    /// <param name="result">The parsed value, or the default on invalid input.</param>
    /// <returns>Whether the input was valid.</returns>
    static bool IParsable<PgTimeTz>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out PgTimeTz result)
        => TryParse(s, out result);
}
