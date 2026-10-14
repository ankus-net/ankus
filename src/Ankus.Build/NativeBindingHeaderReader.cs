using System.Globalization;

namespace Ankus.Build;

/// <summary>
/// Reads a header manifest's declarations through libclang the way bindgen 0.72 does, so callback parameter names,
/// adjusted parameters, anonymous members and comments follow what bindgen sees.
/// </summary>
/// <param name="library">The worker's selected compiler library.</param>
/// <param name="unit">The live translation unit.</param>
internal sealed unsafe class NativeBindingHeaderReader(NativeClang library, NativeClangUnit unit)
{
    private const int StructDecl = 2;
    private const int UnionDecl = 3;
    private const int EnumDecl = 5;
    private const int FieldDecl = 6;
    private const int EnumConstantDecl = 7;
    private const int FunctionDecl = 8;
    private const int VarDecl = 9;
    private const int ParmDecl = 10;
    private const int TypedefDecl = 20;
    private const int TypeRef = 43;
    private const int MacroDefinition = 501;
    private const int CommentToken = 4;
    private const int PackedAttr = 408;
    private const int InternalLinkage = 2;
    private const int DefaultVisibility = 3;

    private readonly NativeBindingHeaderAst _ast = new();
    private readonly Dictionary<uint, List<(NativeClangCursor Cursor, string Id)>> _identities = [];
    private readonly HashSet<string> _typedefs = new(StringComparer.Ordinal);
    private int _count;

    /// <summary>
    /// Reads every file-scope declaration in order, and the declarations their types reach.
    /// </summary>
    /// <returns>The declarations.</returns>
    internal NativeBindingHeaderAst Read()
    {
        NativeClangCursor root = ((delegate* unmanaged[Cdecl]<nint, NativeClangCursor>)library.Export("clang_getTranslationUnitCursor"))(unit.DangerousGetHandle());
        library.LoadDeclarations(root);

        // A translation unit with system headers and macro definitions can far exceed a record's member bound.
        foreach (NativeClangCursor cursor in library.Children(root, limit: 10_000_000))
        {
            switch (cursor.Kind)
            {
                case StructDecl or UnionDecl:
                    string record = ReadRecord(cursor, parent: null);
                    _ast.TopLevelOrder.Add(record);
                    _ = _ast.FileScope.Add(record);
                    break;
                case EnumDecl:
                    _ast.TopLevelOrder.Add(ReadEnum(cursor));
                    break;
                case TypedefDecl:
                    _ast.TopLevelOrder.Add(ReadTypedef(cursor));
                    break;
                case FunctionDecl:
                    _ast.Functions.Add(new AstFunction(library.Name(cursor), Function(library.Type(cursor), cursor),
                        Linkage(cursor) == InternalLinkage, Flag(cursor, "clang_Cursor_isFunctionInlined"),
                        ((delegate* unmanaged[Cdecl]<NativeClangCursor, int>)library.Export("clang_getCursorVisibility"))(cursor) == DefaultVisibility,
                        File(cursor), Comment(cursor)));
                    break;
                case VarDecl:
                    _ast.Variables.Add(ReadVariable(cursor));
                    break;
                case MacroDefinition:
                    bool functionLike = Flag(cursor, "clang_Cursor_isMacroFunctionLike");
                    _ast.Macros.Add(new AstMacro(library.Name(cursor), File(cursor), functionLike, functionLike ? [] : Tokens(cursor)));
                    break;
            }
        }

        return _ast;
    }

