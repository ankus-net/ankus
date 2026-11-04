using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises generated calls and serialization across C# assembly-visible declarations.
/// </summary>
/// <param name="count">The stored count.</param>
[PgSchema("assembly_access")]
[PgType(Name = "value")]
public partial class AssemblyAccessValue(int count)
{
    /// <summary>
    /// Gets the count restored by the generated constructor call.
    /// </summary>
    public int Count { get; } = count;

    /// <summary>
    /// Gets a setting whose generated partial declaration must preserve assembly access.
    /// </summary>
    [PgGucInt("ankus_access.limit", 7, "Assembly accessibility probe", Show = nameof(ShowLimit))]
    protected internal static partial int Limit { get; }

    /// <summary>
    /// Reports the setting through a protected-internal callback.
    /// </summary>
    /// <param name="current">The current setting.</param>
    /// <param name="extra">The unused check-hook payload.</param>
    /// <returns>The independently observable hook result.</returns>
    protected internal static string ShowLimit(int current, PgGucExtra? extra)
    {
        _ = extra;
        return "limit=" + current.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the generated setting from an assembly-visible function.
    /// </summary>
    /// <returns>The underlying numeric setting.</returns>
    [PgFunction]
    protected internal static int AssemblyAccessRead() => Limit;

    /// <summary>
    /// Restores a stored custom value and returns a newly serialized value.
    /// </summary>
    /// <param name="value">The typed PostgreSQL input.</param>
    /// <returns>The count increased by the current setting.</returns>
    [PgFunction]
    protected internal static AssemblyAccessValue AssemblyAccessIncrement(AssemblyAccessValue value)
        => new(checked(value.Count + Limit));

    /// <summary>
    /// Carries a nested type visible to generated code through its assembly access.
    /// </summary>
    /// <param name="Number">The exact integer.</param>
    /// <param name="Label">Optional owned text.</param>
    [PgType(Name = "payload")]
    protected internal sealed record Payload(int Number, string? Label);

    /// <summary>
    /// Exchanges a nested custom value and SQL NULL through native dispatch.
    /// </summary>
    /// <param name="value">The value or SQL NULL.</param>
    /// <returns>The unchanged value.</returns>
    [PgFunction]
    protected internal static Payload? AssemblyAccessPayload(Payload? value) => value;
}
