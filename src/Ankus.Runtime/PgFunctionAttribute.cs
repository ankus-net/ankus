using System;

namespace Ankus;

/// <summary>
/// Exposes a static .NET method as a PostgreSQL function through generated native entry points and SQL.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PgFunctionAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the SQL function name. The default is the method name converted to snake_case.
    /// </summary>
    public string? Name
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets a unique, case-sensitive identifier used by installation SQL dependencies.
    /// It does not change the function's SQL name or signature.
    /// </summary>
    public string? Id
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets identifiers of SQL blocks or generated declarations that must precede this function.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Gets or sets whether installation SQL is emitted for this function and its attached operators or casts.
    /// The default is true. False retains the managed method, native entry points and dependency identifiers.
    /// Cannot be false when Sql contains a replacement, including an empty string.
    /// </summary>
    public bool GenerateSql
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Gets or sets literal installation SQL replacing this function and its attached operators or casts.
    /// Null preserves generated SQL; empty text emits no statements. Native entry points remain generated.
    /// Replaces @FUNCTION_NAME@ with the native export and @MODULE_PATHNAME@ with MODULE_PATHNAME without adding quotes.
    /// </summary>
    /// <remarks>
    /// Replacement declarations must match the managed wrapper's argument, result and NULL contracts.
    /// Explicit dependencies and type/schema prerequisites still apply. For an aggregate helper this affects only
    /// that helper; the aggregate declaration is unchanged. SQL syntax and object definitions are checked by
    /// PostgreSQL.
    /// </remarks>
    public string? Sql
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether the literal Sql replacement permits moving the extension to another schema.
    /// The default is false. This option is consulted only when Sql is non-null; fixed schemas and other
    /// non-relocatable declarations can still prevent relocation.
    /// </summary>
    public bool SqlRelocatable
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets an existing SQL schema, overriding the nearest PgSchema declaration.
    /// Declare PgSchema separately when the extension should create the schema.
    /// A fixed schema makes the extension non-relocatable.
    /// </summary>
    public string? Schema
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the planner's volatility contract. The default is Volatile.
    /// </summary>
    public PgVolatility Volatility
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether PostgreSQL may execute this function in parallel queries. The default is Unsafe.
    /// </summary>
    public PgParallelSafety ParallelSafety
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets SQL NULL dispatch behavior. The default infers strictness from SQL parameter nullability.
    /// </summary>
    public PgNullInput NullInput
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether the function executes with its owner's privileges; otherwise it uses the caller's.
    /// </summary>
    public bool SecurityDefiner
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the claim that the function reveals no argument information except through its result.
    /// PostgreSQL requires superuser privileges to install a leakproof function.
    /// </summary>
    public bool Leakproof
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether installation uses CREATE OR REPLACE FUNCTION.
    /// </summary>
    public bool CreateOrReplace
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the positive finite planner cost in cpu_operator_cost units. The C-language default is one.
    /// </summary>
    public double Cost
    {
        get;
        set;
    } = 1;

    /// <summary>
    /// Gets or sets the positive finite estimated row count for a set-returning function. The default is 1000.
    /// This option is only valid for IEnumerable returns.
    /// </summary>
    public double Rows
    {
        get;
        set;
    } = 1000;

    /// <summary>
    /// Gets or sets how a set-returning function produces rows. Auto prefers one row per call.
    /// This option is only valid for IEnumerable returns.
    /// </summary>
    public PgSetMode SetMode
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets an ordered schema search path scoped to this function. Null preserves the caller's search path.
    /// Each entry is a schema identifier, including the special $user and pg_temp entries.
    /// Use <see cref="PgSearchPath.ExtensionSchema"/> for the schema selected during CREATE EXTENSION.
    /// Such a generated search path makes the extension non-relocatable.
    /// </summary>
    public string[]? SearchPath
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the name of an existing planner support function, optionally qualified with one schema.
    /// PostgreSQL resolves and validates its internal-to-internal signature during installation.
    /// </summary>
    public string? SupportFunction
    {
        get;
        set;
    }
}
