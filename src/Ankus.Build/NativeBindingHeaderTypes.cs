using System.Globalization;
using System.Text.Json.Serialization;

namespace Ankus.Build;

/// <summary>
/// Retains a selected compiler's C type tree without replacing native typedef identities.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(NativeHeaderScalar), "scalar")]
[JsonDerivedType(typeof(NativeHeaderRecord), "record")]
[JsonDerivedType(typeof(NativeHeaderEnum), "enum")]
[JsonDerivedType(typeof(NativeHeaderAlias), "alias")]
[JsonDerivedType(typeof(NativeHeaderQualified), "qualified")]
[JsonDerivedType(typeof(NativeHeaderPointer), "pointer")]
[JsonDerivedType(typeof(NativeHeaderArray), "array")]
[JsonDerivedType(typeof(NativeHeaderAdjusted), "adjusted")]
[JsonDerivedType(typeof(NativeHeaderFunction), "function")]
internal abstract record NativeHeaderType
{
    /// <summary>
    /// Writes one C declaration while preserving nested declarator precedence.
    /// </summary>
    /// <param name="name">The declaration's identifier.</param>
    /// <returns>A complete declaration without its terminating semicolon.</returns>
    internal string Declare(string name)
    {
        NativeBindingCDeclaration.ValidateName(name);
        return Format(name, NativeHeaderQualifiers.None);
    }

    /// <summary>
    /// Places a declarator inside this type and applies qualifiers at this level.
    /// </summary>
    internal abstract string Format(string declarator, NativeHeaderQualifiers qualifiers);

    /// <summary>
    /// Writes qualifiers in a deterministic order.
    /// </summary>
    internal static string Qualifiers(NativeHeaderQualifiers qualifiers)
    {
        if ((qualifiers & ~(NativeHeaderQualifiers.Const | NativeHeaderQualifiers.Volatile | NativeHeaderQualifiers.Restrict)) != 0)
        {
            throw new InvalidOperationException("Unsupported native type qualifiers.");
        }

        return (qualifiers.HasFlag(NativeHeaderQualifiers.Const) ? "const " : "") +
            (qualifiers.HasFlag(NativeHeaderQualifiers.Volatile) ? "volatile " : "") +
            (qualifiers.HasFlag(NativeHeaderQualifiers.Restrict) ? "restrict " : "");
    }
}

/// <summary>
/// Identifies qualifiers on one native type level.
/// </summary>
[Flags]
internal enum NativeHeaderQualifiers
{
    /// <summary>
    /// No additional qualifier.
    /// </summary>
    None = 0,
    /// <summary>
    /// The qualified value cannot be modified through this type.
    /// </summary>
    Const = 1,
    /// <summary>
    /// Accesses have C volatile semantics.
    /// </summary>
    Volatile = 2,
    /// <summary>
    /// The pointer carries a C restrict contract.
    /// </summary>
    Restrict = 4,
}

/// <summary>
/// Retains the native spelling of a compiler builtin type.
/// </summary>
/// <param name="Name">The C builtin spelling.</param>
internal sealed record NativeHeaderScalar(string Name) : NativeHeaderType
{
    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
        => Qualifiers(qualifiers) + Name + " " + declarator;
}

/// <summary>
/// Distinguishes a real C tag from an anonymous record owned by a typedef.
/// </summary>
/// <param name="Name">The native tag, or an empty string for an anonymous record.</param>
/// <param name="IsUnion">Whether the declaration is a union.</param>
/// <param name="IsComplete">Whether a complete definition is available, or null for an implicit compiler tag without a declaration.</param>
internal sealed record NativeHeaderRecord(string Name, bool IsUnion, bool? IsComplete = null) : NativeHeaderType
{
    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
        => Name.Length == 0 ? throw new InvalidOperationException("An anonymous native record requires its owning typedef.")
            : Qualifiers(qualifiers) + (IsUnion ? "union " : "struct ") + Name + " " + declarator;
}

