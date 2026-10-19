namespace Ankus;

/// <summary>
/// Resolves PostgreSQL type names through the server's native regtype input function.
/// </summary>
public static class PgTypes
{
    /// <summary>
    /// Resolves PostgreSQL type syntax in the current search path without caching its identity.
    /// </summary>
    /// <param name="typeName">Native regtype syntax, including quoted names, aliases, arrays, numeric OIDs, or a dash.</param>
    /// <returns>The native OID. A dash or numeric zero returns zero; numeric input need not identify an existing type.</returns>
    /// <remarks>
    /// PostgreSQL ignores type modifiers during this lookup. Missing or malformed named types raise PgException.
    /// Null, embedded zero characters and invalid UTF-16 are rejected before entering the backend.
    /// </remarks>
    public static uint GetOid(string typeName) => NativeBackend.ResolveTypeName(typeName);

    /// <summary>
    /// Resolves the short CLR metadata name of a statically selected type through PostgreSQL's regtype parser.
    /// </summary>
    /// <typeparam name="T">The managed type whose name is supplied verbatim.</typeparam>
    /// <returns>The resolved PostgreSQL OID.</returns>
    /// <remarks>
    /// This is a name-based convenience corresponding to pgrx's rust_regtypein helper.
    /// It ignores generated SQL type mappings.
    /// Namespace and declaring-type qualifiers are omitted; generic arity and array suffixes remain in Type.Name.
    /// PostgreSQL applies its normal unquoted-name rules. SQL renaming, declared schemas and CLR primitive aliases are not inferred.
    /// </remarks>
    public static uint GetOidByManagedName<T>() => GetOid(typeof(T).Name);
}
