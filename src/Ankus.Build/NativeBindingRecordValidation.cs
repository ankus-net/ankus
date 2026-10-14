using System.Globalization;
using System.Numerics;

namespace Ankus.Build;

/// <summary>
/// Rejects partial, contradictory or out-of-bounds native record graphs before publication.
/// </summary>
internal static class NativeBindingRecordValidation
{
    /// <summary>
    /// Checks target identity, every graph edge and physical record storage without following recursive pointers.
    /// </summary>
    internal static void Validate(NativeRecordGraph graph, NativeHeaderTarget expected, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(graph);
        NativeBindingNumericModel.Validate(expected.Numeric);
        string[] selected = [.. names.Order(StringComparer.Ordinal)];
        if (graph.Target != expected || graph.Roots is null || graph.Types is null || graph.Declarations is null ||
            string.IsNullOrEmpty(expected.RuntimeIdentifier) || !NativeBindingTarget.IsValid(expected.RuntimeIdentifier, expected.PointerSize, expected.IsLittleEndian) ||
            graph.Types.Count > 100_000 || graph.Declarations.Count > 100_000 ||
            !graph.Roots.Keys.Order(StringComparer.Ordinal).SequenceEqual(selected, StringComparer.Ordinal))
        {
            throw new FormatException("Native record graph has the wrong target or selection.");
        }

        foreach ((string name, int id) in graph.Roots)
        {
            NativeBindingCDeclaration.ValidateName(name);
            _ = Type(id);
        }

        for (int id = 0; id < graph.Types.Count; id++)
        {
            NativeRecordType type = Type(id);
            NativeRecordType canonical = Type(type.Canonical);
            if (IsWrapper(canonical.Kind))
            {
                throw new FormatException("A canonical native type cannot retain an alias or spelling wrapper.");
            }

            if (canonical.Canonical != type.Canonical || string.IsNullOrEmpty(type.Spelling) || type.Spelling.Length > 1_048_576 ||
                type.Name is null || (type.Qualifiers & ~(NativeHeaderQualifiers.Const | NativeHeaderQualifiers.Volatile | NativeHeaderQualifiers.Restrict)) != 0 ||
                type.SourceDeclaration?.Length > 1_048_576)
            {
                throw new FormatException("Invalid canonical native type or spelling.");
            }

            Storage(type.Size, type.Alignment);
            if (type.Element is int element)
            {
                _ = Type(element);
            }

            if (type.Declaration is int declaration)
            {
                _ = Declaration(declaration);
            }

            bool indirect = type.Kind is "alias" or "pointer" or "array" or "vector" or "elaborated" or "attributed" or "atomic" or "complex" or "typeof";
            bool tagged = type.Kind is "record" or "enum";
            if (indirect != type.Element.HasValue || tagged != type.Declaration.HasValue ||
                type.Kind == "function" != (type.Function is not null) ||
                (type.Kind != "array" && type.Kind != "vector" && type.Count is not null) ||
                (type.Kind != "alias" && type.SourceDeclaration is not null))
            {
                throw new FormatException("Native type edges do not match its shape.");
            }

            if (type.Kind is not ("scalar" or "alias" or "pointer" or "array" or "vector" or "elaborated" or "attributed" or "atomic" or "complex" or "record" or "enum" or "function" or "typeof"))
            {
                throw new FormatException("Unknown native record type shape.");
            }

            if (type.Kind == "typeof" && (type.Element != type.Canonical || type.Canonical == id || type.Size != canonical.Size || type.Alignment != canonical.Alignment))
            {
                throw new FormatException("A native typeof expression disagrees with its resolved object representation.");
            }

            if ((type.Kind is "scalar" or "alias") != (type.Name.Length != 0) || (type.Kind == "alias" && string.IsNullOrEmpty(type.SourceDeclaration)))
            {
                throw new FormatException("Missing or extraneous native type name/declaration.");
            }

            if (canonical.Kind == "pointer" && (type.Size != expected.PointerSize || type.Alignment is null))
            {
                throw new FormatException("Native pointer storage disagrees with the selected target.");
            }

            bool nonObject = canonical.Kind == "function" || canonical.Kind == "scalar" && canonical.Name == "void";
            if (nonObject && (type.Size is not null || type.Alignment is not null))
            {
                throw new FormatException("A non-object native type has object storage.");
            }

            if (type.Kind is "array" or "vector")
            {
                if (type.Count < 0 || (type.Kind == "vector" && type.Count is null or 0))
                {
                    throw new FormatException("Invalid native array/vector extent.");
                }

                if (canonical.Kind == "array" && type.Count is null && type.Size is not null)
                {
                    throw new FormatException("An incomplete native array cannot have a total size.");
                }

                NativeRecordType item = Type(type.Element!.Value);
                if (canonical.Kind == type.Kind && type.Count is long count && item.Size is long stride && type.Size is long size &&
                    (type.Kind == "array" ? (BigInteger)count * stride != size : (BigInteger)count * stride > size))
                {
                    throw new FormatException("Native array/vector storage disagrees with its extent.");
                }
            }

            if (type.Function is NativeRecordFunction function)
            {
                _ = Type(function.Result);
                if (function.Parameters is null || function.Parameters.Count > 100_000 || function.CallingConvention is < 0 or >= 100 ||
                    (!function.HasPrototype && function.Parameters.Count != 0))
                {
                    throw new FormatException("Invalid native function shape.");
                }

                foreach (int parameter in function.Parameters)
                {
                    _ = Type(parameter);
                }
            }

            if (tagged)
            {
                NativeRecordDeclaration target = Declaration(type.Declaration!.Value);
                if (target.Kind == "enum" != (type.Kind == "enum"))
                {
                    throw new FormatException("Native type references the wrong declaration kind.");
                }

                if (type.Size != target.Size || type.Alignment != target.Alignment)
                {
                    throw new FormatException("Native tagged storage disagrees with its declaration.");
                }
            }
        }

        ValidateWrappers(graph);
        long totalFields = 0;
        long totalConstants = 0;
        foreach (NativeRecordDeclaration declaration in graph.Declarations)
        {
            if (declaration is null || declaration.Kind is not ("struct" or "union" or "enum") || declaration.Name is null ||
                declaration.Fields is null || declaration.EnumValues is null || declaration.IsComplete != declaration.Size.HasValue ||
                declaration.IsComplete != declaration.Alignment.HasValue)
            {
                throw new FormatException("Invalid native declaration completeness.");
            }

            Storage(declaration.Size, declaration.Alignment);
            if (declaration.Name.Length != 0)
            {
                NativeBindingCDeclaration.ValidateName(declaration.Name);
            }

            totalFields += declaration.Fields.Count;
            totalConstants += declaration.EnumValues.Count;
            if (totalFields > 1_000_000 || totalConstants > 1_000_000 || !ConsistentMembers(declaration))
            {
                throw new FormatException("Invalid fields or enum values for the native declaration.");
            }

            if (declaration.EnumUnderlying is int underlying)
            {
                _ = Type(underlying);
            }

            var constantNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (NativeRecordConstant constant in declaration.EnumValues)
            {
                if (constant is null || constant.Value is null || constant.Value.Length > 20 || !constantNames.Add(constant.Name) ||
                    !BigInteger.TryParse(constant.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger value) ||
                    value < long.MinValue || value > ulong.MaxValue || value.ToString(CultureInfo.InvariantCulture) != constant.Value)
                {
                    throw new FormatException("Invalid or duplicate native enum constant.");
                }

                NativeBindingCDeclaration.ValidateName(constant.Name);
            }

            foreach (NativeRecordField field in declaration.Fields)
            {
                if (field is null || field.Name is null || string.IsNullOrEmpty(field.SourceDeclaration) || field.SourceDeclaration.Length > 1_048_576 ||
                    field.OffsetBits < 0 || field.BitWidth < 0 || (field.IsAnonymous && (field.Name.Length != 0 || field.BitWidth is not null)))
                {
                    throw new FormatException("Invalid native field identity or bit layout.");
                }

                NativeRecordType fieldType = Type(field.Type);
                NativeRecordType canonicalField = Type(fieldType.Canonical);
                if (field.Name.Length != 0)
                {
                    NativeBindingCDeclaration.ValidateName(field.Name);
                }

                if (!ConsistentField(field, fieldType, canonicalField, declaration.Kind))
                {
                    throw new FormatException("Native field shape contradicts its physical layout.");
                }

                BigInteger length = field.BitWidth is int width ? width : (BigInteger)(fieldType.Size ?? 0) * 8;
                if ((BigInteger)field.OffsetBits + length > (BigInteger)declaration.Size!.Value * 8)
                {
                    throw new FormatException("Native field exceeds its containing record.");
                }
            }
        }

        var visitedTypes = new HashSet<int>();
        var visitedDeclarations = new HashSet<int>();
        var pending = new Queue<int>(graph.Roots.Values);
        while (pending.TryDequeue(out int typeIndex))
        {
            if (!visitedTypes.Add(typeIndex))
            {
                continue;
            }

            NativeRecordType type = Type(typeIndex);
            pending.Enqueue(type.Canonical);
            if (type.Element is int element)
            {
                pending.Enqueue(element);
            }

            if (type.Function is NativeRecordFunction function)
            {
                pending.Enqueue(function.Result);
                foreach (int parameter in function.Parameters)
                {
                    pending.Enqueue(parameter);
                }
            }

            if (type.Declaration is int declarationIndex && visitedDeclarations.Add(declarationIndex))
            {
                NativeRecordDeclaration declaration = Declaration(declarationIndex);
                foreach (NativeRecordField field in declaration.Fields)
                {
                    pending.Enqueue(field.Type);
                }

                if (declaration.EnumUnderlying is int underlying)
                {
                    pending.Enqueue(underlying);
                }
            }
        }

        if (visitedTypes.Count != graph.Types.Count || visitedDeclarations.Count != graph.Declarations.Count)
        {
            throw new FormatException("Native record graph contains observations outside the selected closure.");
        }

        NativeRecordType Type(int id) => id >= 0 && id < graph.Types.Count && graph.Types[id] is NativeRecordType value
            ? value : throw new FormatException("Invalid native type index.");

        NativeRecordDeclaration Declaration(int id) => id >= 0 && id < graph.Declarations.Count && graph.Declarations[id] is NativeRecordDeclaration value
            ? value : throw new FormatException("Invalid native declaration index.");
    }

