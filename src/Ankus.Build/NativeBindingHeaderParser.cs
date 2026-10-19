using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace Ankus.Build;

/// <summary>
/// Reads structured selected-header types exposed by Clang typeof aliases.
/// </summary>
internal static class NativeBindingHeaderParser
{
    /// <summary>
    /// Identifies generated aliases independently of declarations in included headers.
    /// </summary>
    internal const string AliasPrefix = "ankus_header_type_";

    /// <summary>
    /// Adds unevaluated aliases without calling or taking evaluated addresses of native symbols.
    /// </summary>
    internal static string GenerateSource(string headers, IReadOnlyList<NativeHeaderRequest> requests)
    {
        SortedDictionary<string, NativeHeaderRequest> selected = Select(requests);
        var source = new StringBuilder(headers);
        source.AppendLine();
        source.AppendLine("#if !defined(__clang__)");
        source.AppendLine("#error Native header type collection requires Clang");
        source.AppendLine("#endif");
        foreach ((string name, NativeHeaderRequest request) in selected)
        {
            source.Append("typedef __typeof__(").Append(request.NativeName).Append(") ").Append(AliasPrefix).Append(name).AppendLine(";");
        }

        return source.ToString();
    }

    /// <summary>
    /// Reconstructs collected declarations and asks the native compiler to verify them against the headers.
    /// </summary>
    internal static string GenerateChecks(string headers, IReadOnlyDictionary<string, NativeHeaderSymbol> symbols)
    {
        var source = new StringBuilder(headers);
        source.AppendLine();
        NativeBindingCompilerShims.Write(source, symbols.Values);
        foreach ((string name, NativeHeaderSymbol symbol) in symbols.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            NativeBindingCDeclaration.ValidateName(name);
            NativeBindingCDeclaration.ValidateName(symbol.NativeName);
            string check = "ankus_header_check_" + name;
            source.Append("typedef ").Append(new NativeHeaderPointer(symbol.Type).Declare(check)).AppendLine(";");
            source.Append("_Static_assert(_Generic(&").Append(NativeBindingCompilerShims.Reference(symbol)).Append(", ").Append(check)
                .Append(": 1, default: 0), \"incompatible reconstructed native type: ").Append(name).AppendLine("\");");
        }

        return source.ToString();
    }