    /// <summary>
    /// Reads a struct or union once, with its fields as bindgen collects them: an anonymous member, which libclang
    /// reports only as a nested record, becomes an unnamed field unless a following field or typedef uses its type.
    /// </summary>
    private string ReadRecord(NativeClangCursor cursor, string? parent)
    {
        if (Known(cursor) is string known)
        {
            return known;
        }

        string id = Identify(cursor);
        bool complete = Flag(cursor, "clang_isCursorDefinition");
        NativeClangType type = library.Type(cursor);
        List<NativeClangCursor> children = complete ? library.Children(cursor) : [];
        var record = new AstRecord(id, Anonymous(cursor) ? string.Empty : library.Name(cursor), cursor.Kind == UnionDecl, complete,
            File(cursor), parent)
        {
            IsPacked = children.Any(static child => child.Kind == PackedAttr),
            Size = complete ? library.Measure(type, "clang_Type_getSizeOf") : 0,
            Alignment = complete ? library.Measure(type, "clang_Type_getAlignOf") : 0,
        };
        _ast.Records[id] = record;
        (string Id, NativeClangType Type)? anonymous = null;
        foreach (NativeClangCursor child in children)
        {
            if (child.Kind != FieldDecl && anonymous is (string pending, NativeClangType pendingType))
            {
                // A typedef of an anonymous record follows the record; it names the type instead of using a member.
                if (!(child.Kind == TypedefDecl && Equal(library.Transform(Underlying(child), "clang_getCanonicalType"), pendingType)))
                {
                    record.Fields.Add(AnonymousField(pending));
                }

                anonymous = null;
            }

            switch (child.Kind)
            {
                case FieldDecl:
                    if (anonymous is (string member, NativeClangType memberType))
                    {
                        if (!library.Children(child).Any(grandchild => Equal(library.Type(grandchild), memberType)))
                        {
                            record.Fields.Add(AnonymousField(member));
                        }

                        anonymous = null;
                    }

                    long offset = ((delegate* unmanaged[Cdecl]<NativeClangCursor, long>)library.Export("clang_Cursor_getOffsetOfField"))(child);
                    record.Fields.Add(new AstField(library.Name(child), Convert(library.Type(child), child),
                        Flag(child, "clang_Cursor_isBitField")
                            ? ((delegate* unmanaged[Cdecl]<NativeClangCursor, int>)library.Export("clang_getFieldDeclBitWidth"))(child)
                            : null,
                        offset >= 0 ? offset : null));
                    break;
                case StructDecl or UnionDecl or EnumDecl:
                    // Clang reports undefined tags first named in a member here too; bindgen keeps only definitions and
                    // declarations scoped to this record.
                    if (!Flag(child, "clang_isCursorDefinition") && !Equal(SemanticParent(child), cursor))
                    {
                        break;
                    }

                    if (child.Kind == EnumDecl)
                    {
                        _ = ReadEnum(child);
                        break;
                    }

                    string nested = ReadRecord(child, id);
                    record.Nested.Add(nested);
                    if (((delegate* unmanaged[Cdecl]<NativeClangCursor, uint>)library.Export("clang_Cursor_isAnonymous"))(child) != 0)
                    {
                        anonymous = (nested, library.Type(child));
                    }

                    break;
            }
        }

        if (anonymous is (string last, _))
        {
            record.Fields.Add(AnonymousField(last));
        }

        return id;
    }

    /// <summary>
    /// Reads a variable as bindgen's <c>Var::parse</c> does: an integer, floating-point or string initializer Clang can
    /// evaluate makes it a constant, and otherwise it is foreign storage.
    /// </summary>
    private AstVariable ReadVariable(NativeClangCursor cursor)
    {
        NativeClangType type = library.Type(cursor);
        NativeClangType canonical = library.Transform(type, "clang_getCanonicalType");
        bool constant = TypeFlag(type, "clang_isConstQualifiedType") ||
            type.Kind is 112 or 114 && TypeFlag(library.Transform(type, "clang_getArrayElementType"), "clang_isConstQualifiedType");
        NativeCType converted = Convert(type, cursor);
        AstValue? value = null;
        if (Integer(canonical))
        {
            value = Evaluate(cursor, AstValueKind.Integer);
        }
        else if (canonical.Kind is 21 or 22 or 23)
        {
            value = Evaluate(cursor, AstValueKind.Float);
        }
        else
        {
            value = Evaluate(cursor, AstValueKind.String);
        }

        return new AstVariable(library.Name(cursor), converted, constant, File(cursor), Comment(cursor), value);
    }

