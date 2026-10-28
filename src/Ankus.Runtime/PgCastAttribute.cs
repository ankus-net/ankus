using System;

namespace Ankus;

/// <summary>
/// Exposes a static method or C# conversion declaration as a PostgreSQL function and a cast from its first parameter to its return type.
/// Add PgFunction to customize the backing function's name, schema, or execution options.
/// </summary>
/// <remarks>
/// Declares a cast with the selected conversion context.
/// C# implicit or explicit syntax does not select the PostgreSQL conversion context.
/// Conversions retain their exact parameter and result types, including checked implementations.
/// </remarks>
/// <param name="context">The permitted conversion context; explicit by default.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PgCastAttribute(PgCastContext context = PgCastContext.Explicit) : Attribute
{
    /// <summary>
    /// Gets where PostgreSQL may apply this cast.
    /// </summary>
    public PgCastContext Context { get; } = context;

    /// <summary>
    /// Gets or sets the dependency identifier for the cast, independently of its backing function.
    /// </summary>
    public string? Id
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets declarations that must precede CREATE CAST.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];
}
