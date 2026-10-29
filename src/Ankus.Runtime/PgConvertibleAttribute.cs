using System;

namespace Ankus;

/// <summary>
/// Requires a type argument, or each <c>typeof</c> type passed to a <see cref="Type"/> parameter, to have a PostgreSQL
/// conversion. The compiler reports a closed type without one, such as <see cref="ulong"/>, as error ANKUS541.
/// </summary>
/// <remarks>
/// SPI parameters and results, datum reads and writes, and function arguments use this contract. A type argument of
/// <see cref="PgRange{T}"/> or the <see cref="PgRange"/> factory must instead be a supported range bound, and ANKUS542
/// reports one that is not. Apply the attribute to a helper's own type parameter to check its callers the same way.
/// Open type parameters, <see cref="object"/>, interfaces and base classes of mapped types resolve their conversion
/// at run time.
/// </remarks>
[AttributeUsage(AttributeTargets.GenericParameter | AttributeTargets.Parameter, Inherited = false)]
public sealed class PgConvertibleAttribute : Attribute;
