using System;

namespace Ankus;

/// <summary>
/// Declares a configuration prefix to check after native settings register and before managed initialization.
/// </summary>
/// <remarks>
/// PostgreSQL 15 and later remove matching unknown placeholders and reserve the literal first component
/// against new placeholders. PostgreSQL 13 and 14 only warn about existing matching placeholders.
/// Matching is case sensitive. A dotted prefix can match existing placeholders but does not reserve
/// future first components. Empty prefixes are retained with PostgreSQL's native behavior.
/// </remarks>
/// <param name="prefix">The exact prefix text, without embedded zero characters.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class PgGucPrefixAttribute(string prefix) : Attribute
{
    /// <summary>
    /// Gets the literal configuration prefix passed to PostgreSQL.
    /// </summary>
    public string Prefix { get; } = prefix;
}
