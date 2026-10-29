using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

/// <summary>
/// Compares the compile-time classification with the conversions the runtime performs.
/// </summary>
public sealed partial class PgConversionAnalyzerTests
{
    // A present value of these types carries its own PostgreSQL identity, so binding their SQL NULL needs another type.
    private static readonly HashSet<Type> s_polymorphic = [typeof(PgDatum), typeof(PgAnyElement), typeof(PgAnyArray), typeof(PgArrayView)];

    /// <summary>
    /// Every type the runtime binds as a parameter is accepted, and every type it rejects is reported, for scalars,
    /// nullable forms, vectors, arrays, views and ranges of every public Ankus type and the .NET value types.
    /// </summary>
    [TestMethod]
    public void ClassificationMatchesRuntimeParameters()
    {
        CSharpCompilation compilation = CSharpCompilation.Create("PgConversionParity", [], CompilationReferences.Platform);
        var types = new PgConversionTypes(compilation);
        MethodInfo create = typeof(SpiParameter).GetMethods().Single(static method => method is { Name: "Create", IsGenericMethodDefinition: true });
        var mismatches = new List<string>();
        int checkedTypes = 0;
        foreach (Type candidate in Candidates())
        {
            ITypeSymbol symbol = Symbol(compilation, candidate);
            PgConversionSupport support = types.Value(symbol);
            bool binds = Binds(create.MakeGenericMethod(candidate), candidate);
            checkedTypes++;
            PgConversionSupport expected = binds || s_polymorphic.Contains(candidate) ? PgConversionSupport.Supported
                : DecidedAtRunTime(candidate) ? PgConversionSupport.RunTime : PgConversionSupport.Unsupported;
            if (support != expected)
            {
                mismatches.Add(symbol.ToDisplayString() + ": runtime " + (binds ? "binds" : "rejects") + ", analyzer " + support);
            }
        }

        Assert.IsGreaterThan(1000, checkedTypes);
        Assert.IsEmpty(mismatches, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// Every value type the runtime accepts as a range bound is accepted, and every other one is reported.
    /// </summary>
    [TestMethod]
    public void BoundsMatchRuntimeRanges()
    {
        CSharpCompilation compilation = CSharpCompilation.Create("PgConversionParity", [], CompilationReferences.Platform);
        MethodInfo empty = typeof(PgRange).GetMethod(nameof(PgRange.Empty))!;
        var mismatches = new List<string>();
        foreach (Type candidate in Bases().Where(static type => type.IsValueType && Nullable.GetUnderlyingType(type) is null))
        {
            bool constructs = Succeeds(() => empty.MakeGenericMethod(candidate).Invoke(null, null));
            PgConversionSupport support = PgConversionTypes.Bound(Symbol(compilation, candidate));
            if (constructs != (support == PgConversionSupport.Supported))
            {
                mismatches.Add(candidate + ": runtime " + (constructs ? "constructs" : "rejects") + ", analyzer " + support);
            }
        }

        Assert.IsEmpty(mismatches, string.Join(Environment.NewLine, mismatches));
    }

    /// <summary>
    /// Identifies a type a read can still match through a converted value's base types, directly or as a vector element.
    /// </summary>
    private static bool DecidedAtRunTime(Type type)
    {
        Type core = type.IsSZArray ? type.GetElementType()! : type;
        return core.IsInterface || core == typeof(object) || core == typeof(ValueType) || core == typeof(Enum) || core == typeof(Array);
    }

    /// <summary>
    /// Binds a default value. Only NotSupportedException reports a missing conversion; a type whose OID needs the
    /// backend, such as a typed view, has a conversion and fails later without one.
    /// </summary>
    private static bool Binds(MethodInfo create, Type type)
        => Succeeds(() => create.Invoke(null, [type.IsValueType ? Activator.CreateInstance(type) : null]));

    private static bool Succeeds(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is NotSupportedException)
        {
            return false;
        }
        catch (TargetInvocationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Forms each base type as a scalar, nullable value, vector, owned array, typed view and range.
    /// </summary>
    private static IEnumerable<Type> Candidates()
    {
        foreach (Type type in Bases())
        {
            yield return type;
            foreach (Type form in Forms(type))
            {
                yield return form;
            }
        }

        static IEnumerable<Type> Forms(Type element)
        {
            if (element.IsValueType && Nullable.GetUnderlyingType(element) is null)
            {
                yield return typeof(Nullable<>).MakeGenericType(element);
                yield return typeof(PgRange<>).MakeGenericType(element);
                yield return typeof(PgRange<>).MakeGenericType(element).MakeArrayType();
            }

            yield return element.MakeArrayType();
            yield return element.MakeArrayType(2);
            yield return typeof(PgArray<>).MakeGenericType(element);
            yield return typeof(PgArrayView<>).MakeGenericType(element);
        }
    }

    private static IEnumerable<Type> Bases()
        => typeof(PgFunctionAttribute).Assembly.GetExportedTypes()
            .Where(static type => type.Namespace == "Ankus" && !type.IsGenericTypeDefinition && !type.IsByRefLike &&
                !(type.IsAbstract && type.IsSealed) && !type.IsSubclassOf(typeof(Attribute)) && !type.IsPointer)
            .Concat([
                typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long),
                typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(char), typeof(string), typeof(nint), typeof(nuint),
                typeof(Half), typeof(Int128), typeof(UInt128), typeof(BigInteger), typeof(Guid), typeof(Rune), typeof(DateOnly),
                typeof(TimeOnly), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(System.Net.IPAddress),
                typeof(System.Net.IPNetwork), typeof(Uri), typeof(Version), typeof(object), typeof(byte[]), typeof(int[]),
                typeof(ValueType), typeof(Enum), typeof(Array), typeof(IComparable), typeof(Exception), typeof((int, long)),
                typeof(List<int>), typeof(DayOfWeek),
            ]);

    /// <summary>
    /// Finds the Roslyn symbol for a reflected type, including constructed generics and arrays.
    /// </summary>
    private static ITypeSymbol Symbol(Compilation compilation, Type type)
    {
        if (type.IsArray)
        {
            return compilation.CreateArrayTypeSymbol(Symbol(compilation, type.GetElementType()!), type.GetArrayRank());
        }

        string name = (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName!;
        INamedTypeSymbol definition = compilation.GetTypeByMetadataName(name) ?? throw new InvalidOperationException("No symbol for " + name);
        return type.IsGenericType ? definition.Construct([.. type.GetGenericArguments().Select(argument => Symbol(compilation, argument))])
            : definition;
    }
}