    /// <summary>
    /// Checks that only a complete declaration lists members, and that an enum has constants and, once complete, an
    /// underlying type, but no fields.
    /// </summary>
    private static bool ConsistentMembers(NativeRecordDeclaration declaration)
    {
        if (!declaration.IsComplete && (declaration.Fields.Count != 0 || declaration.EnumValues.Count != 0))
        {
            return false;
        }

        return declaration.Kind == "enum"
            ? declaration.Fields.Count == 0 && (!declaration.IsComplete || declaration.EnumUnderlying is not null)
            : declaration.EnumUnderlying is null && declaration.EnumValues.Count == 0;
    }

    /// <summary>
    /// Checks that a field's name, bit width and offset agree with its type and the record that contains it.
    /// </summary>
    private static bool ConsistentField(NativeRecordField field, NativeRecordType fieldType, NativeRecordType canonicalField,
        string declarationKind)
    {
        // Only an anonymous record member may be unnamed, apart from unnamed bit-field padding.
        if (field.IsAnonymous ? canonicalField.Kind != "record" : field.Name.Length == 0 && field.BitWidth is null)
        {
            return false;
        }

        if (field.BitWidth is int bitWidth)
        {
            // A zero-width bit-field only aligns the next one, so it has no name.
            if (bitWidth == 0 && field.Name.Length != 0 || fieldType.Size is not long size || bitWidth > (BigInteger)size * 8)
            {
                return false;
            }
        }
        else if (field.OffsetBits % 8 != 0)
        {
            return false;
        }

        // Only a flexible array member has no size.
        if (fieldType.Size is null && (canonicalField.Kind != "array" || canonicalField.Count is not null))
        {
            return false;
        }

        return declarationKind != "union" || field.OffsetBits == 0;
    }

