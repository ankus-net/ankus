using System;

namespace Ankus;

/// <summary>
/// Selects a generated planner support function by its managed declaration and orders it before this function.
/// </summary>
/// <remarks>
/// The selected method must be an ordinary generated function with one SQL internal argument and a scalar SQL internal result.
/// Aggregate helpers require an aggregate invocation and cannot serve as planner support functions.
/// </remarks>
/// <param name="declaringType">The type declaring the support method.</param>
/// <param name="memberName">The support method name, normally supplied with nameof.</param>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PgSupportFunctionAttribute(Type declaringType, string memberName) : Attribute
{
    /// <summary>
    /// Gets the type declaring the support method.
    /// </summary>
    public Type DeclaringType { get; } = declaringType;

    /// <summary>
    /// Gets the support method name.
    /// </summary>
    public string MemberName { get; } = memberName;

    /// <summary>
    /// Gets or sets exact managed parameter types to select an overloaded method.
    /// The selected function must take one SQL internal argument and return SQL internal.
    /// </summary>
    public Type[]? ParameterTypes
    {
        get;
        set;
    }
}
