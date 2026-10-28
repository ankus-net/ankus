using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Dispatches a closed aggregate capability through its static interface contract.
/// </summary>
/// <param name="Implementation">The exact closed implementing aggregate spelling.</param>
/// <param name="Interface">The closed static capability spelling.</param>
/// <param name="Method">The interface method name.</param>
/// <param name="Result">The exact nullable-aware return spelling.</param>
/// <param name="Parameters">The ordered nullable-aware argument spellings, including context.</param>
/// <param name="AllowsRefStruct">Whether constrained calls allow a ref struct implementation.</param>
/// <param name="Arguments">The reconstructed managed argument groups after context.</param>
/// <param name="Identity">The exact existing assembly-independent callback identity.</param>
internal sealed record AggregateInvocation(string Implementation, string Interface, string Method, string Result,
    EquatableArray<string> Parameters, bool AllowsRefStruct, EquatableArray<AggregateArgument> Arguments, string Identity)
{
    /// <summary>
    /// Freezes a validated constrained capability call without retaining compiler symbols.
    /// </summary>
    /// <param name="implementation">The aggregate implementing the capability.</param>
    /// <param name="contract">The closed static interface method.</param>
    /// <param name="arguments">Managed argument groups after context.</param>
    /// <returns>The equatable exact call contract.</returns>
    internal static AggregateInvocation Create(INamedTypeSymbol implementation, IMethodSymbol contract, IEnumerable<AggregateArgument> arguments)
        => new(AggregateArgument.Format(implementation), AggregateArgument.Format(contract.ContainingType), contract.Name,
            AggregateArgument.Format(contract.ReturnType), new(contract.Parameters.Select(static parameter => AggregateArgument.Format(parameter.Type))),
            implementation.IsRefLikeType, new(arguments), implementation.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ":" + contract.ToDisplayString());

    /// <summary>
    /// Emits a constrained call that supports both implicit and private explicit interface implementations.
    /// </summary>
    internal void Emit(StringBuilder managed, string callback)
    {
        string parameters = string.Join(", ", Parameters.Select((parameter, index) =>
            parameter + " value" + index.ToString(CultureInfo.InvariantCulture)));
        managed.AppendLine("    private static " + Result + " " + callback + "_invoke<TImplementation>(" + parameters + ")");
        managed.AppendLine("        where TImplementation : " + Interface + (AllowsRefStruct ? ", allows ref struct" : string.Empty));
        managed.AppendLine("        => TImplementation." + Method + "(" + string.Join(", ",
            Enumerable.Range(0, Parameters.Count).Select(static index => "value" + index.ToString(CultureInfo.InvariantCulture))) + ");");
        managed.AppendLine();
    }

    /// <summary>
    /// Calls the closed generated dispatcher with reconstructed managed arguments.
    /// </summary>
    internal string Read(string callback, IReadOnlyList<string> values)
        => callback + "_invoke<" + Implementation + ">(context" + string.Concat(Arguments.Select(argument => ", " + argument.Read(values))) + ")";
}
