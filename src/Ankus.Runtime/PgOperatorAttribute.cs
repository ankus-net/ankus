using System;

namespace Ankus;

/// <summary>
/// Exposes a static method or C# operator declaration as a PostgreSQL function and a binary or prefix operator in the function's schema.
/// Add PgFunction to customize the backing function's name, schema, or execution options.
/// </summary>
/// <remarks>
/// Declares an operator using PostgreSQL's operator punctuation syntax.
/// C# operator declarations retain their exact implementation, including checked operators.
/// </remarks>
/// <param name="name">The unqualified SQL operator name.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PgOperatorAttribute(string name) : Attribute
{
    /// <summary>
    /// Gets the operator name. A one-parameter method defines a prefix operator.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets or sets the operator with reversed operands. An unqualified name uses this operator's schema.
    /// </summary>
    public string? Commutator
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the boolean complement operator. An unqualified name uses this operator's schema.
    /// </summary>
    public string? Negator
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets a restriction selectivity function, optionally qualified with one schema.
    /// PostgreSQL validates its signature at installation.
    /// </summary>
    public string? RestrictionEstimator
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets a join selectivity function, optionally qualified with one schema.
    /// PostgreSQL validates its signature at installation.
    /// </summary>
    public string? JoinEstimator
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether this binary boolean operator supports hash joins.
    /// This is a semantic promise; compatible hash operator families are declared separately.
    /// </summary>
    public bool Hashes
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether this binary boolean operator supports merge joins.
    /// This is a semantic promise; compatible B-tree operator families are declared separately.
    /// </summary>
    public bool Merges
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the dependency identifier for the operator, independently of its backing function.
    /// </summary>
    public string? Id
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets declarations that must precede CREATE OPERATOR.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];
}
