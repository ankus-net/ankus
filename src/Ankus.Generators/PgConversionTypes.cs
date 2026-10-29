using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Whether a managed type has a PostgreSQL conversion.
/// </summary>
internal enum PgConversionSupport
{
    /// <summary>
    /// A built-in, mapped or polymorphic conversion exists.
    /// </summary>
    Supported,

    /// <summary>
    /// No conversion exists, so every use fails when it runs.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The value decides at run time, as for an open type parameter, an object, an interface or a base class of a
    /// converted type.
    /// </summary>
    RunTime,
}

/// <summary>
/// Classifies managed types with the same tables SPI parameters, results and datum reads use at run time.
/// </summary>
/// <param name="compilation">The compilation whose mapped types can derive from an unmapped base class.</param>
internal sealed class PgConversionTypes(Compilation compilation)
{
    // Value types also convert in their nullable forms, as SpiType, SpiArray and PgArrayViews list both.
    private static readonly HashSet<string> s_values = new(StringComparer.Ordinal)
    {
        "System.Guid", "System.Text.Rune", "System.DateOnly", "System.TimeOnly", "System.DateTime", "System.DateTimeOffset",
        "System.TimeSpan", "System.Net.IPNetwork", "Ankus.PgJson", "Ankus.PgJsonb", "Ankus.PgNumeric", "Ankus.PgDate",
        "Ankus.PgTime", "Ankus.PgTimeTz", "Ankus.PgTimestamp", "Ankus.PgTimestampTz", "Ankus.PgInterval", "Ankus.PgPoint",
        "Ankus.PgLineSegment", "Ankus.PgBox", "Ankus.PgLine", "Ankus.PgCircle", "Ankus.PgInet", "Ankus.PgCidr",
        "Ankus.PgItemPointer", "Ankus.PgTransactionId", "Ankus.PgRelationIdentity",
    };

    private static readonly HashSet<string> s_references = new(StringComparer.Ordinal)
    {
        "System.Net.IPAddress", "Ankus.PgPath", "Ankus.PgPolygon", "Ankus.PgRelation", "Ankus.PgHeapTuple", "Ankus.PgCString",
    };

    // Borrowed buffers bind as scalars and typed view elements; they have no owned array form.
    private static readonly HashSet<string> s_views = new(StringComparer.Ordinal)
    {
        "Ankus.PgCStringView", "Ankus.PgTextView", "Ankus.PgByteaView",
    };

    // These carry their PostgreSQL type with the value, so they bind and read as scalars of any type.
    private static readonly HashSet<string> s_polymorphic = new(StringComparer.Ordinal)
    {
        "Ankus.PgDatum", "Ankus.PgAnyElement", "Ankus.PgAnyArray", "Ankus.PgArrayView", "Ankus.PgInternal",
    };

    private static readonly HashSet<string> s_bounds = new(StringComparer.Ordinal)
    {
        "Ankus.PgNumeric", "Ankus.PgDate", "System.DateOnly", "Ankus.PgTimestamp", "System.DateTime", "Ankus.PgTimestampTz",
        "System.DateTimeOffset",
    };

