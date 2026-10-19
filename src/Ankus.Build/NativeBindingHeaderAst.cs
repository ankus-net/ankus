namespace Ankus.Build;

/// <summary>
/// The declarations of a header manifest's translation unit, as libclang reports them to bindgen.
/// </summary>
internal sealed class NativeBindingHeaderAst
{
    /// <summary>
    /// Gets every struct and union declaration, complete or forward, by identity.
    /// </summary>
    internal Dictionary<string, AstRecord> Records { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets every file-scope typedef in declaration order.
    /// </summary>
    internal List<AstTypedef> Typedefs { get; } = [];

    /// <summary>
    /// Gets every enum declaration by identity.
    /// </summary>
    internal Dictionary<string, AstEnum> Enums { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets every file-scope function declaration in declaration order.
    /// </summary>
    internal List<AstFunction> Functions { get; } = [];

    /// <summary>
    /// Gets every file-scope variable declaration in declaration order.
    /// </summary>
    internal List<AstVariable> Variables { get; } = [];

    /// <summary>
    /// Gets every macro definition in definition order.
    /// </summary>
    internal List<AstMacro> Macros { get; } = [];

    /// <summary>
    /// Gets the declaration order of file-scope records, enums and typedefs, which bindgen's naming follows.
    /// </summary>
    internal List<string> TopLevelOrder { get; } = [];

    /// <summary>
    /// Gets the identities of the struct and union declarations made at file scope, which bindgen parses as standalone
    /// items, separately from any type that names them.
    /// </summary>
    internal HashSet<string> FileScope { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the target's size and alignment of each scalar type the declarations use, by canonical C spelling, with
    /// <c>void *</c> for pointers.
    /// </summary>
    internal Dictionary<string, (long Size, long Alignment)> Scalars { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A struct or union declaration.
/// </summary>
/// <param name="Id">The declaration's identity.</param>
/// <param name="Name">The tag or the typedef naming an anonymous record, or an empty string when anonymous.</param>
/// <param name="IsUnion">Whether it is a union.</param>
/// <param name="IsComplete">Whether this declaration is a definition.</param>
/// <param name="File">The header it is declared in.</param>
/// <param name="Parent">The enclosing record's identity for a nested declaration.</param>
internal sealed record AstRecord(string Id, string Name, bool IsUnion, bool IsComplete, string? File, string? Parent)
{
    /// <summary>
    /// Gets a value indicating whether the record is declared packed.
    /// </summary>
    internal bool IsPacked { get; init; }

    /// <summary>
    /// Gets the record's size in bytes on the target, for a definition.
    /// </summary>
    internal long Size { get; init; }

    /// <summary>
    /// Gets the record's alignment in bytes on the target, for a definition.
    /// </summary>
    internal long Alignment { get; init; }

    /// <summary>
    /// Gets the fields in declaration order, including bindgen's unnamed fields for anonymous members.
    /// </summary>
    internal List<AstField> Fields { get; } = [];

    /// <summary>
    /// Gets the identities of records declared inside this one.
    /// </summary>
    internal List<string> Nested { get; } = [];
}

/// <summary>
/// A record field.
/// </summary>
/// <param name="Name">The field name, empty for an anonymous member.</param>
/// <param name="Type">The field type.</param>
/// <param name="BitWidth">The bitfield width, or null for an ordinary field.</param>
/// <param name="Offset">The offset in bits, or null where libclang reports none, as for an anonymous member.</param>
internal sealed record AstField(string Name, NativeCType Type, int? BitWidth, long? Offset);

/// <summary>
/// An enum declaration.
/// </summary>
/// <param name="Id">The declaration's identity.</param>
/// <param name="Name">The tag or the typedef naming an anonymous enum, or an empty string when anonymous.</param>
/// <param name="File">The header it is declared in.</param>
/// <param name="IntegerType">The enum's integer type.</param>
internal sealed record AstEnum(string Id, string Name, string? File, NativeCType IntegerType)
{
    /// <summary>
    /// Gets the enumerators and their values in declaration order, read with the integer type's signedness.
    /// </summary>
    internal List<(string Name, string Value)> Constants { get; } = [];
}

/// <summary>
/// A typedef.
/// </summary>
/// <param name="Id">The declaration's identity.</param>
/// <param name="Name">The typedef name.</param>
/// <param name="Type">The aliased type.</param>
/// <param name="File">The header it is declared in.</param>
internal sealed record AstTypedef(string Id, string Name, NativeCType Type, string? File);

/// <summary>
/// A function declaration.
/// </summary>
/// <param name="Name">The function name.</param>
/// <param name="Type">The function type, with the declaration's parameter names.</param>
/// <param name="IsInternal">Whether the function has internal linkage, as a <c>static</c> function does.</param>
/// <param name="IsInline">Whether it is declared inline.</param>
/// <param name="IsVisible">Whether it has default visibility, which bindgen requires.</param>
/// <param name="File">The header it is declared in.</param>
/// <param name="Comment">The raw documentation comment, if any.</param>
internal sealed record AstFunction(string Name, NativeCFunction Type, bool IsInternal, bool IsInline, bool IsVisible, string? File,
    string? Comment);

/// <summary>
/// A file-scope variable declaration.
/// </summary>
/// <param name="Name">The variable name.</param>
/// <param name="Type">The declared type.</param>
/// <param name="IsConst">Whether the variable, or an array's elements, is const, which bindgen declares immutable.</param>
/// <param name="File">The header it is declared in.</param>
/// <param name="Comment">The raw documentation comment, if any.</param>
/// <param name="Value">The value bindgen evaluates from the initializer, which makes the variable a constant.</param>
internal sealed record AstVariable(string Name, NativeCType Type, bool IsConst, string? File, string? Comment, AstValue? Value);

/// <summary>
/// A macro definition.
/// </summary>
/// <param name="Name">The macro name.</param>
/// <param name="File">The header it is defined in, or null for a builtin or command-line definition.</param>
/// <param name="IsFunctionLike">Whether it takes parameters, which bindgen does not evaluate.</param>
/// <param name="Tokens">The definition's tokens, its name first, without comments.</param>
internal sealed record AstMacro(string Name, string? File, bool IsFunctionLike, IReadOnlyList<NativeCToken> Tokens);

/// <summary>
/// A value Clang evaluates for a constant.
/// </summary>
/// <param name="Kind">The kind of value.</param>
/// <param name="Text">The value: a decimal integer, a round-trip floating-point number, or the string's bytes as text.</param>
internal sealed record AstValue(AstValueKind Kind, string Text);

/// <summary>
/// The kinds of evaluated constant.
/// </summary>
internal enum AstValueKind
{
    /// <summary>
    /// An integer.
    /// </summary>
    Integer,

    /// <summary>
    /// A floating-point number.
    /// </summary>
    Float,

    /// <summary>
    /// A string literal.
    /// </summary>
    String,
}
