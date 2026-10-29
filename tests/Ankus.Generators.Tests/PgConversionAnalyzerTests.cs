using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies that values without a PostgreSQL conversion fail the build, as pgrx's datum bounds do, while every converted
/// type and every value decided at run time still compiles.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed partial class PgConversionAnalyzerTests(TestContext context)
{
    private const string Declarations = """
        internal enum Plain { A }
        [PgEnum] internal enum Mood { Happy }
        internal struct Unmapped;
        internal sealed class SealedValue;
        internal record OpenRecord(int Id);
        internal sealed class Converter;
        [PgDatumType("int4", typeof(Converter))] internal readonly struct Mapped;
        [PgDatumType("int4", typeof(Converter))] [PgRangeType("int4range")] internal readonly struct MappedBound;
        [PgType] internal sealed class Custom;
        [PgType] internal struct Packed;
        [PgType] internal abstract class Shape;
        internal sealed class Circle : Shape;
        internal class Base;
        [PgType] internal sealed class Derived : Base;
        """;

    private const string Helpers = """
        private static T Scalar<[PgConvertible] T>() => default!;
        private static void Bind<[PgConvertible] T>(T value) { }
        private static T ExecuteScalar<T>() => default!;
        private static void Generic<T>(T value) { SpiParameter.Create(value); Spi.ExecuteScalar<T>("SELECT 1"); Spi.ExecuteScalar<T[]>("SELECT 1"); }
        """;

    /// <summary>
    /// Every API that binds or reads a value reports the type, at the type argument or the value that inferred it.
    /// </summary>
    /// <param name="expression">The consumer expression.</param>
    /// <param name="location">The reported source text.</param>
    /// <param name="type">The reported type.</param>
    [TestMethod]
    [DataRow("Spi.Execute(Spi.Sql($\"SELECT {big}\"))", "big", "ulong")]
    [DataRow("Spi.Execute(Spi.Sql($\"\"\"SELECT {big}\"\"\"))", "big", "ulong")]
    [DataRow("session.Execute(Spi.Sql($\"SELECT {number}, {(ushort)number}\"))", "(ushort)number", "ushort")]
    [DataRow("Spi.Connect(active => active.Execute(Spi.Sql($\"SELECT {big}\")))", "big", "ulong")]
    [DataRow("Spi.Execute(\"SELECT $1\", SpiParameter.Create(big))", "big", "ulong")]
    [DataRow("SpiParameter.Create(value: big)", "big", "ulong")]
    [DataRow("SpiParameter.Create<ulong>(1)", "ulong", "ulong")]
    [DataRow("Spi.ExecuteScalar<ulong>(\"SELECT 1\")", "ulong", "ulong")]
    [DataRow("Spi.ExecuteScalar<ulong?>(Spi.Sql($\"SELECT {number}\"))", "ulong?", "ulong?")]
    [DataRow("Spi.ExecuteScalars<int, ulong>(\"SELECT 1, 2\")", "ulong", "ulong")]
    [DataRow("Spi.ExecuteScalars<int, long, char>(\"SELECT 1, 2, 3\")", "char", "char")]
    [DataRow("session.ExecuteScalar<byte>(\"SELECT 1\")", "byte", "byte")]
    [DataRow("session.ExecuteScalars<Unmapped, int>(Spi.Sql($\"SELECT {number}, 2\"))", "Unmapped", "Unmapped")]
    [DataRow("Spi.Prepare(\"SELECT 1\").ExecuteScalar<nint>()", "nint", "nint")]
    [DataRow("Spi.Prepare(\"SELECT $1\", typeof(ulong))", "ulong", "ulong")]
    [DataRow("session.Prepare(\"SELECT $1, $2\", typeof(int), typeof(Plain))", "Plain", "Plain")]
    [DataRow("Spi.Prepare(\"SELECT $1\", [typeof(ulong)])", "ulong", "ulong")]
    [DataRow("Spi.Prepare(\"SELECT $1\", new[] { typeof(ulong) })", "ulong", "ulong")]
    [DataRow("Spi.Select(\"SELECT 1\")[0].Get<ulong>(0)", "ulong", "ulong")]
    [DataRow("Spi.Select(\"SELECT 1\")[0].Get<List<int>>(\"id\")", "List<int>", "System.Collections.Generic.List<int>")]
    [DataRow("Spi.Select(\"SELECT 1\")[0].Set(0, big)", "big", "ulong")]
    [DataRow("Spi.SelectRaw(\"SELECT 1\")[0].Get<ulong>(\"id\")", "ulong", "ulong")]
    [DataRow("tuple.Get<ulong>(0)", "ulong", "ulong")]
    [DataRow("tuple.Set(\"id\", big)", "big", "ulong")]
    [DataRow("PgDatum.Create(big, PgMemoryContext.Current)", "big", "ulong")]
    [DataRow("datum.Read<ulong>()", "ulong", "ulong")]
    [DataRow("element.Read<ulong>()", "ulong", "ulong")]
    [DataRow("array.Read<ulong[]>()", "ulong[]", "ulong[]")]
    [DataRow("PgFunctionArgument.Create(big)", "big", "ulong")]
    [DataRow("PgFunctionArgument.Default<ulong>()", "ulong", "ulong")]
    [DataRow("PgFunctions.Call<ulong>(\"now\")", "ulong", "ulong")]
    [DataRow("aggregate.Compare(big, big)", "big", "ulong")]
    [DataRow("PgEnums.GetLabel(Plain.A)", "Plain.A", "Plain")]
    [DataRow("PgEnums.GetTypeOid<Plain>()", "Plain", "Plain")]
    [DataRow("Scalar<ulong>()", "ulong", "ulong")]
    [DataRow("Bind(big)", "big", "ulong")]
    [DataRow("new[] { big }.Select(SpiParameter.Create<ulong>)", "ulong", "ulong")]
    [DataRow("Func<ulong, SpiParameter> bind = SpiParameter.Create", "SpiParameter.Create", "ulong")]
    [DataRow("Func<SpiCommand, ulong> read = Spi.ExecuteScalar<ulong>", "ulong", "ulong")]
    public async Task UnconvertedValuesAreErrors(string expression, string location, string type)
        => await AssertRejected(expression, "ANKUS541", location, type);

    /// <summary>
    /// Types without a built-in, mapped or array conversion are rejected, including in arrays, views and nullable forms.
    /// </summary>
    /// <param name="type">The parameter type.</param>
    [TestMethod]
    [DataRow("ulong")]
    [DataRow("ushort")]
    [DataRow("byte")]
    [DataRow("char")]
    [DataRow("nint")]
    [DataRow("nuint")]
    [DataRow("Half")]
    [DataRow("Int128")]
    [DataRow("UInt128")]
    [DataRow("System.Numerics.BigInteger")]
    [DataRow("(int, long)")]
    [DataRow("List<int>")]
    [DataRow("int[,]")]
    [DataRow("int[][]")]
    [DataRow("ulong[]")]
    [DataRow("ulong?")]
    [DataRow("PgArray<ulong>")]
    [DataRow("PgArray<int[]>")]
    [DataRow("PgArray<PgDatum>")]
    [DataRow("PgArrayView<System.Text.Rune>")]
    [DataRow("PgArrayView<ulong>")]
    [DataRow("PgRange<short>")]
    [DataRow("PgTextView[]")]
    [DataRow("PgInternal[]")]
    [DataRow("Uri")]
    [DataRow("Exception")]
    [DataRow("Plain")]
    [DataRow("Plain[]")]
    [DataRow("Unmapped")]
    [DataRow("Unmapped?")]
    [DataRow("SealedValue")]
    [DataRow("OpenRecord")]
    [DataRow("PgVarlena<Unmapped>")]
    [DataRow("Action")]
    [DataRow("SpiResult")]
    public async Task TypesWithoutConversionsAreErrors(string type)
        => await AssertRejected("SpiParameter.Create<" + type + ">(default!)", "ANKUS541", type, null);

    /// <summary>
    /// Built-in, mapped, polymorphic and open types compile, as do base classes and interfaces a converted value can match.
    /// </summary>
    /// <param name="type">The parameter and result type.</param>
    [TestMethod]
    [DataRow("int")]
    [DataRow("int?")]
    [DataRow("string")]
    [DataRow("string?")]
    [DataRow("byte[]")]
    [DataRow("byte[][]")]
    [DataRow("int[]")]
    [DataRow("int?[]")]
    [DataRow("PgArray<int>")]
    [DataRow("PgArray<byte[]>")]
    [DataRow("PgArray<PgRange<int>>")]
    [DataRow("PgRange<long>")]
    [DataRow("PgRange<int>[]")]
    [DataRow("PgRange<MappedBound>")]
    [DataRow("PgArrayView<int>")]
    [DataRow("PgArrayView<int?>")]
    [DataRow("PgArrayView<PgTextView>")]
    [DataRow("decimal")]
    [DataRow("PgNumeric")]
    [DataRow("DateOnly")]
    [DataRow("uint")]
    [DataRow("Guid")]
    [DataRow("System.Text.Rune")]
    [DataRow("System.Net.IPAddress")]
    [DataRow("PgHeapTuple")]
    [DataRow("PgHeapTuple[]")]
    [DataRow("PgDatum")]
    [DataRow("PgAnyElement")]
    [DataRow("PgArrayView")]
    [DataRow("object")]
    [DataRow("object[]")]
    [DataRow("IComparable")]
    [DataRow("IReadOnlyList<ulong>")]
    [DataRow("ValueType")]
    [DataRow("Enum")]
    [DataRow("Array")]
    [DataRow("Mood")]
    [DataRow("Mood[]")]
    [DataRow("Mapped")]
    [DataRow("Mapped?")]
    [DataRow("Mapped[]")]
    [DataRow("PgArray<Mapped>")]
    [DataRow("Custom")]
    [DataRow("PgVarlena<Packed>")]
    [DataRow("Shape")]
    [DataRow("Circle")]
    [DataRow("Base")]
    public async Task ConvertedTypesCompile(string type)
        => Assert.IsEmpty(await Analyze("SpiParameter.Create<" + type + ">(default!); Spi.ExecuteScalar<" + type + ">(\"SELECT 1\")"));

    /// <summary>
    /// Open type parameters resolve at run time, and methods without the attribute keep their ordinary C# rules.
    /// </summary>
    /// <param name="expression">The consumer expression.</param>
    [TestMethod]
    [DataRow("Generic(big)")]
    [DataRow("ExecuteScalar<ulong>()")]
    [DataRow("Spi.Prepare(\"SELECT $1\", typeof(int), typeof(PgHeapTuple))")]
    [DataRow("Spi.Execute(Spi.Sql($\"SELECT {number}, {(long)big}, {big.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"))")]
    [DataRow("new[] { number }.Select(SpiParameter.Create<int>)")]
    [DataRow("Func<long, SpiParameter> bind = SpiParameter.Create")]
    public async Task OpenAndUnmarkedTypesCompile(string expression)
        => Assert.IsEmpty(await Analyze(expression));

    /// <summary>
    /// Range construction and factories require a PostgreSQL range bound, as pgrx's RangeSubType bound does.
    /// </summary>
    /// <param name="expression">The consumer expression.</param>
    /// <param name="location">The reported source text.</param>
    /// <param name="type">The reported type.</param>
    [TestMethod]
    [DataRow("PgRange.Create(1UL, 2UL)", "1UL", "ulong")]
    [DataRow("PgRange.Empty<short>()", "short", "short")]
    [DataRow("PgRange.Unbounded<Mapped>()", "Mapped", "Mapped")]
    [DataRow("PgRange.Parse<ulong>(\"[1,2)\")", "ulong", "ulong")]
    [DataRow("PgRange.TryParse<double>(\"[1,2)\", out _)", "double", "double")]
    [DataRow("new PgRange<ushort>(1, 2)", "ushort", "ushort")]
    [DataRow("new Ankus.PgRange<ushort>()", "ushort", "ushort")]
    [DataRow("PgRange<ushort> range = new()", "new()", "ushort")]
    public async Task UnsupportedRangeBoundsAreErrors(string expression, string location, string type)
        => await AssertRejected(expression, "ANKUS542", location, type);

    /// <summary>
    /// Built-in and mapped range bounds construct without diagnostics.
    /// </summary>
    /// <param name="expression">The consumer expression.</param>
    [TestMethod]
    [DataRow("PgRange.Create(1, 2)")]
    [DataRow("PgRange.Create(1L, 2L)")]
    [DataRow("PgRange.Empty<DateOnly>()")]
    [DataRow("new PgRange<PgTimestampTz>()")]
    [DataRow("PgRange.Unbounded<MappedBound>()")]
    public async Task SupportedRangeBoundsCompile(string expression)
        => Assert.IsEmpty(await Analyze(expression));

    /// <summary>
    /// Asserts one exact diagnostic, its location, the reported type when given, and its help link.
    /// </summary>
    private async Task AssertRejected(string expression, string id, string location, string? type)
    {
        Diagnostic error = Assert.ContainsSingle(await Analyze(expression));
        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(location, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        if (type is not null)
        {
            Assert.StartsWith("Type '" + type + "' ", error.GetMessage(CultureInfo.InvariantCulture));
        }

        Assert.AreEqual(id == "ANKUS541" ? "https://ankus.net/spi/#value-types" : "https://ankus.net/ranges/", error.Descriptor.HelpLinkUri);
    }

    /// <summary>
    /// Compiles a consumer of the actual runtime and runs the shipped analyzer.
    /// </summary>
    private async Task<ImmutableArray<Diagnostic>> Analyze(string expression)
    {
        string source = "using System; using System.Collections.Generic; using System.Linq; using Ankus; internal static class Consumer { "
            + "internal static void Invoke(ulong big, int number, SpiSession session, PgHeapTuple tuple, PgDatum datum, "
            + "PgAnyElement element, PgAnyArray array, PgAggregateContext aggregate) { " + expression + "; } " + Helpers + " } "
            + Declarations;
        CSharpCompilation compilation = CSharpCompilation.Create("PgConversionConsumer",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), path: "Consumer.cs",
                cancellationToken: context.CancellationToken)], CompilationReferences.Platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden));
        var analysis = new CompilationWithAnalyzers(compilation, [new PgConversionAnalyzer()],
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: false));
        return await analysis.GetAnalyzerDiagnosticsAsync(context.CancellationToken);
    }
}
