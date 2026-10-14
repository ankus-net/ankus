using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Ankus.Build;

/// <summary>
/// Reads pinned bindgen declarations and reproduces pgrx's complete node inheritance and alias rules.
/// Physical offsets and sizes must still come from the selected PostgreSQL headers.
/// </summary>
internal static partial class NativeBindingParser
{
    /// <summary>
    /// Parses one supported PostgreSQL major without interpreting Rust as executable code.
    /// </summary>
    internal static NativeBindingCatalog Parse(string source, int postgresMajor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(postgresMajor, 13);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(postgresMajor, 19);
        string code = MaskTrivia(source);
        Match tagDeclaration = TagDeclaration().Match(code);
        if (!tagDeclaration.Success)
        {
            throw new FormatException("The bindings contain no NodeTag declaration.");
        }

        int start = tagDeclaration.Index + tagDeclaration.Length;
        int end = ReadUntil(code, start, '}');
        var tags = new SortedDictionary<string, uint>(StringComparer.Ordinal);
        var numbers = new HashSet<uint>();
        foreach (string entry in code[start..end].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Match tag = TagEntry().Match(entry);
            if (!tag.Success || !uint.TryParse(tag.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) ||
                !numbers.Add(value) || !tags.TryAdd(tag.Groups["name"].Value, value))
            {
                throw new FormatException($"Invalid or duplicate node tag: {entry}");
            }
        }

        if (!tags.TryGetValue("T_Invalid", out uint invalid) || invalid != 0)
        {
            throw new FormatException("NodeTag must include T_Invalid = 0.");
        }

        var types = new SortedDictionary<string, NativeBindingType>(StringComparer.Ordinal);
        foreach (Match declaration in TypeDeclaration().Matches(code))
        {
            start = declaration.Index + declaration.Length;
            end = ReadUntil(code, start, '}');
            string name = declaration.Groups["name"].Value;
            var fields = new List<NativeBindingField>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match field in FieldDeclaration().Matches(code[start..end]))
            {
                string fieldName = field.Groups["name"].Value;
                if (!names.Add(fieldName))
                {
                    throw new FormatException($"Duplicate field {name}.{fieldName}.");
                }

                int fieldStart = start + field.Index + field.Length;
                int fieldEnd = ReadUntil(code, fieldStart, ',');
                if (fieldEnd >= end)
                {
                    throw new FormatException($"Unterminated field {name}.{fieldName}.");
                }

                fields.Add(new(fieldName, NativeIdentifier(fieldName), source[fieldStart..fieldEnd].Trim()));
            }

            if (!types.TryAdd(name, new(name, declaration.Groups["kind"].Value == "union", fields.AsReadOnly(), false, [])))
            {
                throw new FormatException($"Duplicate native type {name}.");
            }
        }

        var aliases = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (Match alias in AliasDeclaration().Matches(code))
        {
            start = alias.Index + alias.Length;
            string name = alias.Groups["name"].Value;
            if (!aliases.TryAdd(name, source[start..ReadUntil(code, start, ';')].Trim()))
            {
                throw new FormatException($"Duplicate native alias {name}.");
            }
        }

        // Bindgen re-exports a typedef of a constified enum under the typedef's own name instead of declaring an alias.
        foreach (Match alias in EnumReexport().Matches(code))
        {
            string name = alias.Groups["name"].Value;
            if (!aliases.TryAdd(name, alias.Groups["module"].Value + "::Type"))
            {
                throw new FormatException($"Duplicate native alias {name}.");
            }
        }

        var enums = new SortedDictionary<string, NativeBindingEnum>(StringComparer.Ordinal);
        foreach (Match module in ModuleDeclaration().Matches(code))
        {
            start = module.Index + module.Length;
            end = ReadUntil(code, start, '}');
            string body = code[start..end];
            Match storage = EnumStorage().Match(body);
            if (!storage.Success)
            {
                continue;
            }

            int storageStart = start + storage.Index + storage.Length;
            string representation = source[storageStart..ReadUntil(code, storageStart, ';')].Trim();
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (Match constant in EnumConstant().Matches(body))
            {
                int constantStart = start + constant.Index + constant.Length;
                string name = constant.Groups["name"].Value;
                if (!values.TryAdd(name, source[constantStart..ReadUntil(code, constantStart, ';')].Trim()))
                {
                    throw new FormatException($"Duplicate enum constant {name}.");
                }
            }

            if (!enums.TryAdd(module.Groups["name"].Value, new(representation, new ReadOnlyDictionary<string, string>(values))))
            {
                throw new FormatException("Duplicate native enum module.");
            }
        }