    /// <summary>
    /// Reads a complete translation unit and requires every requested symbol exactly once.
    /// </summary>
    internal static IReadOnlyDictionary<string, NativeHeaderSymbol> Read(string json, IReadOnlyList<NativeHeaderRequest> requests)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 512 });
        return Read(document.RootElement, requests);
    }

    /// <summary>
    /// Reads types from an existing document so target metadata and declarations share one compiler observation.
    /// </summary>
    internal static IReadOnlyDictionary<string, NativeHeaderSymbol> Read(JsonElement root, IReadOnlyList<NativeHeaderRequest> requests)
    {
        SortedDictionary<string, NativeHeaderRequest> selected = Select(requests);
        if (Text(root, "kind") != "TranslationUnitDecl")
        {
            throw Invalid("Expected a complete C translation unit.");
        }

        var declarations = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        Index(root);
        Dictionary<string, JsonElement> tags = ResolveTags(declarations);
        var symbols = new SortedDictionary<string, NativeHeaderSymbol>(StringComparer.Ordinal);
        foreach (JsonElement node in Children(root))
        {
            if (Text(node, "kind") != "TypedefDecl" || !Text(node, "name").StartsWith(AliasPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string name = Text(node, "name")[AliasPrefix.Length..];
            if (!selected.TryGetValue(name, out NativeHeaderRequest? request) || symbols.ContainsKey(name))
            {
                throw Invalid("Unexpected or duplicate generated header alias.");
            }

            JsonElement expressionType = SingleChild(node);
            if (Text(expressionType, "kind") != "TypeOfExprType")
            {
                throw Invalid("Header alias does not use typeof.");
            }

            JsonElement[] parts = Children(expressionType);
            if (parts.Length != 2)
            {
                throw Invalid("Incomplete typeof type information.");
            }

            JsonElement expression = parts[0];
            while (Text(expression, "kind") == "ParenExpr")
            {
                expression = SingleChild(expression);
            }

            if (Text(expression, "kind") != "DeclRefExpr")
            {
                throw Invalid("Header alias does not name a native declaration.");
            }

            JsonElement reference = Object(expression, "referencedDecl");
            string expectedKind = request.IsFunction ? "FunctionDecl" : "VarDecl";
            if (Text(reference, "name") != request.NativeName || Text(reference, "kind") != expectedKind ||
                !declarations.TryGetValue(Text(reference, "id"), out JsonElement declaration) ||
                Text(declaration, "kind") != expectedKind || Text(declaration, "name") != request.NativeName)
            {
                throw Invalid("Header alias refers to a different or missing native declaration.");
            }

            NativeHeaderType type = ReadType(parts[1], 0);
            JsonElement[] members = Children(declaration);
            string[] parameterNames = [.. members.Where(static child => Text(child, "kind") == "ParmVarDecl")
                .Select(static child => OptionalText(child, "name") ?? "")];
            NativeHeaderType canonical = type;
            while (canonical is NativeHeaderAlias alias)
            {
                canonical = alias.Underlying;
            }

            NativeHeaderFunction? function = canonical as NativeHeaderFunction;
            if (request.IsFunction && (function is null || function.Parameters.Count != parameterNames.Length))
            {
                throw Invalid("Native function parameters disagree with the compiler type.");
            }

            string[] attributes = [.. members.Select(static child => Text(child, "kind"))
                .Where(static kind => kind.EndsWith("Attr", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            bool noReturn = function?.DoesNotReturn == true || attributes.Any(static attribute => attribute is "C11NoReturnAttr" or "NoReturnAttr");
            symbols.Add(name, new(request.NativeName, OptionalText(declaration, "mangledName") ?? request.NativeName,
                request.IsFunction, type, Array.AsReadOnly(parameterNames), noReturn, declaration.TryGetProperty("tls", out _),
                OptionalText(declaration, "storageClass") ?? "", Array.AsReadOnly(attributes)));
        }

        if (symbols.Count != selected.Count)
        {
            throw Invalid("Missing requested native header aliases.");
        }

        return new ReadOnlyDictionary<string, NativeHeaderSymbol>(symbols);

        void Index(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Invalid compiler node.");
            }

            // Clang writes an empty object for an absent statement child (for example a for-loop initializer).
            if (!node.EnumerateObject().Any())
            {
                return;
            }

            string kind = Text(node, "kind");
            if (kind is "FunctionDecl" or "VarDecl" or "RecordDecl" or "EnumDecl")
            {
                string id = Text(node, "id");
                if (!declarations.TryAdd(id, node))
                {
                    throw Invalid("Duplicate compiler declaration identity.");
                }
            }

            foreach (JsonElement child in Children(node))
            {
                Index(child);
            }
        }

        NativeHeaderType ReadType(JsonElement node, int depth)
        {
            if (depth >= 128)
            {
                throw Invalid("Native header type nesting exceeds the supported limit.");
            }

            string kind = Text(node, "kind");
            JsonElement[] inner = Children(node);
            switch (kind)
            {
                case "BuiltinType":
                    string spelling = Text(Object(node, "type"), "qualType");
                    if (spelling is not ("void" or "bool" or "_Bool" or "char" or "signed char" or "unsigned char" or
                        "short" or "unsigned short" or "int" or "unsigned int" or "long" or "unsigned long" or
                        "long long" or "unsigned long long" or "float" or "double" or "long double" or
                        "__int128" or "unsigned __int128" or "_Float16" or "__bf16" or "__float128" or "__fp16"))
                    {
                        throw Invalid($"Unsupported native builtin '{spelling}'.");
                    }

                    RequireChildren(inner, 0);
                    return new NativeHeaderScalar(spelling);
                case "RecordType":
                case "EnumType":
                    RequireChildren(inner, 0);
                    JsonElement recordReference = Object(node, "decl");
                    if (Text(recordReference, "kind") != (kind == "EnumType" ? "EnumDecl" : "RecordDecl"))
                    {
                        throw Invalid("Native tag has the wrong declaration kind.");
                    }

                    string recordName = Text(recordReference, "name");
                    if (recordName.Length != 0)
                    {
                        NativeBindingCDeclaration.ValidateName(recordName);
                    }

                    string recordId = Text(recordReference, "id");
                    bool foundRecord = declarations.TryGetValue(recordId, out JsonElement record);
                    record = tags.GetValueOrDefault(recordId, record);
                    if (foundRecord && (Text(record, "kind") != Text(recordReference, "kind") || (OptionalText(record, "name") ?? "") != recordName))
                    {
                        throw Invalid("Native tag identity does not match its declaration.");
                    }

                    if (kind == "EnumType")
                    {
                        return new NativeHeaderEnum(recordName, foundRecord
                            ? record.TryGetProperty("fixedUnderlyingType", out _) ||
                                Children(record).Any(static child => Text(child, "kind") == "EnumConstantDecl")
                            : null);
                    }

                    string tag = foundRecord ? Text(record, "tagUsed") : Text(Object(node, "type"), "qualType").Split(' ')[0];
                    if (tag is not ("struct" or "union"))
                    {
                        throw Invalid("Missing native record tag information.");
                    }

                    return new NativeHeaderRecord(recordName, tag == "union", foundRecord ? Boolean(record, "completeDefinition") : null);
                case "TypedefType":
                    RequireChildren(inner, 1);
                    string alias = Text(Object(node, "decl"), "name");
                    NativeBindingCDeclaration.ValidateName(alias);
                    return new NativeHeaderAlias(alias, ReadType(inner[0], depth + 1));
                case "ElaboratedType":
                case "ParenType":
                case "TypeOfType":
                case "MacroQualifiedType":
                    RequireChildren(inner, 1);
                    return ReadType(inner[0], depth + 1);
                case "AttributedType":
                    RequireChildren(inner, 2);
                    // The second child is the compiler's effective type after applying the attribute.
                    return ReadType(inner[1], depth + 1);
                case "TypeOfExprType":
                    RequireChildren(inner, 2);
                    // The compiler supplies the unevaluated expression followed by its resolved type.
                    // That type also reflects typeof_unqual's removal of top-level qualifiers.
                    return ReadType(inner[1], depth + 1);
                case "QualType":
                    RequireChildren(inner, 1);
                    NativeHeaderQualifiers qualifiers = ReadQualifiers(Text(node, "qualifiers"));
                    return new NativeHeaderQualified(ReadType(inner[0], depth + 1), qualifiers);
                case "PointerType":
                    RequireChildren(inner, 1);
                    return new NativeHeaderPointer(ReadType(inner[0], depth + 1));
                case "ConstantArrayType":
                case "IncompleteArrayType":
                    RequireChildren(inner, 1);
                    ulong? size = null;
                    if (kind == "ConstantArrayType")
                    {
                        if (!node.TryGetProperty("size", out JsonElement count) || count.ValueKind != JsonValueKind.Number ||
                            !count.TryGetUInt64(out ulong extent))
                        {
                            throw Invalid("Invalid native array extent.");
                        }

                        size = extent;
                    }

                    string? modifier = OptionalText(node, "sizeModifier");
                    if (modifier is not (null or "static"))
                    {
                        throw Invalid("Unsupported native array size modifier.");
                    }

                    string? bracketQualifiers = OptionalText(node, "indexTypeQualifiers");
                    return new NativeHeaderArray(ReadType(inner[0], depth + 1), size, modifier == "static",
                        bracketQualifiers is null ? NativeHeaderQualifiers.None : ReadQualifiers(bracketQualifiers));
                case "DecayedType":
                case "AdjustedType":
                    RequireChildren(inner, 2);
                    return new NativeHeaderAdjusted(ReadType(inner[0], depth + 1), ReadType(inner[1], depth + 1));
                case "FunctionProtoType":
                case "FunctionNoProtoType":
                    if (inner.Length == 0 ||
                        node.TryGetProperty("regParm", out JsonElement registerCount) &&
                            (registerCount.ValueKind != JsonValueKind.Number || !registerCount.TryGetInt32(out int registerParameters) || registerParameters != 0))
                    {
                        throw Invalid("Unsupported or incomplete native calling convention.");
                    }

                    int convention = Text(node, "cc") switch
                    {
                        "cdecl" => 1,
                        "stdcall" => 2,
                        "fastcall" => 3,
                        "thiscall" => 4,
                        "ms_abi" => 10,
                        "sysv_abi" => 11,
                        "vectorcall" => 12,
                        _ => throw Invalid("Unsupported native calling convention."),
                    };
                    NativeHeaderType[] parameters = [.. inner.Skip(1).Select(child => ReadType(child, depth + 1))];
                    bool hasPrototype = kind == "FunctionProtoType";
                    bool variadic = Boolean(node, "variadic");
                    if (!NativeHeaderFunction.IsValidPrototype(hasPrototype, parameters.Length, variadic))
                    {
                        throw Invalid("Invalid C11 function prototype.");
                    }

                    return new NativeHeaderFunction(ReadType(inner[0], depth + 1), Array.AsReadOnly(parameters),
                        variadic, hasPrototype, Boolean(node, "noreturn"), convention);
                default:
                    throw Invalid($"Unsupported native compiler type '{kind}'.");
            }
        }
    }

    /// <summary>
    /// Resolves tag redeclarations by compiler identity, including types that reference an initial forward declaration.
    /// </summary>
    private static Dictionary<string, JsonElement> ResolveTags(Dictionary<string, JsonElement> declarations)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach ((string id, JsonElement declaration) in declarations)
        {
            if (Text(declaration, "kind") is not ("RecordDecl" or "EnumDecl"))
            {
                continue;
            }

            var chain = new HashSet<string>(StringComparer.Ordinal);
            string currentId = id;
            JsonElement current = declaration;
            while (!roots.ContainsKey(currentId))
            {
                if (!chain.Add(currentId))
                {
                    throw Invalid("Cyclic native tag redeclarations.");
                }

                string? previous = OptionalText(current, "previousDecl");
                if (previous is null)
                {
                    break;
                }

                if (!declarations.TryGetValue(previous, out JsonElement earlier) ||
                    Text(earlier, "kind") != Text(current, "kind") ||
                    OptionalText(earlier, "name") != OptionalText(current, "name") ||
                    OptionalText(earlier, "tagUsed") != OptionalText(current, "tagUsed"))
                {
                    throw Invalid("Native tag redeclaration has a missing or incompatible identity.");
                }

                currentId = previous;
                current = earlier;
            }

            string root = roots.GetValueOrDefault(currentId, currentId);
            foreach (string member in chain)
            {
                roots.Add(member, root);
            }

            if (!definitions.TryGetValue(root, out JsonElement existing))
            {
                definitions.Add(root, declaration);
            }
            else if (HasDefinition(declaration))
            {
                if (HasDefinition(existing))
                {
                    throw Invalid("Duplicate native tag definitions.");
                }

                definitions[root] = declaration;
            }
            else if (!HasDefinition(existing) && declaration.TryGetProperty("fixedUnderlyingType", out _))
            {
                definitions[root] = declaration;
            }
        }

        return roots.ToDictionary(static pair => pair.Key, pair => definitions[pair.Value], StringComparer.Ordinal);

        static bool HasDefinition(JsonElement node) => Boolean(node, "completeDefinition") ||
            Children(node).Any(static child => Text(child, "kind") == "EnumConstantDecl");
    }

    private static SortedDictionary<string, NativeHeaderRequest> Select(IReadOnlyList<NativeHeaderRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var selected = new SortedDictionary<string, NativeHeaderRequest>(StringComparer.Ordinal);
        foreach (NativeHeaderRequest request in requests)
        {
            NativeBindingCDeclaration.ValidateName(request.Name);
            NativeBindingCDeclaration.ValidateName(request.NativeName);
            if (!selected.TryAdd(request.Name, request))
            {
                throw Invalid("Duplicate native header request.");
            }
        }

        return selected;
    }

    private static NativeHeaderQualifiers ReadQualifiers(string text)
    {
        NativeHeaderQualifiers qualifiers = NativeHeaderQualifiers.None;
        foreach (NativeHeaderQualifiers value in text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static qualifier => qualifier switch
            {
                "const" or "__const" or "__const__" => NativeHeaderQualifiers.Const,
                "volatile" or "__volatile" or "__volatile__" => NativeHeaderQualifiers.Volatile,
                "restrict" or "__restrict" or "__restrict__" => NativeHeaderQualifiers.Restrict,
                _ => throw Invalid($"Unsupported native qualifier '{qualifier}'."),
            }))
        {
            if ((qualifiers & value) != 0)
            {
                throw Invalid("Duplicate native type qualifier.");
            }

            qualifiers |= value;
        }

        return qualifiers == NativeHeaderQualifiers.None ? throw Invalid("Missing native type qualifiers.") : qualifiers;
    }

    private static JsonElement[] Children(JsonElement node)
        => !node.TryGetProperty("inner", out JsonElement inner) ? [] : inner.ValueKind == JsonValueKind.Array
            ? [.. inner.EnumerateArray()] : throw Invalid("Invalid compiler child list.");

    private static JsonElement SingleChild(JsonElement node)
    {
        JsonElement[] children = Children(node);
        RequireChildren(children, 1);
        return children[0];
    }

    private static void RequireChildren(JsonElement[] children, int count)
    {
        if (children.Length != count)
        {
            throw Invalid("Unexpected native type child count.");
        }
    }

    private static JsonElement Object(JsonElement node, string name)
        => node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value : throw Invalid($"Missing compiler object '{name}'.");

    private static string Text(JsonElement node, string name)
        => OptionalText(node, name) ?? throw Invalid($"Missing compiler text '{name}'.");

    private static string? OptionalText(JsonElement node, string name)
        => node.ValueKind != JsonValueKind.Object ? throw Invalid("Invalid compiler node.")
            : !node.TryGetProperty(name, out JsonElement value) ? null : value.ValueKind == JsonValueKind.String
            ? value.GetString() : throw Invalid($"Invalid compiler text '{name}'.");

    private static bool Boolean(JsonElement node, string name)
        => node.TryGetProperty(name, out JsonElement value) && (value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw Invalid($"Invalid compiler flag '{name}'."));

    private static FormatException Invalid(string message) => new(message);
}

/// <summary>
/// Associates a requested binding name with its selected-header C declaration.
/// </summary>
/// <param name="Name">The binding inventory name.</param>
/// <param name="NativeName">The C declaration's identifier, before native linker aliasing.</param>
/// <param name="IsFunction">Whether the requested symbol must be a function; otherwise it must be a global.</param>
internal sealed record NativeHeaderRequest(string Name, string NativeName, bool IsFunction);

/// <summary>
/// Retains the selected header's type and declaration metadata without asserting runtime call availability.
/// </summary>
/// <param name="NativeName">The actual C identifier.</param>
/// <param name="LinkageName">The compiler's linker symbol spelling.</param>
/// <param name="IsFunction">Whether this is a function declaration.</param>
/// <param name="Type">The selected target's semantic type.</param>
/// <param name="ParameterNames">Ordered native parameter names, including empty unnamed parameters.</param>
/// <param name="DoesNotReturn">Whether the declaration or type carries a no-return contract.</param>
/// <param name="IsThreadLocal">Whether the global has thread-local storage.</param>
/// <param name="StorageClass">The compiler's declared storage class, or an empty string when absent.</param>
/// <param name="Attributes">The declaration's compiler attribute kinds.</param>
internal sealed record NativeHeaderSymbol(string NativeName, string LinkageName, bool IsFunction, NativeHeaderType Type,
    IReadOnlyList<string> ParameterNames, bool DoesNotReturn, bool IsThreadLocal, string StorageClass, IReadOnlyList<string> Attributes);
