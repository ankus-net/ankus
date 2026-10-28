using System;
using System.Text;

namespace Ankus;

/// <summary>
/// Identifies one statically discovered backend test without invoking its managed method in the host process.
/// </summary>
/// <param name="name">The original managed test name used in reports.</param>
/// <param name="schema">The explicit SQL schema, or null for the installed extension schema.</param>
/// <param name="functionName">The generated zero-argument SQL function name.</param>
/// <param name="expectedError">The exact expected server error message, or null for success.</param>
/// <param name="ignoreReason">The host framework's ignore reason, or null to run the test.</param>
public sealed class PgTestCase(string name, string? schema, string functionName, string? expectedError = null, string? ignoreReason = null)
{
    /// <summary>
    /// Gets the original managed test name.
    /// </summary>
    public string Name { get; } = ValidateText(name, nameof(name), identifier: false);

    /// <summary>
    /// Gets the explicit schema, or null for the installed extension schema.
    /// </summary>
    public string? Schema { get; } = schema is null ? null : ValidateText(schema, nameof(schema), identifier: true);

    /// <summary>
    /// Gets the generated SQL function name.
    /// </summary>
    public string FunctionName { get; } = ValidateText(functionName, nameof(functionName), identifier: true);

    /// <summary>
    /// Gets the exact expected PostgreSQL primary message, or null for successful execution.
    /// </summary>
    public string? ExpectedError { get; } = expectedError is null ? null : ValidateText(expectedError, nameof(expectedError), identifier: false, allowEmpty: true);

    /// <summary>
    /// Gets the reason the host framework should report this case as ignored, or null to run it.
    /// </summary>
    public string? IgnoreReason { get; } = ignoreReason is null ? null : ValidateText(ignoreReason, nameof(ignoreReason), identifier: false);

    /// <summary>
    /// Returns the managed test name for ordinary data-driven test reports.
    /// </summary>
    /// <returns>The stable managed test name.</returns>
    public override string ToString() => Name;

    private static string ValidateText(string value, string parameter, bool identifier, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if ((!allowEmpty && value.Length == 0) || !identifier && !allowEmpty && string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            throw new ArgumentException("Backend test metadata must contain valid text without zero characters.", parameter);
        }

        int length;
        try
        {
            length = new UTF8Encoding(false, true).GetByteCount(value);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("Backend test metadata must contain valid Unicode.", parameter, error);
        }

        if (identifier && length > 63)
        {
            throw new ArgumentException("A PostgreSQL identifier cannot exceed 63 UTF-8 bytes.", parameter);
        }

        return value;
    }
}
