using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Captures only the managed call and return options needed to render a scalar or set dispatcher.
/// </summary>
/// <param name="Target">The fully qualified containing type and escaped method name.</param>
/// <param name="NumericPrecision">The optional validated output rescaling constraint.</param>
/// <param name="SetMode">The selected PostgreSQL set execution policy.</param>
/// <param name="Accessor">The exact special-method signature, or null for an ordinary C# call.</param>
internal sealed record MethodInvocation(string Target, NumericPrecision? NumericPrecision, int SetMode, MethodAccessor? Accessor = null)
{
    /// <summary>
    /// Detaches invocation metadata from the compiler's attributed method.
    /// </summary>
    /// <param name="method">The semantically validated static method.</param>
    /// <returns>The immutable invocation and return options.</returns>
    internal static MethodInvocation Create(IMethodSymbol method)
    {
        AttributeData? attribute = method.GetAttributes().FirstOrDefault(static item =>
            item.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute");
        return new(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".@" + method.Name,
            NumericConstraint.Read(method.GetReturnTypeAttributes()), attribute is null ? 0 : AttributeValues.Get(attribute, "SetMode", 0),
            method.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion ? MethodAccessor.Create(method) : null);
    }

    /// <summary>
    /// Renders the exact validated call without invoking C# overload resolution a second time.
    /// </summary>
    /// <param name="arguments">The converted managed argument expressions.</param>
    /// <param name="callback">The unique generated callback identity.</param>
    /// <returns>The ordinary invocation or statically bound special-method invocation.</returns>
    internal string Invoke(IEnumerable<string> arguments, string callback)
        => Accessor is null ? Target + "(" + string.Join(", ", arguments) + ")" :
            callback + "_call(default!, " + string.Join(", ", arguments) + ")";

    /// <summary>
    /// Emits a direct static bridge for special methods that C# cannot call by metadata name.
    /// </summary>
    /// <param name="callback">The unique generated callback identity.</param>
    /// <param name="source">The managed dispatcher source.</param>
    internal void AppendAccessor(string callback, StringBuilder source)
    {
        if (Accessor is null)
        {
            return;
        }

        source.AppendLine("    [global::System.Runtime.CompilerServices.UnsafeAccessor(");
        source.AppendLine("        global::System.Runtime.CompilerServices.UnsafeAccessorKind.StaticMethod, Name = \"" + Accessor.Name + "\")]");
        source.AppendLine("    private static extern " + Accessor.Result + " " + callback + "_call(" + Accessor.Owner + " declaringType, " +
            string.Join(", ", Accessor.Parameters.Select(static (type, index) => type + " argument" + index.ToString(CultureInfo.InvariantCulture))) + ");");
        source.AppendLine();
    }
}

/// <summary>
/// Holds only the exact metadata signature required for a compiler-bound static call.
/// </summary>
/// <param name="Owner">The accessible declaring type.</param>
/// <param name="Name">The exact metadata name, including checked-operator distinctions.</param>
/// <param name="Result">The exact managed result type, including conversion overload distinctions.</param>
/// <param name="Parameters">The exact ordered managed argument types.</param>
internal sealed record MethodAccessor(string Owner, string Name, string Result, EquatableArray<string> Parameters)
{
    /// <summary>
    /// Preserves reference nullability at every level of the compiler-validated signature.
    /// </summary>
    private static readonly SymbolDisplayFormat s_signature = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>
    /// Freezes one validated operator or conversion signature without compiler objects.
    /// </summary>
    /// <param name="method">The transient attributed special method.</param>
    /// <returns>The equatable static binding contract.</returns>
    internal static MethodAccessor Create(IMethodSymbol method)
        => new(method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), method.MetadataName,
            method.ReturnType.ToDisplayString(s_signature),
            new(method.Parameters.Select(static parameter => parameter.Type.ToDisplayString(s_signature))));
}