    /// <summary>
    /// Evaluates a declaration's initializer through libclang, which bindgen declines when a referenced type is
    /// unexposed.
    /// </summary>
    private AstValue? Evaluate(NativeClangCursor cursor, AstValueKind kind)
    {
        if (library.Children(cursor, recursive: true).Any(child => child.Kind == TypeRef &&
            library.Transform(library.Type(child), "clang_getCanonicalType").Kind == 1))
        {
            return null;
        }

        nint result = ((delegate* unmanaged[Cdecl]<NativeClangCursor, nint>)library.Export("clang_Cursor_Evaluate"))(cursor);
        if (result == 0)
        {
            return null;
        }

        try
        {
            int evaluated = ((delegate* unmanaged[Cdecl]<nint, int>)library.Export("clang_EvalResult_getKind"))(result);
            switch (kind)
            {
                case AstValueKind.Integer when evaluated == 1:
                    if (((delegate* unmanaged[Cdecl]<nint, uint>)library.Export("clang_EvalResult_isUnsignedInt"))(result) != 0)
                    {
                        ulong unsigned = ((delegate* unmanaged[Cdecl]<nint, ulong>)library.Export("clang_EvalResult_getAsUnsigned"))(result);
                        return unsigned > long.MaxValue ? null : new AstValue(kind, unsigned.ToString(CultureInfo.InvariantCulture));
                    }

                    return new AstValue(kind, ((delegate* unmanaged[Cdecl]<nint, long>)library.Export("clang_EvalResult_getAsLongLong"))(result)
                        .ToString(CultureInfo.InvariantCulture));
                case AstValueKind.Float when evaluated == 2:
                    return new AstValue(kind, ((delegate* unmanaged[Cdecl]<nint, double>)library.Export("clang_EvalResult_getAsDouble"))(result)
                        .ToString("R", CultureInfo.InvariantCulture));
                case AstValueKind.String when evaluated == 4:
                    nint text = ((delegate* unmanaged[Cdecl]<nint, nint>)library.Export("clang_EvalResult_getAsStr"))(result);
                    return text == 0 ? null : new AstValue(kind, System.Runtime.InteropServices.Marshal.PtrToStringUTF8(text) ?? string.Empty);
                default:
                    return null;
            }
        }
        finally
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)library.Export("clang_EvalResult_dispose"))(result);
        }
    }

    /// <summary>
    /// Gets whether bindgen treats a canonical type as an integer: a builtin integer, character or <c>_Bool</c>, but
    /// not an enum.
    /// </summary>
    private static bool Integer(NativeClangType type) => type.Kind is >= 3 and <= 20;

    /// <summary>
    /// Tokenizes a macro definition's extent as bindgen does for cexpr, which never sees comments.
    /// </summary>
    private List<NativeCToken> Tokens(NativeClangCursor cursor)
    {
        NativeClangRange range = ((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangRange>)library.Export("clang_getCursorExtent"))(cursor);
        nint translationUnit = unit.DangerousGetHandle();
        NativeClangToken* tokens = null;
        uint count = 0;
        _ = library.Export("clang_disposeTokens");
        ((delegate* unmanaged[Cdecl]<nint, NativeClangRange, NativeClangToken**, uint*, void>)library.Export("clang_tokenize"))(
            translationUnit, range, &tokens, &count);
        try
        {
            var result = new List<NativeCToken>((int)count);
            for (uint index = 0; index < count; index++)
            {
                int kind = ((delegate* unmanaged[Cdecl]<NativeClangToken, int>)library.Export("clang_getTokenKind"))(tokens[index]);
                if (kind is < 0 or > CommentToken)
                {
                    throw new FormatException("Unsupported native token kind.");
                }

                NativeClangString spelling = ((delegate* unmanaged[Cdecl]<nint, NativeClangToken, NativeClangString>)library.Export("clang_getTokenSpelling"))(
                    translationUnit, tokens[index]);
                byte[] bytes = Bytes(spelling);
                if (kind != CommentToken)
                {
                    result.Add(new NativeCToken((NativeCTokenKind)kind, bytes));
                }
            }

            return result;
        }
        finally
        {
            if (tokens != null)
            {
                ((delegate* unmanaged[Cdecl]<nint, NativeClangToken*, uint, void>)library.Export("clang_disposeTokens"))(translationUnit, tokens, count);
            }
        }
    }

    /// <summary>
    /// Copies a native string's bytes without decoding them, then releases it.
    /// </summary>
    private byte[] Bytes(NativeClangString value)
    {
        try
        {
            byte* text = (byte*)((delegate* unmanaged[Cdecl]<NativeClangString, nint>)library.Export("clang_getCString"))(value);
            return text == null ? [] : System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text).ToArray();
        }
        finally
        {
            ((delegate* unmanaged[Cdecl]<NativeClangString, void>)library.Export("clang_disposeString"))(value);
        }
    }

    private AstField AnonymousField(string id)
        => new(string.Empty, new NativeCTag(_ast.Records[id].IsUnion ? NativeCTagKind.Union : NativeCTagKind.Struct, string.Empty, id),
            null, null);

    private string ReadEnum(NativeClangCursor cursor)
    {
        if (Known(cursor) is string known)
        {
            return known;
        }

        string id = Identify(cursor);
        NativeClangType integer = ((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangType>)library.Export("clang_getEnumDeclIntegerType"))(cursor);
        var value = new AstEnum(id, Anonymous(cursor) ? string.Empty : library.Name(cursor), File(cursor), Convert(integer, cursor));
        _ast.Enums[id] = value;
        bool signed = Signed(library.Transform(integer, "clang_getCanonicalType"));
        foreach (NativeClangCursor constant in library.Children(cursor).Where(static constant => constant.Kind == EnumConstantDecl))
        {
            value.Constants.Add((library.Name(constant), signed
                ? ((delegate* unmanaged[Cdecl]<NativeClangCursor, long>)library.Export("clang_getEnumConstantDeclValue"))(constant).ToString(CultureInfo.InvariantCulture)
                : ((delegate* unmanaged[Cdecl]<NativeClangCursor, ulong>)library.Export("clang_getEnumConstantDeclUnsignedValue"))(constant).ToString(CultureInfo.InvariantCulture)));
        }

        return id;
    }

    private string ReadTypedef(NativeClangCursor cursor)
    {
        if (Known(cursor) is string known)
        {
            return known;
        }

        string id = Identify(cursor);
        string name = library.Name(cursor);
        if (_typedefs.Add(name))
        {
            _ast.Typedefs.Add(new AstTypedef(id, name, Convert(Underlying(cursor), cursor), File(cursor)));
        }

        return id;
    }

    /// <summary>
    /// Converts a type as bindgen resolves it at a declaration, which supplies the names of a function type's
    /// parameters.
    /// </summary>
    private NativeCType Convert(NativeClangType type, NativeClangCursor context)
    {
        bool constant = TypeFlag(type, "clang_isConstQualifiedType");
        bool volatileType = TypeFlag(type, "clang_isVolatileQualifiedType");
        NativeCType converted;
        switch (type.Kind)
        {
            case 119:
                converted = Convert(library.Transform(type, "clang_Type_getNamedType"), context);
                break;
            case 163:
                converted = Convert(library.Transform(type, "clang_Type_getModifiedType"), context);
                break;
            case 177:
                converted = Convert(library.Transform(type, "clang_Type_getValueType"), context);
                break;
            case 1 or 182:
                // Clang 21 and later wrap predefined types such as __size_t in sugar that older libclang, as bindgen
                // saw it, reported as the canonical integer.
                NativeClangType canonical = library.Transform(type, "clang_getCanonicalType");
                converted = canonical.Kind is not (1 or 182) ? Convert(canonical, context) : new NativeCUnsupported(library.Spelling(type));
                break;
            case 107:
                NativeClangCursor alias = library.Declaration(type);
                _ = ReadTypedef(alias);
                converted = new NativeCTypedef(library.Name(alias));
                break;
            case 105:
                NativeClangCursor record = library.Declaration(type);
                string recordId = ReadRecord(record, parent: null);
                converted = new NativeCTag(record.Kind == UnionDecl ? NativeCTagKind.Union : NativeCTagKind.Struct, _ast.Records[recordId].Name, recordId);
                break;
            case 106:
                string enumId = ReadEnum(library.Declaration(type));
                converted = new NativeCTag(NativeCTagKind.Enum, _ast.Enums[enumId].Name, enumId);
                break;
            case 101:
                _ast.Scalars.TryAdd("void *", Layout(type));
                converted = new NativeCPointer(Convert(library.Transform(type, "clang_getPointeeType"), context));
                break;
            case 112:
                long count = library.Measure(type, "clang_getArraySize");
                converted = new NativeCArray(Convert(library.Transform(type, "clang_getArrayElementType"), context),
                    count >= 0 ? (ulong)count : throw new FormatException("A native array has no extent."));
                break;
            case 114:
                converted = new NativeCArray(Convert(library.Transform(type, "clang_getArrayElementType"), context), null);
                break;
            case 110 or 111:
                converted = Function(type, context);
                break;
            case 100:
                converted = new NativeCComplex(Convert(library.Transform(type, "clang_getElementType"), context));
                break;
            default:
                string? name = Builtin(type);
                if (name is null)
                {
                    converted = new NativeCUnsupported(library.Spelling(type));
                    break;
                }

                if (name != "void")
                {
                    _ast.Scalars.TryAdd(name, Layout(type));
                }

                converted = new NativeCBuiltin(name);
                break;
        }

        return converted with { IsConst = constant || converted.IsConst, IsVolatile = volatileType || converted.IsVolatile };
    }

    /// <summary>
    /// Converts a function type as bindgen's <c>FunctionSig::from_ty</c> does: a function declaration pairs its
    /// arguments with the type's parameters, and any other declaration supplies its parameter declarations, or the
    /// type alone supplies unnamed parameters.
    /// </summary>
    private NativeCFunction Function(NativeClangType type, NativeClangCursor context)
    {
        int count = Math.Max(0, ((delegate* unmanaged[Cdecl]<NativeClangType, int>)library.Export("clang_getNumArgTypes"))(type));
        var types = new List<NativeCType>();
        var names = new List<string?>();
        if (context.Kind == FunctionDecl)
        {
            int arguments = Math.Max(0, ((delegate* unmanaged[Cdecl]<NativeClangCursor, int>)library.Export("clang_Cursor_getNumArguments"))(context));
            for (int index = 0; index < Math.Max(arguments, count); index++)
            {
                NativeClangCursor? argument = index < arguments
                    ? ((delegate* unmanaged[Cdecl]<NativeClangCursor, uint, NativeClangCursor>)library.Export("clang_Cursor_getArgument"))(context, (uint)index)
                    : null;
                string? name = argument is NativeClangCursor named ? library.Name(named) : null;
                names.Add(string.IsNullOrEmpty(name) ? null : name);
                NativeClangCursor location = argument ?? context;
                types.Add(Convert(index < count ? Argument(type, index) : library.Type(location), location));
            }
        }
        else
        {
            foreach (NativeClangCursor parameter in library.Children(context).Where(static parameter => parameter.Kind == ParmDecl))
            {
                string name = library.Name(parameter);
                names.Add(name.Length == 0 ? null : name);
                types.Add(Convert(library.Type(parameter), parameter));
            }

            if (types.Count == 0)
            {
                for (int index = 0; index < count; index++)
                {
                    names.Add(null);
                    types.Add(Convert(Argument(type, index), context));
                }
            }
        }

        // Clang reports a function without a prototype as variadic, but bindgen counts a signature as variadic only
        // when a fixed parameter precedes the ellipsis.
        return new NativeCFunction(Convert(library.Transform(type, "clang_getResultType"), context), types,
            TypeFlag(type, "clang_isFunctionTypeVariadic") && types.Count != 0)
        {
            ParameterNames = names,
            IsDivergent = Divergent(library.Spelling(type)),
        };
    }

    /// <summary>
    /// Detects <c>__attribute__((noreturn))</c> outside any parentheses of a function type's spelling, which is how
    /// bindgen recognizes a function that does not return.
    /// </summary>
    internal static bool Divergent(string spelling)
    {
        const string Attribute = "__attribute__((noreturn))";
        for (int index = spelling.IndexOf(Attribute, StringComparison.Ordinal); index >= 0;
            index = spelling.IndexOf(Attribute, index + 1, StringComparison.Ordinal))
        {
            int depth = 0;
            foreach (char character in spelling.AsSpan(0, index))
            {
                depth += character switch
                {
                    '(' => 1,
                    ')' => -1,
                    _ => 0,
                };
            }

            if (depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    private NativeClangType Argument(NativeClangType type, int index)
        => ((delegate* unmanaged[Cdecl]<NativeClangType, uint, NativeClangType>)library.Export("clang_getArgType"))(type, (uint)index);

    private NativeClangType Underlying(NativeClangCursor typedef)
        => ((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangType>)library.Export("clang_getTypedefDeclUnderlyingType"))(typedef);

    private (long Size, long Alignment) Layout(NativeClangType type)
        => (library.Measure(type, "clang_Type_getSizeOf"), library.Measure(type, "clang_Type_getAlignOf"));

    private static string? Builtin(NativeClangType type) => type.Kind switch
    {
        2 => "void",
        3 => "_Bool",
        4 or 13 => "char",
        5 => "unsigned char",
        8 => "unsigned short",
        9 => "unsigned int",
        10 => "unsigned long",
        11 => "unsigned long long",
        12 => "unsigned __int128",
        14 => "signed char",
        16 => "short",
        17 => "int",
        18 => "long",
        19 => "long long",
        20 => "__int128",
        21 => "float",
        22 => "double",
        23 => "long double",
        _ => null,
    };

    private static bool Signed(NativeClangType type) => type.Kind is 13 or 14 or 16 or 17 or 18 or 19 or 20;

    private bool Anonymous(NativeClangCursor cursor)
        => ((delegate* unmanaged[Cdecl]<NativeClangCursor, uint>)library.Export("clang_Cursor_isAnonymous"))(cursor) != 0;

    private int Linkage(NativeClangCursor cursor)
        => ((delegate* unmanaged[Cdecl]<NativeClangCursor, int>)library.Export("clang_getCursorLinkage"))(cursor);

    private NativeClangCursor SemanticParent(NativeClangCursor cursor)
        => ((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangCursor>)library.Export("clang_getCursorSemanticParent"))(cursor);

    private bool Equal(NativeClangCursor first, NativeClangCursor second)
        => ((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangCursor, uint>)library.Export("clang_equalCursors"))(first, second) != 0;

    private bool Equal(NativeClangType first, NativeClangType second)
        => ((delegate* unmanaged[Cdecl]<NativeClangType, NativeClangType, uint>)library.Export("clang_equalTypes"))(first, second) != 0;

    private bool Flag(NativeClangCursor cursor, string function)
        => ((delegate* unmanaged[Cdecl]<NativeClangCursor, uint>)library.Export(function))(cursor) != 0;

    private bool TypeFlag(NativeClangType type, string function)
        => ((delegate* unmanaged[Cdecl]<NativeClangType, uint>)library.Export(function))(type) != 0;

    private string? Comment(NativeClangCursor cursor)
    {
        string text = library.Text(((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangString>)library.Export("clang_Cursor_getRawCommentText"))(cursor));
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Gets the file a declaration is in, where a macro expansion counts as the place it is expanded, as bindgen's
    /// file allowlist reads it.
    /// </summary>
    private string? File(NativeClangCursor cursor)
    {
        NativeClangLocation location = ((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangLocation>)library.Export("clang_getCursorLocation"))(cursor);
        nint file = 0;
        uint line;
        uint column;
        uint offset;
        ((delegate* unmanaged[Cdecl]<NativeClangLocation, nint*, uint*, uint*, uint*, void>)library.Export("clang_getFileLocation"))(
            location, &file, &line, &column, &offset);
        if (file == 0)
        {
            return null;
        }

        string name = library.Text(((delegate* unmanaged[Cdecl]<nint, NativeClangString>)library.Export("clang_getFileName"))(file));
        return name.Length == 0 ? null : name;
    }

    private string? Known(NativeClangCursor cursor)
    {
        uint hash = ((delegate* unmanaged[Cdecl]<NativeClangCursor, uint>)library.Export("clang_hashCursor"))(cursor);
        if (_identities.TryGetValue(hash, out List<(NativeClangCursor Cursor, string Id)>? candidates))
        {
            foreach ((NativeClangCursor candidate, string id) in candidates)
            {
                if (Equal(candidate, cursor))
                {
                    return id;
                }
            }
        }

        return null;
    }

    private string Identify(NativeClangCursor cursor)
    {
        uint hash = ((delegate* unmanaged[Cdecl]<NativeClangCursor, uint>)library.Export("clang_hashCursor"))(cursor);
        if (!_identities.TryGetValue(hash, out List<(NativeClangCursor Cursor, string Id)>? candidates))
        {
            _identities.Add(hash, candidates = []);
        }

        string id = "d" + (++_count).ToString(CultureInfo.InvariantCulture);
        candidates.Add((cursor, id));
        return id;
    }
}
