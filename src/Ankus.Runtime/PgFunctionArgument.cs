using System;

namespace Ankus;

/// <summary>
/// Supplies a typed function argument, SQL NULL, or a request for its declared default.
/// </summary>
public readonly struct PgFunctionArgument
{
    /// <summary>
    /// Creates a validated argument envelope.
    /// </summary>
    /// <param name="parameter">The typed value or default type.</param>
    /// <param name="isDefault">Whether PostgreSQL should evaluate the declared default.</param>
    private PgFunctionArgument(SpiParameter parameter, bool isDefault)
    {
        Parameter = parameter;
        IsDefault = isDefault;
    }

    /// <summary>
    /// Gets the PostgreSQL type used to resolve the function, including for NULL and default arguments.
    /// </summary>
    public uint TypeOid => Parameter.TypeOid;

    /// <summary>
    /// Gets whether this argument requests the function's declared default expression.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets the typed transport value.
    /// </summary>
    internal SpiParameter Parameter { get; }

    /// <summary>
    /// Supplies a value using its declared managed type. Nullable values preserve SQL NULL.
    /// </summary>
    /// <typeparam name="T">The managed argument type.</typeparam>
    /// <param name="value">The argument value.</param>
    /// <returns>The typed argument.</returns>
    public static PgFunctionArgument Create<T>(T value) => new(SpiParameter.Create(value), false);

    /// <summary>
    /// Supplies an existing typed parameter, including explicit composite descriptors and raw datums.
    /// </summary>
    /// <param name="parameter">The parameter to bind.</param>
    /// <returns>The typed argument.</returns>
    public static PgFunctionArgument Create(SpiParameter parameter)
    {
        ArgumentOutOfRangeException.ThrowIfZero(parameter.TypeOid);
        return new PgFunctionArgument(parameter, false);
    }

    /// <summary>
    /// Requests a declared default, using the managed type to resolve overloaded functions.
    /// </summary>
    /// <typeparam name="T">The default argument's expected managed type.</typeparam>
    /// <returns>The typed default request.</returns>
    public static PgFunctionArgument Default<T>() => new(SpiParameter.CreateType(SpiType.GetOid<T>()), true);

    /// <summary>
    /// Requests a declared default using an exact PostgreSQL type identity.
    /// </summary>
    /// <param name="typeOid">The nonzero catalog type OID.</param>
    /// <returns>The typed default request.</returns>
    public static PgFunctionArgument Default(uint typeOid)
    {
        ArgumentOutOfRangeException.ThrowIfZero(typeOid);
        return new PgFunctionArgument(SpiParameter.CreateType(typeOid), true);
    }
}
