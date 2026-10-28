using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Separates immutable invocation and SQL metadata from the compiler parameter that supplied it.
/// </summary>
/// <param name="Name">The authored managed parameter name.</param>
/// <param name="DeclaredTypeName">The leaf managed type name used by injection diagnostics.</param>
/// <param name="DeclaredSpecialType">The original managed type category before nullable unwrapping.</param>
/// <param name="RefKind">The exact managed parameter passing mode.</param>
/// <param name="IsParams">Whether C# packs the final vector argument.</param>
/// <param name="IsFunctionContext">Whether this injected parameter captures function-call metadata.</param>
/// <param name="Type">The immutable SQL conversion contract, or null for injection or unsupported types.</param>
/// <param name="SqlIndex">The zero-based SQL slot, or minus one for an injected context.</param>
/// <param name="HasExplicitDefaultValue">Whether C# supplies an optional-parameter constant.</param>
/// <param name="OptionalDefaultSql">The exact optional default expression, or null when it needs explicit SQL.</param>
/// <param name="SqlOptions">The ordered authored SQL parameter policies, retaining invalid duplicates.</param>
/// <param name="Numeric">The optional precision/scale constraint for conversion.</param>
internal sealed record FunctionParameter(string Name, string DeclaredTypeName, SpecialType DeclaredSpecialType, RefKind RefKind,
    bool IsParams, bool IsFunctionContext, FunctionType? Type, int SqlIndex, bool HasExplicitDefaultValue,
    string? OptionalDefaultSql, EquatableArray<SqlParameterOptions> SqlOptions, NumericPrecision? Numeric)
{
    /// <summary>
    /// Gets whether the backend supplies this parameter without consuming a SQL argument.
    /// </summary>
    internal bool IsInjected => SqlIndex < 0;

    /// <summary>
    /// Builds the ordered managed invocation parameters while assigning contiguous SQL slots.
    /// </summary>
    /// <param name="method">The method whose invocation is generated.</param>
    /// <returns>Every managed parameter, including unsupported SQL types for validation.</returns>
    internal static FunctionParameter[] Create(IMethodSymbol method)
    {
        var parameters = new FunctionParameter[method.Parameters.Length];
        int sqlIndex = 0;
        for (int index = 0; index < parameters.Length; index++)
        {
            IParameterSymbol parameter = method.Parameters[index];
            bool injected = parameter.Type is INamedTypeSymbol { Name: "PgMemoryContext" or "PgFunctionContext", Arity: 0, ContainingType: null } named &&
                named.ContainingNamespace.ToDisplayString() == "Ankus";
            FunctionType? type = injected ? null : FunctionType.Create(parameter);
            parameters[index] = new(parameter.Name, parameter.Type.Name, parameter.Type.SpecialType, parameter.RefKind,
                parameter.IsParams, injected && parameter.Type.Name == "PgFunctionContext", type, injected ? -1 : sqlIndex++,
                parameter.HasExplicitDefaultValue,
                parameter.HasExplicitDefaultValue && type is not null ? ParameterDefault.Create(parameter, type) : null,
                new(parameter.GetAttributes().Where(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgParameterAttribute")
                    .Select(SqlParameterOptions.Read)), NumericConstraint.Read(parameter.GetAttributes()));
        }

        return parameters;
    }

    /// <summary>
    /// Creates one generated SQL parameter that has no authored symbol or metadata.
    /// </summary>
    /// <param name="name">The managed and SQL parameter name.</param>
    /// <param name="type">The validated conversion contract.</param>
    /// <param name="sqlIndex">The zero-based SQL argument index.</param>
    /// <returns>The generated parameter contract.</returns>
    internal static FunctionParameter Synthetic(string name, FunctionType type, int sqlIndex)
        => new(name, type.Managed, SpecialType.None, RefKind.None, false, false, type, sqlIndex, false, null, new([]), null);

    /// <summary>
    /// Emits a managed argument expression from validated values after entry into the memory capability.
    /// </summary>
    /// <param name="borrowVarlena">Whether native-layout values may use their checked borrowed view.</param>
    /// <returns>A checked context lookup or a conversion from the parameter's SQL slot.</returns>
    internal string ReadExpression(bool borrowVarlena = false)
        => IsFunctionContext ? "functionContext" : IsInjected ? "global::Ankus.PgMemoryContext.Current" :
            ManagedConversion.Read(Type!, "arguments[" + SqlIndex.ToString(CultureInfo.InvariantCulture) + "]",
                Numeric?.Suffix ?? string.Empty, borrowVarlena);
}
