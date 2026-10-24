namespace Ankus.TestExtension;

/// <summary>
/// Takes raw datum arguments, so a test can observe the backend work that reading an argument starts.
/// </summary>
[PgSchema("argument_reads")]
public static class ArgumentReadFunctions
{
    /// <summary>
    /// Reads a <c>name</c> argument as a raw datum, as pglogical's functions take relation and set names.
    /// </summary>
    /// <param name="value">The argument.</param>
    /// <returns>Whether the argument has the <c>name</c> type.</returns>
    [PgFunction]
    public static bool TakesName([PgSqlType("name", Schema = "pg_catalog")] PgDatum value) => value.TypeOid == 19;

    /// <summary>
    /// Reads a <c>text</c> argument as a raw datum and returns the length of the copied, detoasted value.
    /// </summary>
    /// <param name="value">The argument, which may be compressed or stored out of line.</param>
    /// <returns>The value's length in bytes including its four-byte header.</returns>
    [PgFunction]
    public static unsafe int TakesText([PgSqlType("text", Schema = "pg_catalog")] PgDatum value)
    {
        // A detoasted value has a four-byte header whose upper 30 bits hold its total length on little-endian targets.
        uint header = *(uint*)value.DangerousGetBits();
        return checked((int)(header >> 2));
    }
}
