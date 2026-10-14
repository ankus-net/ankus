using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Derives the native declaration catalog from the selected PostgreSQL headers, applying the rules of pgrx's bindgen
/// configuration so the result matches the catalog pgrx generates: PostgreSQL's include directories are allowlisted
/// with every type they reference, enums become constant modules except the rustified <c>NodeTag</c>, <c>size_t</c> is
/// <c>usize</c>, non-Copy union members are wrapped in <c>ManuallyDrop</c>, nested anonymous types are named after
/// their parent, and <c>Datum</c>, <c>Oid</c>, <c>TransactionId</c> and <c>MultiXactId</c> are blocklisted.
/// </summary>
internal sealed class NativeBindingHeaderCatalog
{
    private static readonly HashSet<string> s_blocklistedTypes = new(StringComparer.Ordinal)
    {
        "Datum", "Oid", "TransactionId", "MultiXactId",
    };

    /// <summary>
    /// The typedefs bindgen renders as Rust primitives instead of declaring aliases, with <c>size_t</c> as <c>usize</c>.
    /// </summary>
    private static readonly Dictionary<string, string> s_primitiveTypedefs = new(StringComparer.Ordinal)
    {
        ["size_t"] = "usize",
        ["ssize_t"] = "isize",
        ["int8_t"] = "i8",
        ["uint8_t"] = "u8",
        ["int16_t"] = "i16",
        ["uint16_t"] = "u16",
        ["int32_t"] = "i32",
        ["uint32_t"] = "u32",
        ["int64_t"] = "i64",
        ["uint64_t"] = "u64",
        ["intptr_t"] = "isize",
        ["uintptr_t"] = "usize",
        ["ptrdiff_t"] = "isize",
    };

    private static readonly Dictionary<string, string> s_builtins = new(StringComparer.Ordinal)
    {
        ["void"] = "::core::ffi::c_void",
        ["_Bool"] = "bool",
        ["char"] = "::core::ffi::c_char",
        ["signed char"] = "::core::ffi::c_schar",
        ["unsigned char"] = "::core::ffi::c_uchar",
        ["short"] = "::core::ffi::c_short",
        ["unsigned short"] = "::core::ffi::c_ushort",
        ["int"] = "::core::ffi::c_int",
        ["unsigned int"] = "::core::ffi::c_uint",
        ["long"] = "::core::ffi::c_long",
        ["unsigned long"] = "::core::ffi::c_ulong",
        ["long long"] = "::core::ffi::c_longlong",
        ["unsigned long long"] = "::core::ffi::c_ulonglong",
        ["__int128"] = "i128",
        ["unsigned __int128"] = "u128",
        ["float"] = "f32",
        ["double"] = "f64",
    };