    private static void Storage(long? size, long? alignment)
    {
        bool validAlignment = alignment is not long value || value > 0 && BitOperations.IsPow2((ulong)value);
        if (size < 0 || size is not null && alignment is null || !validAlignment)
        {
            throw new FormatException("Invalid native object size or alignment.");
        }
    }

    /// <summary>
    /// Rejects cyclic typedef/spelling edges in linear time, including aliases reachable only through record fields.
    /// </summary>
    private static void ValidateWrappers(NativeRecordGraph graph)
    {
        byte[] states = new byte[graph.Types.Count];
        for (int index = 0; index < graph.Types.Count; index++)
        {
            if (states[index] != 0 || !IsWrapper(graph.Types[index].Kind))
            {
                continue;
            }

            var path = new List<int>();
            int current = index;
            while (IsWrapper(graph.Types[current].Kind))
            {
                if (states[current] == 2)
                {
                    break;
                }

                if (states[current] == 1)
                {
                    throw new FormatException("Native type shape has an alias or wrapper cycle.");
                }

                states[current] = 1;
                path.Add(current);
                current = graph.Types[current].Element!.Value;
            }

            foreach (int visited in path)
            {
                states[visited] = 2;
            }
        }
    }

    /// <summary>
    /// Identifies spelling layers that must resolve to an actual canonical native type.
    /// </summary>
    private static bool IsWrapper(string kind) => kind is "alias" or "elaborated" or "attributed" or "typeof";
}
