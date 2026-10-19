using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Proves a source-defined unmanaged layout contains only fixed-width native values, and locates its padding.
/// </summary>
internal static class NativeTypeLayout
{
    /// <summary>
    /// Computes the payload size and padding ranges without assuming the layout of framework or metadata-only structs.
    /// </summary>
    /// <param name="type">The custom type.</param>
    /// <param name="failure">The first violated contract, or null.</param>
    /// <param name="padding">
    /// Offset and length pairs of the padding bytes, which writes zero so stored values are deterministic.
    /// </param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>The payload size, or zero after a failure.</returns>
    internal static int Validate(INamedTypeSymbol type, out CustomTypeValidationFailure? failure, out string padding,
        CancellationToken cancellationToken = default)
    {
        padding = string.Empty;
        try
        {
            if (type.TypeKind != TypeKind.Struct)
            {
                throw Invalid(CustomTypeDiagnosticKind.NativeRoot, type.Locations.FirstOrDefault(), type.ToDisplayString());
            }

            Shape shape = Layout(type, type.ContainingAssembly, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default),
                new Dictionary<ITypeSymbol, Shape>(SymbolEqualityComparer.Default), type.Locations.FirstOrDefault(), cancellationToken);
            failure = null;
            padding = string.Join(",", shape.Padding.SelectMany(static gap => new[] { gap.Offset, gap.Length })
                .Select(static value => value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return shape.Size;
        }
        catch (ValidationException exception)
        {
            failure = exception.Failure;
            return 0;
        }
        catch (OverflowException)
        {
            failure = new(CustomTypeDiagnosticKind.NativeTooLarge, type.Locations.FirstOrDefault(), new([type.ToDisplayString()]));
            return 0;
        }
    }

    /// <summary>
    /// Lays out a value as .NET lays out a blittable sequential struct, which is also C's layout: each field at the next
    /// multiple of its alignment, limited by Pack, and the size rounded up to the struct's alignment.
    /// </summary>
    private static Shape Layout(ITypeSymbol type, IAssemblySymbol assembly, HashSet<ITypeSymbol> visiting,
        Dictionary<ITypeSymbol, Shape> shapes, Location? location, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int primitive = PrimitiveSize(type);
        if (primitive != 0)
        {
            return new(primitive, primitive, []);
        }

        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            return Layout(underlying, assembly, visiting, shapes, location, cancellationToken);
        }

        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct, IsUnmanagedType: true, IsGenericType: false, IsRefLikeType: false } named ||
            !SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, assembly) || named.DeclaringSyntaxReferences.Length == 0)
        {
            throw Invalid(CustomTypeDiagnosticKind.NativeField, location, type.ToDisplayString());
        }

        if (shapes.TryGetValue(named, out Shape? known))
        {
            return known;
        }

        // A struct without StructLayout is sequential with the default packing, which never exceeds 8 here.
        AttributeData? layout = named.GetAttributes().FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute");
        int pack = layout is null ? 0 : AttributeValues.Get(layout, "Pack", 0);
        if ((layout is not null && (layout.ConstructorArguments.FirstOrDefault().Value is not 0 ||
            AttributeValues.Get(layout, "Size", 0) != 0 || AttributeValues.Get(layout, "CharSet", 1) != 1)) ||
            pack is not (0 or 1 or 2 or 4 or 8 or 16 or 32 or 64 or 128) ||
            named.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InlineArrayAttribute"))
        {
            throw Invalid(CustomTypeDiagnosticKind.NativeStructLayout,
                layout?.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? location, named.ToDisplayString());
        }

        if (!visiting.Add(named))
        {
            throw Invalid(CustomTypeDiagnosticKind.NativeRecursive, location, named.ToDisplayString());
        }

        int limit = pack == 0 ? 8 : pack;
        int offset = 0;
        int alignment = 1;
        var padding = new List<Gap>();
        foreach (IFieldSymbol field in named.GetMembers().OfType<IFieldSymbol>().Where(static field => !field.IsStatic))
        {
            Shape member;
            if (field.IsFixedSizeBuffer && field.Type is IPointerTypeSymbol pointer)
            {
                int elementSize = PrimitiveSize(pointer.PointedAtType);
                if (elementSize == 0 || field.FixedSize <= 0)
                {
                    throw Invalid(CustomTypeDiagnosticKind.NativeFixedBuffer, field.Locations.FirstOrDefault(), field.Name);
                }

                member = new(checked(elementSize * field.FixedSize), elementSize, []);
            }
            else
            {
                member = Layout(field.Type, assembly, visiting, shapes, field.Locations.FirstOrDefault(), cancellationToken);
            }

            int fieldAlignment = Math.Min(member.Alignment, limit);
            offset = Pad(offset, fieldAlignment, padding);
            padding.AddRange(member.Padding.Select(gap => new Gap(checked(offset + gap.Offset), gap.Length)));
            offset = checked(offset + member.Size);
            alignment = Math.Max(alignment, fieldAlignment);
        }

        visiting.Remove(named);
        if (offset == 0)
        {
            throw Invalid(CustomTypeDiagnosticKind.NativeEmpty, location, named.ToDisplayString());
        }

        var shape = new Shape(Pad(offset, alignment, padding), alignment, padding);
        shapes.Add(named, shape);
        return shape;
    }

    /// <summary>
    /// Advances to the next multiple of an alignment, recording any skipped bytes as padding.
    /// </summary>
    private static int Pad(int offset, int alignment, List<Gap> padding)
    {
        int aligned = checked((offset + alignment - 1) / alignment * alignment);
        if (aligned != offset)
        {
            padding.Add(new(offset, aligned - offset));
        }

        return aligned;
    }

    /// <summary>
    /// Returns the CLR storage width only for representations with valid arbitrary bit patterns.
    /// </summary>
    private static int PrimitiveSize(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte => 1,
        SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
        SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
        _ => 0,
    };

    /// <summary>
    /// The size, alignment and padding of one laid-out type.
    /// </summary>
    /// <param name="Size">The size including trailing padding.</param>
    /// <param name="Alignment">The alignment the type requires.</param>
    /// <param name="Padding">The padding ranges in order.</param>
    private sealed record Shape(int Size, int Alignment, List<Gap> Padding);

    /// <summary>
    /// One run of padding bytes.
    /// </summary>
    /// <param name="Offset">The first padding byte.</param>
    /// <param name="Length">The number of padding bytes.</param>
    private sealed record Gap(int Offset, int Length);

    /// <summary>
    /// Creates a local unwind carrying a closed diagnostic: its kind, location and arguments.
    /// </summary>
    private static ValidationException Invalid(CustomTypeDiagnosticKind kind, Location? location, params string[] arguments)
        => new(new(kind, location, new(arguments)));

    /// <summary>
    /// Unwinds recursive layout validation while preserving the exact failed contract.
    /// </summary>
    private sealed class ValidationException(CustomTypeValidationFailure failure) : Exception
    {
        /// <summary>
        /// Gets the diagnostic to report at the custom-type boundary.
        /// </summary>
        internal CustomTypeValidationFailure Failure { get; } = failure;
    }
}