    private static readonly HashSet<string> s_rustKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "alignof", "as", "async", "await", "become", "box", "break", "const", "continue", "crate", "do", "dyn",
        "else", "enum", "extern", "false", "final", "fn", "for", "gen", "if", "impl", "in", "let", "loop", "macro", "match",
        "mod", "move", "mut", "offsetof", "override", "priv", "proc", "pub", "pure", "ref", "return", "Self", "self", "sizeof",
        "static", "struct", "super", "trait", "true", "try", "type", "typeof", "unsafe", "unsized", "use", "virtual", "where",
        "while", "yield", "str", "bool", "f32", "f64", "usize", "isize", "u128", "i128", "u64", "i64", "u32", "i32", "u16",
        "i16", "u8", "i8", "_",
    };

    private readonly NativeBindingHeaderAst _ast;
    private readonly string _includeRoot;
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AstTypedef> _typedefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _recordsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _enumsByName = new(StringComparer.Ordinal);
    private readonly HashSet<string> _included = new(StringComparer.Ordinal);
    private readonly Queue<string> _pending = new();
    private readonly Dictionary<string, bool> _copy = new(StringComparer.Ordinal);
    private readonly HashSet<string> _fileScope = new(StringComparer.Ordinal);
    private readonly int _major;

    private NativeBindingHeaderCatalog(NativeBindingHeaderAst ast, string includeRoot, int major)
    {
        _ast = ast;
        _major = major;
        includeRoot = includeRoot.Replace('\\', '/');
        _includeRoot = includeRoot.EndsWith('/') ? includeRoot : includeRoot + "/";
    }

    /// <summary>
    /// Builds the type catalog for one PostgreSQL major from its headers.
    /// </summary>
    /// <param name="ast">The declarations of the major's header manifest.</param>
    /// <param name="major">The PostgreSQL major.</param>
    /// <param name="includeRoot">The server include directory, whose declarations are allowlisted.</param>
    /// <returns>The node tags, records, aliases and enums, with node relationships resolved.</returns>
    internal static NativeBindingCatalog BuildTypes(NativeBindingHeaderAst ast, int major, string includeRoot)
        => Build(ast, major, includeRoot).Types;

    /// <summary>
    /// Builds the type catalog and the foreign function and global inventory for one PostgreSQL major from its headers.
    /// </summary>
    /// <param name="ast">The declarations of the major's header manifest.</param>
    /// <param name="major">The PostgreSQL major.</param>
    /// <param name="includeRoot">The server include directory, whose declarations are allowlisted.</param>
    /// <returns>The type catalog and the raw inventory.</returns>
    internal static NativeHeaderCatalogs Build(NativeBindingHeaderAst ast, int major, string includeRoot)
    {
        ArgumentNullException.ThrowIfNull(ast);
        var builder = new NativeBindingHeaderCatalog(ast, includeRoot, major);
        builder.Name();
        builder.Allowlist();
        return new NativeHeaderCatalogs(builder.Emit(major), builder.EmitRaw(major));
    }

    /// <summary>
    /// Assigns bindgen's names: tags and typedef-named anonymous types keep theirs, nested anonymous records become
    /// <c>Parent__bindgen_ty_N</c>, and other anonymous types <c>_bindgen_ty_N</c>.
    /// </summary>
    private void Name()
    {
        foreach (AstTypedef typedef in _ast.Typedefs)
        {
            _typedefs.TryAdd(typedef.Name, typedef);
        }

        int topLevel = 0;
        foreach (string id in _ast.TopLevelOrder)
        {
            if (Anonymous(id) && !_names.ContainsKey(id))
            {
                _names[id] = "_bindgen_ty_" + (++topLevel).ToString(CultureInfo.InvariantCulture);
            }

            if (_ast.Records.TryGetValue(id, out AstRecord? record))
            {
                NameNested(record);
            }
        }

        foreach (AstRecord record in _ast.Records.Values)
        {
            string name = NameOf(record.Id);
            if (_ast.FileScope.Contains(record.Id))
            {
                _fileScope.Add(name);
            }

            if (record.IsComplete || !_recordsByName.ContainsKey(name))
            {
                _recordsByName[name] = record.Id;
            }
        }

        foreach (AstEnum value in _ast.Enums.Values)
        {
            string name = NameOf(value.Id);
            if (value.Constants.Count != 0)
            {
                _enumsByName[name] = value.Id;
            }
            else
            {
                _enumsByName.TryAdd(name, value.Id);
            }
        }
    }

    private void NameNested(AstRecord parent)
    {
        int count = 0;
        foreach (string id in parent.Nested)
        {
            AstRecord nested = _ast.Records[id];
            if (nested.Name.Length == 0 && !_names.ContainsKey(id))
            {
                _names[id] = NameOf(parent.Id) + "__bindgen_ty_" + (++count).ToString(CultureInfo.InvariantCulture);
            }

            NameNested(nested);
        }
    }

    private bool Anonymous(string id)
        => _ast.Records.TryGetValue(id, out AstRecord? record) ? record.Name.Length == 0 :
            _ast.Enums.TryGetValue(id, out AstEnum? value) && value.Name.Length == 0;

    private string NameOf(string id)
        => _names.TryGetValue(id, out string? name) ? name :
            _ast.Records.TryGetValue(id, out AstRecord? record) ? record.Name : _ast.Enums[id].Name;

    /// <summary>
    /// Includes PostgreSQL's own declarations, <c>PGERROR</c> and <c>SIG*</c>, then every type they reference.
    /// </summary>
    private void Allowlist()
    {
        foreach (AstRecord record in _ast.Records.Values.Where(record => _ast.FileScope.Contains(record.Id) && Allowed(record.File, NameOf(record.Id))))
        {
            Include(_recordsByName[NameOf(record.Id)]);
        }

        // Bindgen also allowlists an anonymous enum when one of its constants is an allowlisted item.
        foreach (AstEnum value in _ast.Enums.Values.Where(value => Allowed(value.File, NameOf(value.Id)) ||
                Anonymous(value.Id) && value.Constants.Any(static constant => AllowlistedItem(constant.Name))))
        {
            Include(value.Id);
        }

        foreach (AstTypedef typedef in _typedefs.Values.Where(typedef => Allowed(typedef.File, typedef.Name)))
        {
            Include("typedef:" + typedef.Name);
        }

        foreach (AstFunction function in _ast.Functions.Where(function => Allowed(function.File, function.Name) && !NativeBindingHeaderRules.IsBlocklistedFunction(function.Name, _major)))
        {
            Reference(function.Type);
        }

        foreach (AstVariable variable in _ast.Variables.Where(variable => Allowed(variable.File, variable.Name)))
        {
            Reference(variable.Type);
        }

        while (_pending.TryDequeue(out string? id))
        {
            if (id.StartsWith("typedef:", StringComparison.Ordinal))
            {
                Reference(_typedefs[id[8..]].Type);
            }
            else if (_ast.Records.TryGetValue(id, out AstRecord? record))
            {
                // A named struct declared inside another is still at file scope in C, so its definition elsewhere counts.
                foreach (string nested in record.Nested)
                {
                    Include(Anonymous(nested) ? nested : _recordsByName[NameOf(nested)]);
                }

                foreach (AstField field in record.Fields)
                {
                    Reference(field.Type);
                }
            }
        }
    }

    private bool Allowed(string? file, string name)
        => file is not null && file.Replace('\\', '/').StartsWith(_includeRoot, StringComparison.Ordinal) || AllowlistedItem(name);

    private static bool AllowlistedItem(string name) => name == "PGERROR" || name.StartsWith("SIG", StringComparison.Ordinal);

    private void Include(string id)
    {
        if (_included.Add(id))
        {
            _pending.Enqueue(id);
        }
    }

    private void Reference(NativeCType type)
    {
        switch (type)
        {
            case NativeCTypedef typedef when _typedefs.ContainsKey(typedef.Name) && !s_primitiveTypedefs.ContainsKey(typedef.Name):
                Include("typedef:" + typedef.Name);
                break;
            case NativeCTag tag:
                Include(Resolve(tag));
                break;
            case NativeCPointer pointer:
                Reference(pointer.Element);
                break;
            case NativeCArray array:
                Reference(array.Element);
                break;
            case NativeCComplex complex:
                Reference(complex.Element);
                break;
            case NativeCFunction function:
                Reference(function.Result);
                foreach (NativeCType parameter in function.Parameters)
                {
                    Reference(parameter);
                }

                break;
        }
    }

    /// <summary>
    /// Finds the declaration a tag names: a named tag's definition wherever it is, since C tags share one scope, or the
    /// anonymous declaration itself.
    /// </summary>
    private string Resolve(NativeCTag tag)
        => tag.Name.Length == 0 ? tag.Declaration
            : tag.Kind == NativeCTagKind.Enum ? _enumsByName.GetValueOrDefault(tag.Name, tag.Declaration)
            : _recordsByName.GetValueOrDefault(tag.Name, tag.Declaration);

    private NativeBindingCatalog Emit(int major)
    {
        var tags = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        var types = new SortedDictionary<string, NativeBindingType>(StringComparer.Ordinal);
        var aliases = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var enums = new SortedDictionary<string, NativeBindingEnum>(StringComparer.Ordinal);
        foreach (string id in _included)
        {
            if (id.StartsWith("typedef:", StringComparison.Ordinal))
            {
                AstTypedef typedef = _typedefs[id[8..]];
                if (AliasOf(typedef) is string alias)
                {
                    aliases[typedef.Name] = alias;
                }
            }
            else if (_ast.Records.TryGetValue(id, out AstRecord? record) && _recordsByName.GetValueOrDefault(NameOf(id), id) == id)
            {
                // Bindgen emits a forward-declared union as an opaque struct.
                bool union = record.IsUnion && (record.IsComplete || !_fileScope.Contains(NameOf(id)));
                types[NameOf(id)] = new NativeBindingType(NameOf(id), union, Fields(record), false, []);
            }
            else if (_ast.Enums.TryGetValue(id, out AstEnum? value))
            {
                string name = NameOf(id);
                if (name == "NodeTag")
                {
                    foreach ((string constant, string number) in value.Constants)
                    {
                        tags[constant] = uint.Parse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    }
                }
                else if (value.Constants.Count != 0)
                {
                    enums[name] = new NativeBindingEnum(Render(value.IntegerType),
                        new ReadOnlyDictionary<string, string>(value.Constants.ToDictionary(static constant => constant.Name,
                            static constant => constant.Value, StringComparer.Ordinal)));
                }
            }
        }

        NativeBindingParser.ResolveNodes(types, tags, aliases, major);
        return new NativeBindingCatalog(major, tags, types, aliases, enums);
    }

    /// <summary>
    /// Declares the allowlisted foreign functions and globals as bindgen's codegen does: the first declaration of a
    /// name wins, a hidden or variadic static function or a non-static inline one has no binding, a static function
    /// links to pgrx's C shim wrapper, and a variable with an evaluated initializer is a constant instead.
    /// </summary>
    private NativeBindingRawCatalog EmitRaw(int major)
    {
        var functions = new SortedDictionary<string, NativeBindingFunction>(StringComparer.Ordinal);
        foreach (AstFunction function in _ast.Functions)
        {
            if (!Allowed(function.File, function.Name) || NativeBindingHeaderRules.IsBlocklistedFunction(function.Name, _major) ||
                !function.IsVisible || function.IsInline && !function.IsInternal || function.IsInternal && function.Type.IsVariadic ||
                functions.ContainsKey(function.Name))
            {
                continue;
            }

            var attributes = new List<string>();
            if (Documentation(function.Comment) is string documentation)
            {
                attributes.Add(documentation);
            }

            string symbol = function.Name;
            if (function.IsInternal)
            {
                symbol += "__pgrx_cshim";
                attributes.Add("#[link_name = \"" + symbol + "\"]");
            }

            var parameters = new List<NativeBindingParameter>();
            int unnamed = 0;
            for (int index = 0; index < function.Type.Parameters.Count; index++)
            {
                string? name = index < function.Type.ParameterNames.Count ? function.Type.ParameterNames[index] : null;
                parameters.Add(new NativeBindingParameter(name is null ? "arg" + (++unnamed).ToString(CultureInfo.InvariantCulture) : Mangle(name),
                    Parameter(function.Type.Parameters[index])));
            }

            string result = function.Type.IsDivergent ? "!"
                : function.Type.Result is NativeCBuiltin { Name: "void" } ? "()"
                : Render(function.Type.Result);
            functions.Add(function.Name, new NativeBindingFunction(symbol, "C-unwind", parameters.AsReadOnly(), result,
                function.Type.IsVariadic, attributes.AsReadOnly()));
        }

        var globals = new SortedDictionary<string, NativeBindingGlobal>(StringComparer.Ordinal);
        foreach (AstVariable variable in _ast.Variables)
        {
            if (!Allowed(variable.File, variable.Name) || variable.Value is not null || globals.ContainsKey(variable.Name))
            {
                continue;
            }

            string[] attributes = Documentation(variable.Comment) is string documentation ? [documentation] : [];
            globals.Add(variable.Name, new NativeBindingGlobal(variable.Name, "C-unwind", Render(variable.Type with { IsConst = false, IsVolatile = false }),
                !variable.IsConst, attributes));
        }

        return new NativeBindingRawCatalog(major, new ReadOnlyDictionary<string, NativeBindingFunction>(functions),
            new ReadOnlyDictionary<string, NativeBindingGlobal>(globals),
            new ReadOnlyDictionary<string, NativeBindingConstant>(EmitConstants()));
    }

    /// <summary>
    /// Evaluates every object-like macro in definition order as bindgen does, so each sees the macros before it, then
    /// declares the allowlisted ones, and the variables with evaluated initializers, as constants. A redefined macro keeps
    /// its first declaration, and pgrx retypes built-in OID constants as <c>Oid</c>.
    /// </summary>
    private SortedDictionary<string, NativeBindingConstant> EmitConstants()
    {
        var constants = new SortedDictionary<string, NativeBindingConstant>(StringComparer.Ordinal);
        var evaluated = new Dictionary<string, NativeCConstant>(StringComparer.Ordinal);
        foreach (AstMacro macro in _ast.Macros)
        {
            // Bindgen skips builtin and command-line definitions, pgrx's ignored macros and function-like macros.
            if (macro.File is null || macro.IsFunctionLike || NativeBindingHeaderRules.IsIgnoredMacro(macro.Name) ||
                new NativeCExpression(evaluated).MacroDefinition(macro.Tokens) is not (string name, NativeCConstant value))
            {
                continue;
            }

            bool redefined = evaluated.ContainsKey(name);
            evaluated[name] = value;
            if (redefined || !Allowed(macro.File, name) || NativeBindingHeaderRules.IsBlocklistedVariable(name) || constants.ContainsKey(name) ||
                MacroConstant(name, value) is not NativeBindingConstant constant)
            {
                continue;
            }

            constants.Add(name, constant);
        }

        foreach (AstVariable variable in _ast.Variables)
        {
            if (!(variable.Value is AstValue value && Allowed(variable.File, variable.Name) &&
                !NativeBindingHeaderRules.IsBlocklistedVariable(variable.Name) && !constants.ContainsKey(variable.Name)))
            {
                continue;
            }

            constants.Add(variable.Name, VariableConstant(variable, value));
        }

        return constants;
    }

    /// <summary>
    /// Types a macro's value as bindgen does: an integer as <c>u32</c>, or <c>i32</c> when negative, widening to 64 bits
    /// when it does not fit; a character as <c>u8</c>; a floating-point number as <c>f64</c>; a string as a C string.
    /// </summary>
    private static NativeBindingConstant? MacroConstant(string name, NativeCConstant value)
    {
        switch (value.Kind)
        {
            case NativeCConstantKind.Integer:
                long number = value.IntegerValue;
                string type = number < 0 ? number is < int.MinValue or > int.MaxValue ? "i64" : "i32" : number > uint.MaxValue ? "u64" : "u32";
                string expression = number.ToString(CultureInfo.InvariantCulture);
                return type == "u32" && BuiltinOid(name)
                    ? new NativeBindingConstant("Oid", "Oid(" + expression + ")")
                    : new NativeBindingConstant(type, expression);
            case NativeCConstantKind.Character:
                if (value.IsRawCharacter ? value.CharacterValue > byte.MaxValue : value.CharacterValue > 0x7f)
                {
                    throw new FormatException($"Macro {name} is a character bindgen cannot represent as u8.");
                }

                return new NativeBindingConstant("u8", value.CharacterValue.ToString(CultureInfo.InvariantCulture) + "u8");
            case NativeCConstantKind.Float:
                return new NativeBindingConstant("f64", FloatLiteral(value.FloatValue));
            case NativeCConstantKind.String:
                return StringConstant(value.Bytes!);
            default:
                return null;
        }
    }

    /// <summary>
    /// Types a variable's evaluated initializer by its declared type, as bindgen does for a constant variable.
    /// </summary>
    private NativeBindingConstant VariableConstant(AstVariable variable, AstValue value)
    {
        if (value.Kind == AstValueKind.String)
        {
            return StringConstant(Encoding.UTF8.GetBytes(value.Text));
        }

        string type = Render(variable.Type with { IsConst = false, IsVolatile = false });
        if (value.Kind == AstValueKind.Float)
        {
            return new NativeBindingConstant(type, FloatLiteral(double.Parse(value.Text, CultureInfo.InvariantCulture)));
        }

        if (Canonical(variable.Type) is NativeCBuiltin { Name: "_Bool" })
        {
            return new NativeBindingConstant(type, value.Text == "0" ? "false" : "true");
        }

        // An unsigned type's value prints as its 64-bit unsigned pattern.
        long number = long.Parse(value.Text, CultureInfo.InvariantCulture);
        bool signed = Canonical(variable.Type) is NativeCBuiltin { Name: "char" or "signed char" or "short" or "int" or "long" or "long long" or "__int128" };
        return new NativeBindingConstant(type, signed ? value.Text : unchecked((ulong)number).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Declares a string as bindgen's C string literal, or as a NUL-terminated byte string when it contains a NUL.
    /// </summary>
    private static NativeBindingConstant StringConstant(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) < 0)
        {
            return new NativeBindingConstant("&::core::ffi::CStr", CStringLiteral(bytes));
        }

        var literal = new StringBuilder("b\"");
        byte[] terminated = [.. bytes, 0];
        for (int index = 0; index < terminated.Length; index++)
        {
            byte value = terminated[index];
            literal.Append(value switch
            {
                0 => index + 1 < terminated.Length && terminated[index + 1] is >= (byte)'0' and <= (byte)'7' ? "\\x00" : "\\0",
                (byte)'\t' => "\\t",
                (byte)'\n' => "\\n",
                (byte)'\r' => "\\r",
                (byte)'"' => "\\\"",
                (byte)'\\' => "\\\\",
                >= 0x20 and <= 0x7e => ((char)value).ToString(),
                _ => "\\x" + value.ToString("X2", CultureInfo.InvariantCulture),
            });
        }

        return new NativeBindingConstant("&[u8; " + terminated.Length.ToString(CultureInfo.InvariantCulture) + "]", literal.Append('"').ToString());
    }

    /// <summary>
    /// Writes proc-macro2's C string literal: valid UTF-8 is escaped as a Rust string is, and invalid bytes as hexadecimal.
    /// </summary>
    private static string CStringLiteral(byte[] bytes)
    {
        var literal = new StringBuilder("c\"");
        var decoder = new UTF8Encoding(false, true);
        int position = 0;
        while (position < bytes.Length)
        {
            int valid = ValidUtf8Length(bytes.AsSpan(position));
            literal.Append(EscapeRust(decoder.GetString(bytes, position, valid)));
            position += valid;
            if (position < bytes.Length)
            {
                literal.Append("\\x").Append(bytes[position].ToString("X2", CultureInfo.InvariantCulture));
                position++;
            }
        }

        return literal.Append('"').ToString();
    }

    private static int ValidUtf8Length(ReadOnlySpan<byte> bytes)
    {
        System.Buffers.OperationStatus status = System.Text.Unicode.Utf8.ToUtf16(bytes, new char[bytes.Length], out int read, out _,
            replaceInvalidSequences: false);
        return status == System.Buffers.OperationStatus.Done ? bytes.Length : read;
    }

    /// <summary>
    /// Escapes text as proc-macro2 does for a string literal: Rust's debug escapes, except that a quote is kept.
    /// </summary>
    private static string EscapeRust(string text)
    {
        var escaped = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            escaped.Append(character switch
            {
                '\t' => "\\t",
                '\r' => "\\r",
                '\n' => "\\n",
                '\\' => "\\\\",
                '"' => "\\\"",
                _ when char.IsControl(character) => "\\u{" + ((int)character).ToString("x", CultureInfo.InvariantCulture) + "}",
                _ => character.ToString(),
            });
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Writes a floating-point number as Rust's shortest round-trip display does, without an exponent, and as
    /// proc-macro2 keeps a decimal point.
    /// </summary>
    internal static string FloatLiteral(double value)
    {
        if (double.IsNaN(value))
        {
            return "::core::f64::NAN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "::core::f64::INFINITY" : "::core::f64::NEG_INFINITY";
        }

        string shortest = value.ToString("R", CultureInfo.InvariantCulture);
        string sign = shortest.StartsWith('-') ? "-" : string.Empty;
        shortest = shortest.TrimStart('-');
        int exponentAt = shortest.IndexOfAny(['E', 'e']);
        int exponent = exponentAt < 0 ? 0 : int.Parse(shortest[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        string mantissa = exponentAt < 0 ? shortest : shortest[..exponentAt];
        int point = mantissa.IndexOf('.', StringComparison.Ordinal);
        string digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        int integerDigits = (point < 0 ? mantissa.Length : point) + exponent;
        string plain = integerDigits <= 0 ? "0." + new string('0', -integerDigits) + digits
            : integerDigits >= digits.Length ? digits + new string('0', integerDigits - digits.Length)
            : digits[..integerDigits] + "." + digits[integerDigits..];
        if (plain.Contains('.', StringComparison.Ordinal))
        {
            plain = plain.TrimEnd('0').TrimEnd('.');
        }

        return sign + (plain.Contains('.', StringComparison.Ordinal) ? plain : plain + ".0");
    }

    /// <summary>
    /// Follows pgrx's heuristic for the <c>u32</c> constants it retypes as <c>Oid</c>.
    /// </summary>
    private static bool BuiltinOid(string name)
        => name.EndsWith("OID", StringComparison.Ordinal) && name != "HEAP_HASOID" || name.EndsWith("RelationId", StringComparison.Ordinal) ||
            name == "TemplateDbOid";

    /// <summary>
    /// Renders a raw documentation comment as bindgen's <c>#[doc]</c> attribute, after its comment preprocessing.
    /// </summary>
    internal static string? Documentation(string? comment)
    {
        if (comment is null)
        {
            return null;
        }

        string text;
        if (comment.StartsWith("//", StringComparison.Ordinal))
        {
            text = string.Join("\n", comment.Split('\n').Select(static line => line.TrimEnd('\r').Trim().TrimStart('/')));
        }
        else if (comment.StartsWith("/*", StringComparison.Ordinal))
        {
            string body = comment.TrimStart('/').TrimEnd('/').TrimEnd('*');
            var lines = body.Split('\n').Select(static line => line.TrimEnd('\r').Trim().TrimStart('*').TrimStart('!'))
                .SkipWhile(static line => line.Trim().Length == 0).ToList();
            if (lines.Count != 0 && lines[^1].Trim().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            text = string.Join("\n", lines);
        }
        else
        {
            text = comment;
        }

        if (text.Length == 0)
        {
            return null;
        }

        var literal = new StringBuilder("#[doc = \"");
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            switch (character)
            {
                case '\0':
                    literal.Append(index + 1 < text.Length && text[index + 1] is >= '0' and <= '7' ? "\\x00" : "\\0");
                    break;
                case '\t':
                    literal.Append("\\t");
                    break;
                case '\r':
                    literal.Append("\\r");
                    break;
                case '\n':
                    literal.Append("\\n");
                    break;
                case '\\':
                    literal.Append("\\\\");
                    break;
                case '"':
                    literal.Append("\\\"");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        literal.Append("\\u{").Append(((int)character).ToString("x", CultureInfo.InvariantCulture)).Append('}');
                    }
                    else
                    {
                        literal.Append(character);
                    }

                    break;
            }
        }

        return literal.Append("\"]").ToString();
    }

    /// <summary>
    /// Renders a typedef as bindgen's <c>pub type</c>, or null when bindgen emits none: the typedef names its own tag,
    /// is blocklisted or is mapped to a Rust primitive.
    /// </summary>
    private string? AliasOf(AstTypedef typedef)
    {
        if (s_blocklistedTypes.Contains(typedef.Name) || s_primitiveTypedefs.ContainsKey(typedef.Name) ||
            typedef.Type is NativeCTag tag && NameOf(Resolve(tag)) == typedef.Name)
        {
            return null;
        }

        return Render(typedef.Type with { IsConst = false, IsVolatile = false });
    }

    /// <summary>
    /// Renders a record's fields, packing consecutive bitfields into bindgen's allocation units and adding the padding
    /// bindgen's layout tracker declares.
    /// </summary>
    private ReadOnlyCollection<NativeBindingField> Fields(AstRecord record)
    {
        var fields = new List<NativeBindingField>();
        if (!record.IsComplete)
        {
            // Bindgen parses a file-scope declaration as a forward declaration. A type only ever named inside other
            // records is instead resolved from a field, which bindgen treats as a complete but empty C++-style struct
            // with a one-byte address field.
            if (!_fileScope.Contains(NameOf(record.Id)))
            {
                fields.Add(new NativeBindingField("_address", "_address", "u8"));
            }

            return fields.AsReadOnly();
        }

        // Bindgen wraps every member of a union that cannot derive Copy as a whole.
        bool wrapMembers = record.IsUnion && !IsRecordCopy(record.Id);
        bool packed = record.IsPacked ||
            record.Fields.Any(field => TypeLayout(field.Type) is (_, long alignment) && alignment > record.Alignment);
        var tracker = new LayoutTracker((record.Size, record.Alignment), packed, record.IsUnion, _ast.Scalars["void *"].Size);
        int units = 0;
        int anonymous = 0;
        for (int index = 0; index < record.Fields.Count; index++)
        {
            AstField field = record.Fields[index];
            if (field.BitWidth is not null)
            {
                int end = index;
                while (end + 1 < record.Fields.Count && record.Fields[end + 1].BitWidth is not null)
                {
                    end++;
                }

                foreach ((long size, long alignment) in BitfieldUnits(record.Fields.Skip(index).Take(end - index + 1), packed))
                {
                    units++;
                    string unit = units.ToString(CultureInfo.InvariantCulture);
                    string storage = "__BindgenBitfieldUnit<[u8; " + size.ToString(CultureInfo.InvariantCulture) + "usize]>";
                    fields.Add(new NativeBindingField("_bitfield_align_" + unit, "_bitfield_align_" + unit,
                        "[" + UnitAlignment(alignment) + "; 0]"));
                    fields.Add(new NativeBindingField("_bitfield_" + unit, "_bitfield_" + unit,
                        wrapMembers ? "::core::mem::ManuallyDrop<" + storage + ">" : storage));
                    tracker.SawBitfieldUnit((size, alignment));
                }

                index = end;
                continue;
            }

            // Bindgen declares a field of zero-length or incomplete array type, not through a typedef, as a flexible
            // array member outside unions.
            string representation = !record.IsUnion && field.Type is NativeCArray { Count: null or 0 } flexible
                ? "__IncompleteArrayField<" + Render(flexible.Element) + ">"
                : Render(field.Type);
            if (wrapMembers)
            {
                representation = "::core::mem::ManuallyDrop<" + representation + ">";
            }

            if (FieldLayout(field.Type) is (long, long) fieldLayout && tracker.SawField(fieldLayout, field.Offset) is (long, long) padding)
            {
                fields.Add(tracker.PaddingField(padding));
            }

            string name = field.Name.Length == 0
                ? "__bindgen_anon_" + (++anonymous).ToString(CultureInfo.InvariantCulture)
                : field.Name;
            fields.Add(new NativeBindingField(Mangle(name), name, representation));
        }

        if (!record.IsUnion && fields.Count != 0 && tracker.PadStruct() is (long, long) tail)
        {
            fields.Add(tracker.PaddingField(tail));
        }

        return fields.AsReadOnly();
    }

    /// <summary>
    /// Gets the layout bindgen tracks for a field, which spreads an array of over-aligned elements at eight-byte alignment.
    /// </summary>
    private (long Size, long Alignment)? FieldLayout(NativeCType type)
    {
        (long Size, long Alignment)? layout = TypeLayout(type);
        if (layout is not null && Canonical(type) is NativeCArray { Count: ulong count } array && TypeLayout(array.Element) is (long size, long alignment) &&
            alignment > LayoutTracker.MaxGuaranteedAlignment)
        {
            return (AlignTo(size, alignment) * (long)count, LayoutTracker.MaxGuaranteedAlignment);
        }

        return layout;
    }

    private NativeCType Canonical(NativeCType type)
    {
        while (type is NativeCTypedef typedef && _typedefs.TryGetValue(typedef.Name, out AstTypedef? declaration))
        {
            type = declaration.Type;
        }

        return type;
    }

    /// <summary>
    /// Gets a type's size and alignment on the target, as Clang reports them, or null for an incomplete type.
    /// </summary>
    private (long Size, long Alignment)? TypeLayout(NativeCType type)
    {
        switch (type)
        {
            case NativeCBuiltin { Name: "void" }:
                return null;
            case NativeCBuiltin builtin:
                return _ast.Scalars.TryGetValue(builtin.Name, out (long Size, long Alignment) scalar) ? scalar :
                    throw new FormatException($"No target layout for C type '{builtin.Name}'.");
            case NativeCTypedef typedef:
                return _typedefs.TryGetValue(typedef.Name, out AstTypedef? declaration)
                    ? TypeLayout(declaration.Type)
                    : throw new FormatException($"Unknown C typedef '{typedef.Name}'.");
            case NativeCTag { Kind: NativeCTagKind.Enum } tag:
                return TypeLayout(_ast.Enums[Resolve(tag)].IntegerType);
            case NativeCTag tag:
                return _ast.Records.TryGetValue(Resolve(tag), out AstRecord? record) && record.IsComplete
                    ? (record.Size, record.Alignment) : null;
            case NativeCPointer:
                return _ast.Scalars["void *"];
            case NativeCComplex complex:
                return TypeLayout(complex.Element) is (long part, long partAlignment) ? (part * 2, partAlignment) : null;
            case NativeCArray array:
                if (TypeLayout(array.Element) is not (long size, long alignment))
                {
                    return null;
                }

                return (array.Count is ulong count ? size * (long)count : 0, alignment);
            default:
                return null;
        }
    }

    /// <summary>
    /// Groups consecutive bitfields into allocation units as bindgen 0.72 does after LLVM's Itanium record layout, even
    /// for MSVC targets: a unit's alignment follows its widest bitfield, not the bitfields' declared types.
    /// </summary>
    private IEnumerable<(long Size, long Alignment)> BitfieldUnits(IEnumerable<AstField> bitfields, bool packed)
    {
        long unitSize = 0;
        long unitAlignment = 0;
        foreach (AstField field in bitfields)
        {
            int width = field.BitWidth!.Value;
            (long size, long alignment) = TypeLayout(field.Type) ?? throw new FormatException($"Bitfield {field.Name} has an incomplete type.");
            long offset = unitSize;
            if (!packed && offset != 0 && (width == 0 || (offset & (alignment * 8 - 1)) + width > size * 8))
            {
                offset = AlignTo(offset, alignment * 8);
            }

            if (field.Name.Length != 0)
            {
                unitAlignment = Math.Max(unitAlignment, width);
            }

            unitSize = offset + width;
        }

        if (unitSize != 0)
        {
            yield return (AlignTo(unitSize, 8) / 8, packed ? 1 : BytesFromBitsPow2(unitAlignment));
        }
    }

    private static long AlignTo(long size, long alignment) => alignment == 0 ? size : (size + alignment - 1) / alignment * alignment;

    private static long BytesFromBitsPow2(long bits)
    {
        if (bits == 0)
        {
            return 0;
        }

        long bytes = (bits + 7) / 8;
        long power = 1;
        while (power < bytes)
        {
            power *= 2;
        }

        return power;
    }

    private static string UnitAlignment(long bytes) => bytes switch
    {
        >= 8 => "u64",
        4 => "u32",
        2 => "u16",
        _ => "u8",
    };

    /// <summary>
    /// Follows bindgen's derive analysis for <c>Copy</c>: a zero-length or incomplete array prevents it, and so does any
    /// field that is not <c>Copy</c>.
    /// </summary>
    private bool IsCopy(NativeCType type)
    {
        switch (type)
        {
            case NativeCTypedef typedef when _typedefs.TryGetValue(typedef.Name, out AstTypedef? declaration) &&
                !s_blocklistedTypes.Contains(typedef.Name):
                return IsCopy(declaration.Type);
            case NativeCTag { Kind: not NativeCTagKind.Enum } tag:
                return IsRecordCopy(Resolve(tag));
            case NativeCArray array:
                return array.Count is not (null or 0) && IsCopy(array.Element);
            default:
                return true;
        }
    }

    private bool IsRecordCopy(string id)
    {
        if (_copy.TryGetValue(id, out bool known))
        {
            return known;
        }

        _copy[id] = true;
        AstRecord record = _ast.Records[id];
        bool copy = !record.IsComplete || record.Fields.Where(static field => field.BitWidth is null).All(field => IsCopy(field.Type));
        _copy[id] = copy;
        return copy;
    }

    /// <summary>
    /// Renders a type in bindgen's Rust syntax, which the binding generator reads back.
    /// </summary>
    private string Render(NativeCType type)
    {
        switch (type)
        {
            case NativeCBuiltin { Name: "long double" }:
                // Bindgen picks the Rust type for long double from its size.
                return _ast.Scalars["long double"].Size switch
                {
                    4 => "f32",
                    8 => "f64",
                    16 => "u128",
                    _ => "f64",
                };
            case NativeCBuiltin builtin:
                return s_builtins.TryGetValue(builtin.Name, out string? known) ? known :
                    throw new FormatException($"Unsupported C type '{builtin.Name}'.");
            case NativeCTypedef typedef:
                if (s_primitiveTypedefs.TryGetValue(typedef.Name, out string? primitive))
                {
                    return primitive;
                }

                return _typedefs.TryGetValue(typedef.Name, out AstTypedef? declaration) && EnumModule(declaration) is string module
                    ? module + "::Type"
                    : typedef.Name;
            case NativeCTag tag:
                string name = NameOf(Resolve(tag));
                return tag.Kind == NativeCTagKind.Enum && name != "NodeTag" ? name + "::Type" : name;
            case NativeCPointer pointer when Canonical(pointer.Element) is NativeCFunction:
                // A function pointer is already a pointer in Rust.
                return Render(pointer.Element);
            case NativeCPointer pointer:
                return (pointer.Element.IsConst ? "*const " : "*mut ") + Render(pointer.Element);
            case NativeCArray array:
                return "[" + Render(array.Element) + "; " + (array.Count ?? 0).ToString(CultureInfo.InvariantCulture) + "usize]";
            case NativeCFunction function:
                return Function(function);
            case NativeCComplex complex:
                return "__BindgenComplex<" + Render(complex.Element) + ">";
            case NativeCUnsupported unsupported:
                throw new FormatException($"An allowlisted declaration uses the unsupported C type '{unsupported.Spelling}'.");
            default:
                throw new FormatException($"Unsupported C type {type}.");
        }
    }

    /// <summary>
    /// Gets the enum module a typedef refers to as <c>Module::Type</c>: an enum with the typedef's own name.
    /// </summary>
    private string? EnumModule(AstTypedef typedef)
        => typedef.Type is NativeCTag { Kind: NativeCTagKind.Enum } tag && NameOf(Resolve(tag)) is string name && name == typedef.Name &&
            name != "NodeTag" ? name : null;

    /// <summary>
    /// Renders a function type as bindgen's nullable function pointer, naming unnamed parameters <c>argN</c> by their
    /// own count and passing arrays as pointers to their elements.
    /// </summary>
    private string Function(NativeCFunction function)
    {
        var text = new StringBuilder("::core::option::Option<unsafe extern \"C-unwind\" fn(");
        int unnamed = 0;
        for (int index = 0; index < function.Parameters.Count; index++)
        {
            if (index != 0)
            {
                text.Append(", ");
            }

            string? name = index < function.ParameterNames.Count ? function.ParameterNames[index] : null;
            text.Append(name is null ? "arg" + (++unnamed).ToString(CultureInfo.InvariantCulture) : Mangle(name)).Append(": ")
                .Append(Parameter(function.Parameters[index]));
        }

        if (function.IsVariadic)
        {
            text.Append(", ...");
        }

        text.Append(')');
        if (function.IsDivergent)
        {
            text.Append(" -> !");
        }
        else if (function.Result is not NativeCBuiltin { Name: "void" })
        {
            text.Append(" -> ").Append(Render(function.Result));
        }

        return text.Append('>').ToString();
    }

    /// <summary>
    /// Renders a parameter as bindgen does, which passes an array, including one behind a typedef, as a pointer to its
    /// element.
    /// </summary>
    private string Parameter(NativeCType type)
        => Canonical(type) is NativeCArray array
            ? (array.Element.IsConst ? "*const " : "*mut ") + Render(array.Element)
            : Render(type);

    private static string Mangle(string name) => s_rustKeywords.Contains(name) ? name + "_" : name;
}

/// <summary>
/// The catalogs derived from one major's headers.
/// </summary>
/// <param name="Types">The node tags, records, aliases and enums.</param>
/// <param name="Raw">The foreign functions and globals.</param>
internal sealed record NativeHeaderCatalogs(NativeBindingCatalog Types, NativeBindingRawCatalog Raw);
