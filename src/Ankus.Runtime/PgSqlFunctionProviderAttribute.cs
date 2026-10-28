using System;

namespace Ankus;

/// <summary>
/// Declares a function created by a custom SQL block for named selection and extension attachment.
/// </summary>
/// <remarks>
/// The signature is authored SQL, such as app.calculate(integer, text), and must identify an existing function after the block runs.
/// Use explicit schema qualification when the block installs into a fixed schema. PostgreSQL validates the signature when executing the selected script.
/// </remarks>
/// <param name="sqlId">The identifier of a PgSql or PgSqlFile block.</param>
/// <param name="signature">The SQL function name and argument type list, without a FUNCTION keyword.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class PgSqlFunctionProviderAttribute(string sqlId, string signature) : Attribute
{
    /// <summary>
    /// Gets the identifier of the block that creates the function.
    /// </summary>
    public string SqlId { get; } = sqlId;

    /// <summary>
    /// Gets the authored SQL identity used after ALTER EXTENSION ADD FUNCTION.
    /// </summary>
    public string Signature { get; } = signature;
}