    private readonly Lazy<HashSet<INamedTypeSymbol>> _bases = new(() => FindBases(compilation), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Where a type appears, which selects the runtime table that converts it.
    /// </summary>
    private enum Position
    {
        /// <summary>
        /// A parameter, result or cell.
        /// </summary>
        Scalar,

        /// <summary>
        /// An element of a vector or <c>PgArray&lt;T&gt;</c>.
        /// </summary>
        ArrayElement,

        /// <summary>
        /// An element of a <c>PgArrayView&lt;T&gt;</c>.
        /// </summary>
        ViewElement,
    }

    /// <summary>
    /// Classifies a parameter, result or cell type.
    /// </summary>
    /// <param name="type">The type argument.</param>
    /// <returns>Whether the type has a conversion.</returns>
    internal PgConversionSupport Value(ITypeSymbol type) => Classify(type, Position.Scalar);

    /// <summary>
    /// Classifies the bound of a <c>PgRange&lt;T&gt;</c>.
    /// </summary>
    /// <param name="type">The bound type argument.</param>
    /// <returns>Whether a PostgreSQL range type has this bound.</returns>
    internal static PgConversionSupport Bound(ITypeSymbol type)
    {
        if (IsOpen(type))
        {
            return PgConversionSupport.RunTime;
        }

        if (type.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Decimal)
        {
            return PgConversionSupport.Supported;
        }

        return type is INamedTypeSymbol named && (s_bounds.Contains(Name(named)) || IsMappedBound(named.OriginalDefinition))
            ? PgConversionSupport.Supported : PgConversionSupport.Unsupported;
    }

    /// <summary>
    /// Finds a datum mapping that also declares the PostgreSQL range type for its bound.
    /// </summary>
    private static bool IsMappedBound(INamedTypeSymbol type)
        => DatumTypeDeclaration.IsMapped(type) && HasAttribute(type, "Ankus.PgRangeTypeAttribute");

    /// <summary>
    /// Applies the table for a scalar, an owned array element or a typed view element.
    /// </summary>
    private PgConversionSupport Classify(ITypeSymbol type, Position position)
    {
        if (type.TypeKind is TypeKind.Dynamic or TypeKind.Interface || IsOpen(type))
        {
            return PgConversionSupport.RunTime;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (array.IsSZArray && array.ElementType.SpecialType == SpecialType.System_Byte)
            {
                return PgConversionSupport.Supported;
            }

            // A vector read matches covariantly, so object, interface and base class elements stay value-dependent.
            return array.IsSZArray && position == Position.Scalar ? Classify(array.ElementType, Position.ArrayElement)
                : PgConversionSupport.Unsupported;
        }

        if (type is not INamedTypeSymbol named)
        {
            return PgConversionSupport.Unsupported;
        }

        if (named.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum or
            SpecialType.System_Array)
        {
            return PgConversionSupport.RunTime;
        }

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return Classify(named.TypeArguments[0], position);
        }

        if (named.IsGenericType && IsAnkus(named) && named.Arity == 1)
        {
            ITypeSymbol argument = named.TypeArguments[0];
            switch (named.Name)
            {
                case "PgArray":
                    return position == Position.Scalar ? Exact(Classify(argument, Position.ArrayElement)) : PgConversionSupport.Unsupported;
                case "PgArrayView":
                    return position == Position.Scalar ? Exact(Classify(argument, Position.ViewElement)) : PgConversionSupport.Unsupported;
                case "PgRange":
                    return Bound(argument);
                case "PgVarlena":
                    return argument is INamedTypeSymbol layout && HasAttribute(layout, "Ankus.PgTypeAttribute")
                        ? PgConversionSupport.Supported : PgConversionSupport.Unsupported;
            }
        }

        if (IsMapped(named))
        {
            return PgConversionSupport.Supported;
        }

        string name = Name(named);
        bool supported = named.SpecialType switch
        {
            SpecialType.System_Boolean or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_Int32 or
                SpecialType.System_Int64 or SpecialType.System_UInt32 or SpecialType.System_Single or SpecialType.System_Double or
                SpecialType.System_Decimal or SpecialType.System_String => true,
            _ when s_values.Contains(name) => position != Position.ViewElement || name != "System.Text.Rune",
            _ when s_references.Contains(name) => true,
            _ when s_views.Contains(name) => position != Position.ArrayElement,
            _ when s_polymorphic.Contains(name) => position == Position.Scalar,
            _ => false,
        };
        if (supported)
        {
            return PgConversionSupport.Supported;
        }

        // A read checks the cell's value against the requested type, so a base class of a converted type can match it.
        return named.TypeKind == TypeKind.Class && !named.IsSealed && !named.IsStatic && _bases.Value.Contains(named.OriginalDefinition)
            ? PgConversionSupport.RunTime : PgConversionSupport.Unsupported;
    }