/// <summary>
/// Retains a native enum tag independently of any enclosing typedef.
/// </summary>
/// <param name="Name">The native tag, or an empty string for an anonymous enum.</param>
/// <param name="IsComplete">Whether its definition or fixed underlying type is available, or null for an unavailable compiler declaration.</param>
internal sealed record NativeHeaderEnum(string Name, bool? IsComplete = null) : NativeHeaderType
{
    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
        => Name.Length == 0 ? throw new InvalidOperationException("An anonymous native enum requires its owning typedef.")
            : Qualifiers(qualifiers) + "enum " + Name + " " + declarator;
}

/// <summary>
/// Preserves a native typedef's spelling and its actual selected-target definition.
/// </summary>
/// <param name="Name">The C typedef identifier.</param>
/// <param name="Underlying">The compiler-resolved definition.</param>
internal sealed record NativeHeaderAlias(string Name, NativeHeaderType Underlying) : NativeHeaderType
{
    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
        => Qualifiers(qualifiers) + Name + " " + declarator;
}

/// <summary>
/// Applies qualifiers to exactly one level of a native type.
/// </summary>
/// <param name="Underlying">The qualified type.</param>
/// <param name="Modifiers">Its native qualifiers.</param>
internal sealed record NativeHeaderQualified(NativeHeaderType Underlying, NativeHeaderQualifiers Modifiers) : NativeHeaderType
{
    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
        => Underlying.Format(declarator, qualifiers | Modifiers);
}

/// <summary>
/// Retains a native pointer, including pointers to arrays and function types.
/// </summary>
/// <param name="Element">The pointed-to type.</param>
internal sealed record NativeHeaderPointer(NativeHeaderType Element) : NativeHeaderType
{
    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
    {
        string value = "*" + Qualifiers(qualifiers) + declarator;
        NativeHeaderType element = Element;
        while (element is NativeHeaderQualified qualified)
        {
            element = qualified.Underlying;
        }

        if (element is NativeHeaderArray or NativeHeaderFunction)
        {
            value = "(" + value + ")";
        }

        return Element.Format(value, NativeHeaderQualifiers.None);
    }
}

/// <summary>
/// Retains fixed and incomplete native arrays without confusing them with adjusted function parameters.
/// </summary>
/// <param name="Element">The array element type.</param>
/// <param name="Count">The exact extent, or null for an incomplete array.</param>
/// <param name="HasMinimumExtent">Whether a written parameter promises at least this many elements.</param>
/// <param name="IndexQualifiers">Qualifiers applied to an adjusted parameter pointer by its bracket syntax.</param>
internal sealed record NativeHeaderArray(NativeHeaderType Element, ulong? Count, bool HasMinimumExtent = false,
    NativeHeaderQualifiers IndexQualifiers = NativeHeaderQualifiers.None) : NativeHeaderType
{
    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
        => Element.Format(declarator + "[" + (HasMinimumExtent ? "static " : "") + Qualifiers(IndexQualifiers) + Count?.ToString(CultureInfo.InvariantCulture) + "]", qualifiers);
}

