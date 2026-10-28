using System;

namespace Ankus;

/// <summary>
/// Orders a generated SQL declaration before another declaration identified by its managed type or method.
/// </summary>
/// <param name="declaringType">The type declaring the referenced SQL object or method.</param>
/// <param name="memberName">The referenced method name, normally supplied with nameof; null selects the type's SQL declaration.</param>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Method,
    AllowMultiple = true, Inherited = false)]
public sealed class PgBeforeAttribute(Type declaringType, string? memberName = null) : Attribute
{
    /// <summary>
    /// Gets the type declaring the referenced SQL object or method.
    /// </summary>
    public Type DeclaringType { get; } = declaringType;

    /// <summary>
    /// Gets the referenced method name, or null for the type's SQL declaration.
    /// </summary>
    public string? MemberName { get; } = memberName;

    /// <summary>
    /// Gets or sets exact managed parameter types to select one overloaded method.
    /// Null requires an unambiguous method name; an empty array selects a parameterless method.
    /// </summary>
    public Type[]? ParameterTypes
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the dependency ID of the declaration being ordered.
    /// Required on assembly attributes; otherwise omitted to select the attributed type or method's primary SQL declaration.
    /// </summary>
    public string? DeclarationId
    {
        get;
        set;
    }
}

