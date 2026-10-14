using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ankus.Build;

/// <summary>
/// Projects bindgen signatures into C declarations while retaining selected-header typedef identities.
/// </summary>
internal static partial class NativeBindingCDeclaration
{
    /// <summary>
    /// Declares native storage without substituting a reference platform's typedef representation.
    /// </summary>
    /// <param name="catalog">The named native declarations.</param>
    /// <param name="representation">The complete bindgen type expression.</param>
    /// <param name="name">The generated C identifier.</param>
    /// <returns>A C declaration without a terminating semicolon.</returns>
    internal static string Value(NativeBindingCatalog catalog, string representation, string name)
    {
        ValidateName(name);
        CType type = Parse(catalog, representation);
        if (type is Atom { Name: "void" })
        {
            throw new FormatException("Native storage cannot have void type.");
        }

        return type.Declare(name, false);
    }

    /// <summary>
    /// Declares foreign storage, interpreting bindgen's zero outer extent as an incomplete extern array.
    /// </summary>
    /// <param name="catalog">The named native declarations.</param>
    /// <param name="representation">The global's complete bindgen type.</param>
    /// <param name="name">The generated C identifier.</param>
    /// <returns>A C declaration without an extern specifier or terminating semicolon.</returns>
    internal static string Global(NativeBindingCatalog catalog, string representation, string name)
    {
        ValidateName(name);
        CType type = Parse(catalog, representation, allowIncomplete: true);
        if (type is Atom { Name: "void" })
        {
            throw new FormatException("Native globals cannot have void type.");
        }

        return type.Declare(name, false);
    }

    /// <summary>
    /// Declares a function pointer suitable for checking a selected header's complete prototype.
    /// </summary>
    /// <param name="catalog">The named native declarations.</param>
    /// <param name="function">The versioned foreign function.</param>
    /// <param name="name">The generated C identifier.</param>
    /// <returns>A function-pointer declaration without a terminating semicolon.</returns>
    internal static string FunctionPointer(NativeBindingCatalog catalog, NativeBindingFunction function, string name)
    {
        ValidateName(name);
        ValidateAbi(function.Abi);
        CType result = Parse(catalog, function.ReturnType);
        CType[] parameters = [.. function.Parameters.Select(parameter => Parse(catalog, parameter.Representation))];
        ValidateFunction(result, parameters, function.IsVariadic);
        return new Pointer(new Function(result, parameters, function.IsVariadic), false).Declare(name, false);
    }

    private static CType Parse(NativeBindingCatalog catalog, string representation, bool allowIncomplete = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(representation);
        var reader = new Reader(catalog, representation);
        CType type = reader.ReadType(allowIncomplete);
        reader.End();
        return type;
    }