/// <summary>
/// Retains both the written array/function parameter and its compiler-adjusted pointer type.
/// </summary>
/// <param name="Written">The header spelling before C parameter adjustment.</param>
/// <param name="Adjusted">The type C actually passes.</param>
internal sealed record NativeHeaderAdjusted(NativeHeaderType Written, NativeHeaderType Adjusted) : NativeHeaderType
{
    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
    {
        // Conditional operands apply C array conversion without evaluating either operand.
        // Using the written typedef also avoids inventing inaccessible compiler tags such as __va_list_tag.
        // Restore the adjusted pointer's top-level qualifiers, which conditional conversion discards.
        NativeHeaderType adjusted = Adjusted;
        while (adjusted is NativeHeaderQualified qualified)
        {
            qualifiers |= qualified.Modifiers;
            adjusted = qualified.Underlying;
        }

        NativeHeaderType written = Written;
        while (written is NativeHeaderAlias or NativeHeaderQualified)
        {
            written = written is NativeHeaderAlias alias ? alias.Underlying : ((NativeHeaderQualified)written).Underlying;
        }

        // MSVC's typeof does not reliably decay a function conditional. Its written type can be addressed directly.
        if (written is NativeHeaderFunction)
        {
            return new NativeHeaderPointer(Written).Format(declarator, qualifiers);
        }

        NativeHeaderType storage = Written is NativeHeaderArray array
            ? array with
            {
                HasMinimumExtent = false,
                IndexQualifiers = NativeHeaderQualifiers.None
            } : Written;
        string address = new NativeHeaderPointer(storage).Format("", NativeHeaderQualifiers.None).Trim();
        return Qualifiers(qualifiers) + "__typeof__(0 ? *(" + address + ")0 : *(" + address + ")0) " + declarator;
    }
}

/// <summary>
/// Retains the compiler's ordered native function type, including callback returns.
/// </summary>
/// <param name="Result">The native return type.</param>
/// <param name="Parameters">The ordered, C-adjusted fixed parameter types.</param>
/// <param name="IsVariadic">Whether additional promoted call-site arguments are required.</param>
/// <param name="HasPrototype">
/// Whether the declaration has a prototype; otherwise its argument list is unspecified.
/// </param>
/// <param name="DoesNotReturn">Whether the function type itself carries a no-return contract.</param>
/// <param name="CallingConvention">The compiler's native convention, using the measured graph's canonical convention identifiers.</param>
internal sealed record NativeHeaderFunction(NativeHeaderType Result, IReadOnlyList<NativeHeaderType> Parameters,
    bool IsVariadic, bool HasPrototype, bool DoesNotReturn, int CallingConvention = 1) : NativeHeaderType
{
    /// <summary>
    /// Checks a C11 function type: an unprototyped one has no parameter list, and a variadic one has a named parameter.
    /// </summary>
    /// <param name="hasPrototype">Whether the type declares its parameters.</param>
    /// <param name="parameters">The number of named parameters.</param>
    /// <param name="variadic">Whether the type ends with an ellipsis.</param>
    /// <returns>Whether C11 allows the type.</returns>
    internal static bool IsValidPrototype(bool hasPrototype, int parameters, bool variadic)
        => hasPrototype ? !variadic || parameters != 0 : parameters == 0 && !variadic;

    internal override string Format(string declarator, NativeHeaderQualifiers qualifiers)
    {
        if (qualifiers != NativeHeaderQualifiers.None)
        {
            throw new InvalidOperationException("A C function type cannot be qualified.");
        }

        if (!IsValidPrototype(HasPrototype, Parameters.Count, IsVariadic))
        {
            throw new InvalidOperationException("Invalid C11 function prototype.");
        }

        string parameters = string.Join(", ", Parameters.Select(static (type, index) =>
            (type is NativeHeaderAdjusted adjusted ? adjusted.Written : type).Declare("ankus_arg" + index.ToString(CultureInfo.InvariantCulture))));
        if (IsVariadic)
        {
            parameters += Parameters.Count == 0 ? "..." : ", ...";
        }
        else if (HasPrototype && Parameters.Count == 0)
        {
            parameters = "void";
        }

        string convention = CallingConvention switch
        {
            1 => "",
            2 => " __attribute__((stdcall))",
            3 => " __attribute__((fastcall))",
            4 => " __attribute__((thiscall))",
            10 => " __attribute__((ms_abi))",
            11 => " __attribute__((sysv_abi))",
            12 => " __attribute__((vectorcall))",
            _ => throw new InvalidOperationException("Unsupported native function calling convention."),
        };
        string value = Result.Format(declarator + "(" + parameters + ")", NativeHeaderQualifiers.None) + convention;
        return DoesNotReturn ? value + " __attribute__((noreturn))" : value;
    }
}
