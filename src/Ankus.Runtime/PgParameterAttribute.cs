using System;

namespace Ankus;

/// <summary>
/// Overrides a generated SQL parameter's name or supplies a SQL default expression.
/// Defaults apply to SQL calls; direct C# calls retain the method's ordinary optional-argument behavior.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = true, Inherited = false)]
public sealed class PgParameterAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the C# tuple element selected within a typed aggregate argument group.
    /// Omit for an ordinary scalar parameter; each tuple element is selected by its exact managed name.
    /// </summary>
    public string? Element
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether an aggregate's trailing array input collects variadic SQL arguments.
    /// Ordinary functions declare variadic inputs with the C# params modifier.
    /// </summary>
    public bool Variadic
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the exact quoted SQL argument name. The default is the C# parameter name converted to snake_case.
    /// </summary>
    public string? Name
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets a trusted SQL default expression, overriding any C# optional default.
    /// PostgreSQL validates the expression during extension installation.
    /// </summary>
    public string? Default
    {
        get;
        set;
    }
}
