using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Constant defaults retain the sign of zero in both value equality and the generated PostgreSQL expression.
    /// </summary>
    /// <param name="managed">The authored floating-point type.</param>
    /// <param name="sql">The matching PostgreSQL cast.</param>
    /// <param name="zero">The positive-zero C# constant.</param>
    /// <param name="negativeZero">The negative-zero C# constant.</param>
    [TestMethod]
    [DataRow("float", "real", "0.0f", "-0.0f")]
    [DataRow("double", "double precision", "0.0", "-0.0")]
    public void ParameterModelsPreserveFloatingDefaultSign(string managed, string sql, string zero, string negativeZero)
    {
        string source = $"public static class Functions {{ [Ankus.PgFunction] public static {managed} Value({managed} value = {zero}) => value; }}";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(source.Replace("= " + zero, "= " + negativeZero, StringComparison.Ordinal),
                path: "Module.cs", cancellationToken: context.CancellationToken));
        _ = RunModule(driver, edited, out Compilation second);

        Assert.AreNotEqual(DefaultParameter(initial), DefaultParameter(edited));
        Assert.Contains("DEFAULT (E'0'::" + sql + ")", InstallationBody(first));
        Assert.Contains("DEFAULT (E'-0'::" + sql + ")", InstallationBody(second));
    }

    /// <summary>
    /// Unfinished numeric attributes produce ordinary diagnostics and recover without crashing eager parameter analysis.
    /// </summary>
    /// <param name="arguments">The unfinished or malformed constructor arguments.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("12, \"bad\"")]
    [DataRow("\"bad\", 2")]
    public void MalformedNumericMetadataReportsErrorsAndRecovers(string arguments)
    {
        string source = $"public static class Functions {{ [Ankus.PgFunction] public static decimal Value([Ankus.PgNumericPrecision({arguments})] decimal value) => value; }}";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.Contains(static error => error.Id == "ANKUS003", errors);
        Assert.DoesNotContain(static error => error.Id == "CS8785", errors);

        CSharpCompilation repaired = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(source.Replace("PgNumericPrecision(" + arguments + ")", "PgNumericPrecision(12, 2)", StringComparison.Ordinal),
                path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out _);
        string generated = string.Join("\n", Assert.ContainsSingle(driver.GetRunResult().Results).GeneratedSources.Select(static value => value.SourceText.ToString()));
        Assert.Contains(".ReadNumeric().Rescale(12, 2).ToDecimal()", generated);
    }

    /// <summary>
    /// Resolves the authored method through compiler symbols before extracting its detached parameter contract.
    /// </summary>
    /// <param name="compilation">The compilation containing the defaulted function.</param>
    /// <returns>The immutable parameter used by the production generator.</returns>
    private static FunctionParameter DefaultParameter(CSharpCompilation compilation)
    {
        INamedTypeSymbol owner = compilation.GetTypeByMetadataName("Functions") ?? throw new InvalidOperationException("Missing authored type.");
        var method = (IMethodSymbol)Assert.ContainsSingle(owner.GetMembers("Value"));
        return Assert.ContainsSingle(FunctionParameter.Create(method));
    }
}