    /// <summary>
    /// Owned arrays and typed views convert only their exact element types, so a value-dependent element never matches.
    /// </summary>
    private static PgConversionSupport Exact(PgConversionSupport element)
        => element == PgConversionSupport.RunTime ? PgConversionSupport.Unsupported : element;

    /// <summary>
    /// Finds a type parameter, which only a construction decides, or a type the compiler already reports.
    /// </summary>
    private static bool IsOpen(ITypeSymbol type)
        => type switch
        {
            ITypeParameterSymbol or IErrorTypeSymbol => true,
            IArrayTypeSymbol array => IsOpen(array.ElementType),
            INamedTypeSymbol named => named.TypeArguments.Any(IsOpen) || named.ContainingType is { } outer && IsOpen(outer),
            _ => false,
        };

    /// <summary>
    /// Finds a generated conversion, including a generic datum template and a variant of a polymorphic base type.
    /// </summary>
    private static bool IsMapped(INamedTypeSymbol type)
    {
        if (DatumTypeDeclaration.IsMapped(type.OriginalDefinition) ||
            type.TypeKind == TypeKind.Enum && HasAttribute(type, "Ankus.PgEnumAttribute"))
        {
            return true;
        }

        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (HasAttribute(current.OriginalDefinition, "Ankus.PgTypeAttribute"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects the base classes of built-in reference conversions and of mapped types in this compilation and in
    /// referenced Ankus libraries.
    /// </summary>
    private static HashSet<INamedTypeSymbol> FindBases(Compilation compilation)
    {
        var bases = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (string name in s_references.Concat(s_polymorphic).Concat(["Ankus.PgArray`1", "Ankus.PgArrayView`1", "Ankus.PgRange`1",
            "Ankus.PgVarlena`1"]))
        {
            Add(compilation.GetTypeByMetadataName(name));
        }

        var pending = new Stack<INamespaceSymbol>();
        pending.Push(compilation.Assembly.GlobalNamespace);
        foreach (IAssemblySymbol assembly in compilation.SourceModule.ReferencedAssemblySymbols.Where(static assembly =>
            assembly.Modules.Any(static module => module.ReferencedAssemblySymbols.Any(static reference => reference.Name == "Ankus.Runtime"))))
        {
            pending.Push(assembly.GlobalNamespace);
        }

        while (pending.Count != 0)
        {
            INamespaceSymbol current = pending.Pop();
            foreach (INamespaceSymbol child in current.GetNamespaceMembers())
            {
                pending.Push(child);
            }

            foreach (INamedTypeSymbol type in current.GetTypeMembers())
            {
                AddMapped(type);
            }
        }

        return bases;

        void AddMapped(INamedTypeSymbol type)
        {
            if (DatumTypeDeclaration.IsMapped(type) || HasAttribute(type, "Ankus.PgTypeAttribute"))
            {
                Add(type);
            }

            foreach (INamedTypeSymbol nested in type.GetTypeMembers())
            {
                AddMapped(nested);
            }
        }

        void Add(INamedTypeSymbol? type)
        {
            for (INamedTypeSymbol? current = type?.BaseType; current is not null; current = current.BaseType)
            {
                bases.Add(current.OriginalDefinition);
            }
        }
    }

    /// <summary>
    /// Matches an Ankus attribute by name, as the generators do, so a mapping in a referenced library counts too.
    /// </summary>
    private static bool HasAttribute(INamedTypeSymbol type, string name)
        => type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == name);

    /// <summary>
    /// Finds a top-level type in the Ankus namespace.
    /// </summary>
    private static bool IsAnkus(INamedTypeSymbol type)
        => type.ContainingType is null && type.ContainingNamespace is { Name: "Ankus", ContainingNamespace.IsGlobalNamespace: true };

    /// <summary>
    /// Gets the namespace-qualified metadata name that the runtime tables use.
    /// </summary>
    private static string Name(INamedTypeSymbol type)
        => type.ContainingType is null ? type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName : type.ToDisplayString();
}