        ResolveNodes(types, tags, aliases, postgresMajor);
        return new(postgresMajor, new ReadOnlyDictionary<string, uint>(tags),
            new ReadOnlyDictionary<string, NativeBindingType>(types), new ReadOnlyDictionary<string, string>(aliases),
            new ReadOnlyDictionary<string, NativeBindingEnum>(enums));
    }

    /// <summary>
    /// Reproduces struct prefix inheritance, typedef tag aliases, union directionality and legacy value nodes.
    /// </summary>
    internal static void ResolveNodes(SortedDictionary<string, NativeBindingType> types,
        SortedDictionary<string, uint> tags, SortedDictionary<string, string> aliases, int major)
    {
        Dictionary<string, List<string>> children = types.Keys.ToDictionary(static name => name, static _ => new List<string>(), StringComparer.Ordinal);
        var tagAliases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach ((string alias, string representation) in aliases)
        {
            string target = DirectType(representation);
            if (!types.ContainsKey(target) || !tags.ContainsKey("T_" + alias))
            {
                continue;
            }

            if (!tagAliases.TryGetValue(target, out List<string>? names))
            {
                tagAliases.Add(target, names = []);
            }

            names.Add("T_" + alias);
        }

        foreach ((string name, NativeBindingType type) in types)
        {
            foreach (string parent in (type.IsUnion ? type.Fields : type.Fields.Take(1)).Select(static field => DirectType(field.Representation)))
            {
                if (!types.ContainsKey(parent))
                {
                    continue;
                }

                if (type.IsUnion)
                {
                    children[name].Add(parent);
                }
                else
                {
                    children[parent].Add(name);
                }
            }
        }

        var resolved = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, NativeBindingType type) in types)
        {
            bool root = type.IsUnion
                ? type.Fields.Any(static field => DirectType(field.Representation) == "Node")
                : type.Fields.Count != 0 && DirectType(type.Fields[0].Representation) == "NodeTag";
            if (root)
            {
                Resolve(name);
            }
        }

        if (major <= 14 && resolved.TryGetValue("Value", out SortedSet<string>? valueTags))
        {
            string[] legacy = ["T_Integer", "T_Float", "T_String", "T_BitString", "T_Null"];
            if (legacy.Any(tag => !tags.ContainsKey(tag)))
            {
                throw new FormatException("Legacy Value node tags are incomplete.");
            }

            valueTags.UnionWith(legacy);
        }

        foreach ((string name, SortedSet<string> accepted) in resolved)
        {
            types[name] = types[name] with
            {
                IsNode = true,
                CastTags = Array.AsReadOnly(name == "Node" ? [] : accepted.ToArray())
            };
        }

        SortedSet<string> Resolve(string name)
        {
            if (resolved.TryGetValue(name, out SortedSet<string>? known))
            {
                return known;
            }

            if (!visiting.Add(name))
            {
                throw new FormatException($"Cyclic native node inheritance at {name}.");
            }

            var accepted = new SortedSet<string>(StringComparer.Ordinal);
            if (tags.ContainsKey("T_" + name))
            {
                accepted.Add("T_" + name);
            }

            if (tagAliases.TryGetValue(name, out List<string>? names))
            {
                accepted.UnionWith(names);
            }

            if (!types[name].IsUnion)
            {
                foreach (string child in children[name])
                {
                    accepted.UnionWith(Resolve(child));
                }
            }

            visiting.Remove(name);
            resolved.Add(name, accepted);
            return accepted;
        }
    }

    /// <summary>
    /// Reads a top-level delimiter while respecting nested type and callback expressions.
    /// </summary>
    internal static int ReadUntil(string code, int start, char delimiter, bool trackAngles = true)
    {
        var closes = new Stack<char>();
        for (int index = start; index < code.Length; index++)
        {
            char character = code[index];
            if (closes.Count == 0 && character == delimiter)
            {
                return index;
            }

            if (character is '(' or '[' or '{' || trackAngles && character == '<')
            {
                closes.Push(character switch
                {
                    '(' => ')',
                    '[' => ']',
                    '{' => '}',
                    _ => '>'
                });
            }
            else if (character is ')' or ']' or '}' || trackAngles && character == '>')
            {
                if (character == '>' && index != 0 && code[index - 1] == '-')
                {
                    continue;
                }

                if (closes.Count == 0 || closes.Pop() != character)
                {
                    throw new FormatException("Unbalanced bindgen declaration.");
                }
            }
        }

        throw new FormatException("Unterminated bindgen declaration.");
    }

    /// <summary>
    /// Masks comments and string literals without moving offsets used to preserve original type representations.
    /// </summary>
    internal static string MaskTrivia(string source)
    {
        char[] code = source.ToCharArray();
        for (int index = 0; index < source.Length; index++)
        {
            int start = index;
            if (source[index] == '"')
            {
                bool closed = false;
                while (++index < source.Length)
                {
                    if (source[index] == '\\')
                    {
                        index++;
                    }
                    else if (source[index] == '"')
                    {
                        closed = true;
                        break;
                    }
                }

                if (!closed)
                {
                    throw new FormatException("Unterminated bindgen string.");
                }
            }
            else if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index + 1 < source.Length && source[index + 1] != '\n')
                {
                    index++;
                }
            }
            else if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                int depth = 1;
                index++;
                while (++index + 1 < source.Length)
                {
                    if (source[index] == '/' && source[index + 1] == '*')
                    {
                        depth++;
                        index++;
                    }
                    else if (source[index] == '*' && source[index + 1] == '/')
                    {
                        index++;
                        if (--depth == 0)
                        {
                            break;
                        }
                    }
                }

                if (depth != 0)
                {
                    throw new FormatException("Unterminated bindgen comment.");
                }
            }
            else
            {
                continue;
            }

            for (int position = start; position <= index; position++)
            {
                if (code[position] is not ('\r' or '\n'))
                {
                    code[position] = ' ';
                }
            }
        }

        return new string(code);
    }

    private static string DirectType(string representation)
    {
        string code = MaskTrivia(representation).Trim();
        return DirectTypePattern().IsMatch(code) ? code.Split("::", StringSplitOptions.None)[^1] : string.Empty;
    }

    private static string NativeIdentifier(string name)
        => name.EndsWith('_') && BindgenReservedName().IsMatch(name[..^1]) ? name[..^1] : name;

    [GeneratedRegex(@"(?m)^pub enum NodeTag\s*\{")]
    private static partial Regex TagDeclaration();

    [GeneratedRegex(@"^(?<name>T_\w+)\s*=\s*(?<value>\d+)$")]
    private static partial Regex TagEntry();

    [GeneratedRegex(@"(?m)^pub (?<kind>struct|union) (?<name>\w+)\s*\{")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"(?m)^[ \t]+pub (?<name>\w+):[ \t]*")]
    private static partial Regex FieldDeclaration();

    [GeneratedRegex(@"(?m)^pub type (?<name>\w+)\s*=[ \t]*")]
    private static partial Regex AliasDeclaration();

    [GeneratedRegex(@"(?m)^pub use self::(?<module>\w+)::Type as (?<name>\w+);")]
    private static partial Regex EnumReexport();

    [GeneratedRegex(@"(?m)^pub mod (?<name>\w+)\s*\{")]
    private static partial Regex ModuleDeclaration();

    [GeneratedRegex(@"(?m)^[ \t]+pub type Type\s*=[ \t]*")]
    private static partial Regex EnumStorage();

    [GeneratedRegex(@"(?m)^[ \t]+pub const (?<name>\w+): Type\s*=[ \t]*")]
    private static partial Regex EnumConstant();

    [GeneratedRegex(@"^(?:::)?\w+(?:::\w+)*$")]
    private static partial Regex DirectTypePattern();

    /// <summary>
    /// Matches bindgen's escaped names, including legacy keywords and primitive type names.
    /// </summary>
    [GeneratedRegex(@"^(?:as|break|const|continue|crate|else|enum|extern|false|fn|for|if|impl|in|let|loop|match|mod|move|mut|pub|ref|return|self|Self|static|struct|super|trait|true|type|unsafe|use|where|while|async|await|dyn|abstract|become|box|do|final|macro|override|priv|typeof|unsized|virtual|yield|try|gen|str|alignof|offsetof|proc|pure|sizeof|bool|f32|f64|usize|isize|u128|i128|u64|i64|u32|i32|u16|i16|u8|i8|_)$")]
    private static partial Regex BindgenReservedName();
}