    /// <summary>
    /// Rejects text which cannot be emitted as one C identifier.
    /// </summary>
    /// <param name="name">The exact identifier to validate.</param>
    internal static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Identifier().IsMatch(name))
        {
            throw new FormatException("Invalid generated C identifier.");
        }
    }

    private static void ValidateAbi(string abi)
    {
        if (abi is not ("C" or "C-unwind"))
        {
            throw new FormatException($"Unsupported native function ABI '{abi}'.");
        }
    }

    private static void ValidateFunction(CType result, IReadOnlyList<CType> parameters, bool variadic)
    {
        if (result is Array || parameters.Any(static parameter => parameter is Atom { Name: "void" }) ||
            variadic && parameters.Count == 0)
        {
            throw new FormatException("Invalid C function result or parameter type.");
        }
    }

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z_0-9]*\z")]
    private static partial Regex Identifier();

    private abstract record CType
    {
        /// <summary>
        /// Places a declarator at the correct precedence and qualifies this level of the type.
        /// </summary>
        internal abstract string Declare(string name, bool readOnly);
    }

    private sealed record Atom(string Name, bool IsUnit = false) : CType
    {
        internal override string Declare(string name, bool readOnly) => (readOnly ? "const " : "") + Name + " " + name;
    }

    private sealed record Pointer(CType Element, bool ReadOnly) : CType
    {
        internal override string Declare(string name, bool readOnly)
        {
            string declarator = "*" + (readOnly ? "const " : "") + name;
            if (Element is Array or Function)
            {
                declarator = "(" + declarator + ")";
            }

            return Element.Declare(declarator, ReadOnly);
        }
    }

    private sealed record Array(CType Element, int Count) : CType
    {
        internal override string Declare(string name, bool readOnly)
            => Element.Declare(name + "[" + (Count == 0 ? "" : Count.ToString(CultureInfo.InvariantCulture)) + "]", readOnly);
    }

    private sealed record Function(CType Result, IReadOnlyList<CType> Parameters, bool Variadic) : CType
    {
        internal override string Declare(string name, bool readOnly)
        {
            if (readOnly)
            {
                throw new FormatException("A function type cannot be const-qualified.");
            }

            string arguments = Parameters.Count == 0 ? "void" : string.Join(", ", Parameters.Select(
                static (parameter, index) => parameter.Declare("ankus_arg" + index.ToString(CultureInfo.InvariantCulture), false)));
            if (Variadic)
            {
                arguments += ", ...";
            }

            return Result.Declare(name + "(" + arguments + ")", false);
        }
    }

    private sealed class Reader(NativeBindingCatalog catalog, string source)
    {
        private int _position;
        private int _depth;

        /// <summary>
        /// Reads one complete type, retaining nested callback and pointer structure.
        /// </summary>
        internal CType ReadType(bool allowIncomplete = false)
        {
            if (++_depth > 128)
            {
                throw new FormatException("Native type nesting exceeds the supported limit.");
            }

            try
            {
                if (Take("*"))
                {
                    bool readOnly = Take("const");
                    if (!readOnly)
                    {
                        Require("mut");
                    }

                    CType element = ReadType();
                    if (element is Atom { IsUnit: true })
                    {
                        throw new FormatException("Rust unit and never types cannot be native pointer elements.");
                    }

                    return new Pointer(element, readOnly);
                }

                if (Take("["))
                {
                    CType element = ReadType();
                    Require(";");
                    SkipTrivia();
                    int start = _position;
                    while (_position < source.Length && char.IsAsciiDigit(source[_position]))
                    {
                        _position++;
                    }

                    if (!int.TryParse(source.AsSpan(start, _position - start), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ||
                        count == 0 && !allowIncomplete)
                    {
                        throw new FormatException("Native arrays require a positive finite extent.");
                    }

                    _ = Take("usize");
                    Require("]");
                    if (element is Atom { Name: "void" })
                    {
                        throw new FormatException("A native array cannot contain void.");
                    }

                    return new Array(element, count);
                }

                if (Take("("))
                {
                    Require(")");
                    return new Atom("void", true);
                }

                if (Take("!"))
                {
                    return new Atom("void", true);
                }

                string path = ReadPath();
                if (path is "::core::option::Option" or "core::option::Option")
                {
                    Require("<");
                    Require("unsafe");
                    Require("extern");
                    SkipTrivia();
                    Require("\"");
                    int start = _position;
                    while (_position < source.Length && source[_position] != '"')
                    {
                        _position++;
                    }

                    ValidateAbi(source[start.._position]);
                    Require("\"");
                    Require("fn");
                    Require("(");
                    var parameters = new List<CType>();
                    bool variadic = false;
                    while (!Take(")"))
                    {
                        if (variadic)
                        {
                            throw new FormatException("Variadic arguments must be last.");
                        }

                        if (Take("..."))
                        {
                            variadic = true;
                        }
                        else
                        {
                            _ = ReadIdentifier();
                            Require(":");
                            parameters.Add(ReadType());
                        }

                        if (Take(")"))
                        {
                            break;
                        }

                        Require(",");
                    }

                    CType result = Take("->") ? ReadType() : new Atom("void");
                    _ = Take(",");
                    Require(">");
                    ValidateFunction(result, parameters, variadic);
                    return new Pointer(new Function(result, parameters, variadic), false);
                }

                return Named(path);
            }
            finally
            {
                _depth--;
            }
        }

        /// <summary>
        /// Rejects trailing expressions instead of emitting only a valid prefix.
        /// </summary>
        internal void End()
        {
            SkipTrivia();
            if (_position != source.Length)
            {
                throw new FormatException("Unexpected trailing native type expression.");
            }
        }

        private Atom Named(string path)
        {
            // Keep native typedefs intact: their target headers, not bindgen's source platform, define the ABI.
            if (catalog.Aliases.ContainsKey(path) ||
                path is "NodeTag" or "Oid" or "Datum" or "TransactionId" or "MultiXactId")
            {
                return new(path);
            }

            if (catalog.Types.TryGetValue(path, out NativeBindingType? type))
            {
                // Bindgen also exposes tags with no same-named typedef (e.g. struct pg_tm).
                // Node roots already use the same C spelling as the selected layout probe.
                return new(type.IsUnion ? "union " + path : type.IsNode ? path : "struct " + path);
            }

            if (path.EndsWith("::Type", StringComparison.Ordinal) && catalog.Enums.ContainsKey(path[..^6]))
            {
                return new(path[..^6]);
            }

            string native = path switch
            {
                "bool" => "bool",
                "i8" => "int8_t",
                "u8" => "uint8_t",
                "i16" => "int16_t",
                "u16" => "uint16_t",
                "i32" => "int32_t",
                "u32" => "uint32_t",
                "i64" => "int64_t",
                "u64" => "uint64_t",
                "isize" => "intptr_t",
                "usize" => "uintptr_t",
                "f32" => "float",
                "f64" => "double",
                "::core::ffi::c_void" => "void",
                "::core::ffi::c_char" => "char",
                "::core::ffi::c_schar" => "signed char",
                "::core::ffi::c_uchar" => "unsigned char",
                "::core::ffi::c_short" => "short",
                "::core::ffi::c_ushort" => "unsigned short",
                "::core::ffi::c_int" => "int",
                "::core::ffi::c_uint" => "unsigned int",
                "::core::ffi::c_long" => "long",
                "::core::ffi::c_ulong" => "unsigned long",
                "::core::ffi::c_longlong" => "long long",
                "::core::ffi::c_ulonglong" => "unsigned long long",
                "::core::ffi::c_float" => "float",
                "::core::ffi::c_double" => "double",
                _ => throw new FormatException($"Unsupported native type '{path}'."),
            };
            return new(native);
        }

        private string ReadPath()
        {
            var path = new StringBuilder(Take("::") ? "::" : "");
            path.Append(ReadIdentifier());
            while (Take("::"))
            {
                path.Append("::").Append(ReadIdentifier());
            }

            return path.ToString();
        }

        private string ReadIdentifier()
        {
            SkipTrivia();
            int start = _position;
            if (_position >= source.Length || !IsNameStart(source[_position]))
            {
                throw new FormatException("Expected a native type identifier.");
            }

            while (_position < source.Length && (IsNameStart(source[_position]) || char.IsAsciiDigit(source[_position])))
            {
                _position++;
            }

            return source[start.._position];
        }

        private void Require(string token)
        {
            if (!Take(token))
            {
                throw new FormatException($"Expected '{token}' in native type expression.");
            }
        }

        private bool Take(string token)
        {
            SkipTrivia();
            if (!source.AsSpan(_position).StartsWith(token, StringComparison.Ordinal))
            {
                return false;
            }

            int end = _position + token.Length;
            if (IsNameStart(token[^1]) && end < source.Length && (IsNameStart(source[end]) || char.IsAsciiDigit(source[end])))
            {
                return false;
            }

            _position = end;
            return true;
        }

        private void SkipTrivia()
        {
            while (_position < source.Length)
            {
                if (char.IsWhiteSpace(source[_position]))
                {
                    _position++;
                    continue;
                }

                if (source.AsSpan(_position).StartsWith("//", StringComparison.Ordinal))
                {
                    while (_position < source.Length && source[_position] != '\n')
                    {
                        _position++;
                    }

                    continue;
                }

                if (!source.AsSpan(_position).StartsWith("/*", StringComparison.Ordinal))
                {
                    break;
                }

                _position += 2;
                int depth = 1;
                while (depth != 0)
                {
                    if (_position >= source.Length)
                    {
                        throw new FormatException("Unterminated native type comment.");
                    }

                    if (source.AsSpan(_position).StartsWith("/*", StringComparison.Ordinal))
                    {
                        depth++;
                        _position += 2;
                    }
                    else if (source.AsSpan(_position).StartsWith("*/", StringComparison.Ordinal))
                    {
                        depth--;
                        _position += 2;
                    }
                    else
                    {
                        _position++;
                    }
                }
            }
        }

        private static bool IsNameStart(char character) => char.IsAsciiLetter(character) || character == '_';
    }
}
