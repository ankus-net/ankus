using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ankus.Build;

/// <summary>
/// Emits managed object representations from the compiler's transitive native type graph.
/// </summary>
internal static partial class NativeBindingRecordCSharp
{
    /// <summary>
    /// Retains measured native storage without claiming that CLR stack locations have native alignment.
    /// </summary>
    internal static NativeBindingSource Generate(NativeRecordGraph graph)
        => GenerateCore(graph, null, null);

    /// <summary>
    /// Emits selected guarded methods and shared record values with the complete native declaration identity.
    /// </summary>
    /// <param name="records">The complete selected-header signatures and measured record graph.</param>
    /// <param name="names">The unique fixed functions to expose in the companion.</param>
    /// <param name="globalNames">Actual global declarations to expose, excluding synthetic native type roots.</param>
    /// <returns>One companion whose identity includes native/linkage mappings and the complete observed graph.</returns>
    internal static NativeBindingSource Generate(NativeHeaderRecords records, IReadOnlyList<string> names, IReadOnlyList<string>? globalNames = null)
    {
        IReadOnlyList<NativeBindingCall> calls = NativeBindingCallModel.Select(records, names);
        IReadOnlyList<NativeBindingGlobalContract>? globals = globalNames is null ? null : NativeBindingGlobalModel.Select(records, globalNames);
        return GenerateCore(records.Graph, null, null, records.Headers, calls, globals);
    }

    /// <summary>
    /// Emits shared node values and selected fixed calls from one complete validated native contract.
    /// </summary>
    /// <param name="records">The complete selected-header signatures and measured record graph.</param>
    /// <param name="catalog">The pinned node identities and cast contracts.</param>
    /// <param name="layout">The independently executed native node layout observations.</param>
    /// <param name="names">Unique fixed functions to expose alongside the node APIs.</param>
    /// <param name="globalNames">Actual global declarations to expose, excluding synthetic native type roots.</param>
    /// <returns>One companion with one managed declaration per measured native declaration.</returns>
    internal static NativeBindingSource Generate(NativeHeaderRecords records, NativeBindingCatalog catalog,
        NativeBindingLayout layout, IReadOnlyList<string> names, IReadOnlyList<string>? globalNames = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(layout);
        IReadOnlyList<NativeBindingCall> calls = NativeBindingCallModel.Select(records, names);
        IReadOnlyList<NativeBindingGlobalContract>? globals = globalNames is null ? null : NativeBindingGlobalModel.Select(records, globalNames);
        return GenerateCore(records.Graph, catalog, layout, records.Headers, calls, globals);
    }

    /// <summary>
    /// Retains the existing node APIs while emitting every declaration from the complete measured graph.
    /// </summary>
    internal static NativeBindingSource Generate(NativeRecordGraph graph, NativeBindingCatalog catalog, NativeBindingLayout layout)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(layout);
        return GenerateCore(graph, catalog, layout);
    }

    private static NativeBindingSource GenerateCore(NativeRecordGraph graph, NativeBindingCatalog? catalog, NativeBindingLayout? layout,
        NativeHeaderCatalog? headers = null, IReadOnlyList<NativeBindingCall>? calls = null, IReadOnlyList<NativeBindingGlobalContract>? globals = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (graph.Target is null || graph.Roots is null || graph.Target.PostgresVersion / 10000 is < 13 or > 19 || graph.Target.ClangMajor <= 0)
        {
            throw new FormatException("Native declarations require a complete supported header and compiler identity.");
        }

        NativeBindingRecordValidation.Validate(graph, graph.Target, graph.Roots.Keys);
        ValidateValueGraph(graph);
        NativeBindingNodeContract? nodes = catalog is null ? null : NativeBindingNodeRecords.Create(graph, catalog, layout!);
        return new Writer(graph, nodes, headers, calls, globals).Generate();
    }

    private static void ValidateValueGraph(NativeRecordGraph graph)
    {
        byte[] states = new byte[graph.Types.Count];
        var pending = new Stack<(int Index, bool Leaving)>();
        for (int index = 0; index < graph.Types.Count; index++)
        {
            pending.Push((index, false));
            while (pending.TryPop(out (int Index, bool Leaving) frame))
            {
                int canonical = graph.Types[frame.Index].Canonical;
                if (frame.Leaving)
                {
                    states[canonical] = 2;
                    continue;
                }

                if (states[canonical] == 2)
                {
                    continue;
                }

                if (states[canonical] == 1)
                {
                    throw new FormatException("Native value storage contains a cycle without a pointer boundary.");
                }

                states[canonical] = 1;
                pending.Push((canonical, true));
                NativeRecordType type = graph.Types[canonical];
                if (type.Kind == "record")
                {
                    foreach (NativeRecordField field in graph.Declarations[type.Declaration!.Value].Fields)
                    {
                        pending.Push((field.Type, false));
                    }
                }
                else if (type.Kind is "array" or "vector" or "complex" or "atomic")
                {
                    pending.Push((type.Element!.Value, false));
                }
                else if (type.Kind == "enum" && graph.Declarations[type.Declaration!.Value].EnumUnderlying is int underlying)
                {
                    NativeRecordType integer = graph.Types[graph.Types[underlying].Canonical];
                    if (integer.Kind != "scalar" || integer.Size != type.Size)
                    {
                        throw new FormatException("A native enum requires an equally sized scalar integer representation.");
                    }

                    pending.Push((underlying, false));
                }
                else if (type.Kind is "alias" or "elaborated" or "attributed" or "typeof")
                {
                    throw new FormatException("A canonical native value retains an unresolved type wrapper.");
                }
            }
        }
    }

    private sealed partial class Writer(NativeRecordGraph graph, NativeBindingNodeContract? nodes, NativeHeaderCatalog? headers,
        IReadOnlyList<NativeBindingCall>? calls, IReadOnlyList<NativeBindingGlobalContract>? globals)
    {
        private readonly StringBuilder _source = new("// <auto-generated/>\n#nullable enable\nnamespace Ankus.Postgres;\n\n");
        private readonly HashSet<string> _names = new(StringComparer.Ordinal) { "NativeBinding" };
        private readonly Dictionary<int, string> _declarations = [];
        private readonly Dictionary<int, Value> _types = [];
        private readonly Dictionary<(string Code, int Count), string> _arrays = [];
        private readonly Dictionary<int, string> _storage = [];
        private readonly Dictionary<int, string> _opaqueValues = [];
        private readonly IReadOnlyList<NativeBindingIndirectCall> _indirectCalls = NativeBindingIndirectModel.Describe(graph);
        private readonly Dictionary<int, string> _functionPointers = [];
        private readonly NativeBindingRecordMembers _members = new(graph);

        /// <summary>
        /// Emits every declaration and hashes both the managed source and complete native contract.
        /// </summary>
        internal NativeBindingSource Generate()
        {
            if (calls is not null)
            {
                _names.Add("NativeMethods");
            }

            if (globals is { Count: > 0 })
            {
                _names.Add("NativeGlobals");
            }

            if (nodes is not null)
            {
                foreach ((int index, NativeBindingType type) in nodes.Values.OrderBy(static pair => pair.Value.Name, StringComparer.Ordinal))
                {
                    if (!_names.Add(type.Name))
                    {
                        throw new FormatException("A native node name conflicts with its companion metadata.");
                    }

                    _declarations.Add(index, type.Name);
                }

                if (!_names.Add("NodeTag"))
                {
                    throw new FormatException("A native node value conflicts with its discriminator name.");
                }

                _declarations.Add(nodes.NodeTag, "NodeTag");
                foreach ((int index, string name) in nodes.Enums.OrderBy(static pair => pair.Value, StringComparer.Ordinal))
                {
                    if (!_names.Add(name))
                    {
                        throw new FormatException("A native enum name conflicts with another companion declaration.");
                    }

                    _declarations.Add(index, name);
                }
            }

            var aliases = new Dictionary<int, string>();
            foreach (NativeRecordType type in graph.Types.Where(static type => type.Kind == "alias"))
            {
                if (!(Canonical(type.Canonical).Declaration is int declaration &&
                    (!aliases.TryGetValue(declaration, out string? previous) || string.CompareOrdinal(type.Name, previous) < 0)))
                {
                    continue;
                }

                aliases[declaration] = type.Name;
            }

            for (int index = 0; index < graph.Declarations.Count; index++)
            {
                if (_declarations.ContainsKey(index) || _members.IsInternalDeclaration(index))
                {
                    continue;
                }

                NativeRecordDeclaration declaration = graph.Declarations[index];
                string name = declaration.Name.Length != 0 ? declaration.Name : aliases.GetValueOrDefault(index) ?? "NativeAnonymous" + Number(index);
                _declarations.Add(index, Unique(_names, name));
            }

            foreach (NativeBindingIndirectCall call in _indirectCalls)
            {
                string name = call.Name ?? "NativeFunction" + Number(call.FunctionType);
                if (name is "Invoke" or "IsNull" or "DangerousGetAddress" or "GetNativeBody" or "_address" or "nint" or "nuint")
                {
                    name = "Native_" + name;
                }

                _functionPointers.Add(call.FunctionType, Unique(_names, name));
            }

            NameFieldCallbacks();
            if (globals is { Count: > 0 })
            {
                NameGlobalCallbacks(globals);
            }

            for (int index = 0; index < graph.Types.Count; index++)
            {
                if (graph.Types[index].Size > 0 && !_members.IsInternalType(index))
                {
                    _ = Map(index);
                }
            }

            Header();
            for (int index = 0; index < graph.Declarations.Count; index++)
            {
                if (!_members.IsInternalDeclaration(index))
                {
                    Declaration(index);
                }
            }

            if (calls is not null)
            {
                Methods(calls);
            }

            if (globals is { Count: > 0 })
            {
                Globals(globals);
            }

            FunctionPointers();
            NamedCallbacks();
            PointerValues();

            foreach (string name in _emptyValues.Values)
            {
                Summary("Represents the unique logical value of a native zero-size object without transporting CLR storage bytes.");
                Line($"public readonly struct @{name}\n{{\n}}\n");
            }

            foreach ((int index, string name) in _opaqueValues)
            {
                NativeRecordType type = graph.Types[index];
                int size = Size(type.Size);
                Summary("Retains an exact native representation without a lossy CLR numeric conversion.");
                Layout(size);
                Line($"public struct @{name} : global::Ankus.IPgNativeType");
                Line("{");
                Identity(size, type.Alignment!.Value);
                Summary("Accesses all native representation bytes, including any padding.", "    ");
                Line("    [global::System.Runtime.InteropServices.FieldOffset(0)]");
                Line($"    public {Storage(size)} Bytes;");
                Line("}\n");
            }

            foreach (((string code, int count), string name) in _arrays)
            {
                Summary("Stores contiguous native array elements with their measured stride.");
                Line($"[global::System.Runtime.CompilerServices.InlineArray({Number(count)})]");
                Line($"public struct @{name}\n{{\n    private {code} _element0;\n}}\n");
            }

            foreach ((int count, string name) in _storage)
            {
                Summary("Retains exact native object bytes.");
                Line($"[global::System.Runtime.CompilerServices.InlineArray({Number(count)})]");
                Line($"public struct @{name}\n{{\n    private byte _element0;\n}}\n");
            }

            string body = _source.ToString().ReplaceLineEndings("\n");
            NativeRecordGraph ordered = graph with
            {
                Roots = graph.Roots.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
            };
            string contract = JsonSerializer.Serialize(ordered, NativeBindingRecordWorker.JsonOptions);
            if (headers is not null)
            {
                NativeHeaderCatalog orderedHeaders = headers with
                {
                    Symbols = headers.Symbols.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
                };
                contract += "\n" + JsonSerializer.Serialize(orderedHeaders, NativeBindingRecordWorker.JsonOptions);
            }

            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body + "\n" + contract)));
            return new("Ankus.Postgres.Pg" + Number(graph.Target.PostgresVersion / 10000) + "." + identity, identity,
                body.Replace("__ANKUS_RECORD_IDENTITY__", identity, StringComparison.Ordinal));
        }

        private void Header()
        {
            Summary("Identifies the complete measured native declaration contract.");
            Line("public static class NativeBinding\n{");
            Summary("Identifies the selected native and managed ABI.", "    ");
            Line("    public const string Identity = \"__ANKUS_RECORD_IDENTITY__\";");
            Summary("Identifies the measured operating system and processor.", "    ");
            Line($"    public const string RuntimeIdentifier = \"{graph.Target.RuntimeIdentifier}\";");
            if (nodes is not null)
            {
                Summary("Retains measured concrete node sizes and alignments for the backend boundary.", "    ");
                Line($"    public const string NodeLayouts = \"{nodes.Layouts}\";");
            }

            Summary("Rejects a managed host with an incompatible native representation.", "    ");
            Line("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
            Line("    internal static void ValidateHost()\n    {");
            Line($"        if (global::System.IntPtr.Size != {Number(graph.Target.PointerSize)} || global::System.BitConverter.IsLittleEndian != {(graph.Target.IsLittleEndian ? "true" : "false")} || global::System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier != RuntimeIdentifier)");
            Line("        {\n            throw new global::System.PlatformNotSupportedException(\"The native declarations require their measured target ABI.\");\n        }\n    }\n}\n");
        }

        private void Declaration(int index)
        {
            NativeRecordDeclaration declaration = graph.Declarations[index];
            string name = _declarations[index];
            if (!declaration.IsComplete || declaration.Size == 0)
            {
                Summary("Identifies native storage that has no allocatable CLR value representation.");
                Line($"public readonly struct @{name}\n{{");
                Summary("Reports whether the selected headers define this native object.", "    ");
                Line($"    public const bool IsComplete = {(declaration.IsComplete ? "true" : "false")};");
                Summary("Reports the native byte size, or minus one when its layout is opaque.", "    ");
                Line($"    public const long NativeSize = {Number(declaration.Size ?? -1)}L;\n}}\n");
                return;
            }

            if (declaration.Kind == "enum")
            {
                Enum(declaration, name, nodes?.NodeTag == index);
                return;
            }

            int size = Size(declaration.Size);
            Summary("Retains the complete selected-header native record. Addresses have caller-owned lifetimes.");
            NativeBindingType? node = nodes?.Values.GetValueOrDefault(index);
            int pack = node is null ? 1 : Size(declaration.Alignment);
            if (pack > 128)
            {
                throw new FormatException("The existing node layout requires unsupported managed packing.");
            }

            NativeRecordField[] fields = [.. _members.Get(index).Select(static member => member.Field)];
            bool pointers = fields.Any(field => Canonical(field.Type).Kind == "pointer" && Map(field.Type).Pointer);
            Layout(size, pack);
            Line($"public {(pointers ? "unsafe " : "")}struct @{name} : global::Ankus.{(node?.IsNode == true ? "IPgNativeNode" : "IPgNativeType")}\n{{");
            Identity(size, declaration.Alignment!.Value);
            if (node?.IsNode == true)
            {
                string predicate = name == "Node" ? "true" : node.CastTags.Count == 0 ? "false" :
                    "tag is " + string.Join(" or ", node.CastTags.Select(tag => Number(nodes!.Tags[tag]) + "U"));
                Line($"    static bool global::Ankus.IPgNativeNode.AcceptsTag(uint tag) => {predicate};");
            }

            var members = new HashSet<string>(fields.Select(static field => field.Name).Where(static value => value.Length != 0), StringComparer.Ordinal) { name };
            string bits = Unique(members, "_nativeBits");
            if (fields.Any(static field => field.BitWidth > 0 && field.Name.Length != 0))
            {
                Line("    [global::System.Runtime.InteropServices.FieldOffset(0)]");
                Line($"    private {Storage(size)} @{bits};");
            }

            foreach (NativeRecordField field in fields)
            {
                if (field.BitWidth is not null && field.Name.Length == 0)
                {
                    continue;
                }

                string member = field.Name == name ? Unique(members, "Native_" + field.Name) : field.Name;
                NativeRecordType type = Canonical(field.Type);
                if (type.Kind == "array" && type.Count is null or 0)
                {
                    Flexible(field, member, members, name);
                    continue;
                }

                if (field.BitWidth is int width)
                {
                    Bitfield(field, member, bits, width);
                    continue;
                }

                Value value = Map(field.Type);
                Summary("Accesses the measured native field.", "    ");
                Line($"    [global::System.Runtime.InteropServices.FieldOffset({Number(field.OffsetBits / 8)})]");
                if (value.Code == "bool")
                {
                    Line("    [global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)]");
                }

                Line($"    public {Hide(member)}{value.Code} @{member};");
            }

            Line("}\n");
        }

        private void Flexible(NativeRecordField field, string member, HashSet<string> members, string owner)
        {
            NativeRecordType type = Canonical(field.Type);
            Value element = ContainerValue(type.Element!.Value);
            string name = Unique(members, "Dangerous_" + member);
            Summary("Borrows trailing native elements from a live address and caller-guaranteed extent.", "    ");
            Line("    /// <param name=\"address\">The live native record with sufficient trailing storage.</param>");
            Line("    /// <param name=\"length\">The number of initialized elements to borrow.</param>");
            Line("    /// <returns>A view bounded by the caller's native storage lifetime.</returns>");
            string address = "@" + owner + "*";
            Line($"    public static unsafe global::System.Span<{element.Code}> @{name}({address} address, int length)\n    {{");
            Line("        global::System.ArgumentOutOfRangeException.ThrowIfNegative(length);");
            Line("        if (address is null)\n        {\n            throw new global::System.ArgumentNullException(nameof(address));\n        }\n");
            Line($"        nuint start = checked(unchecked((nuint)address) + {Number(field.OffsetBits / 8)}U);");
            Line($"        _ = checked(start + (nuint)length * {Number(element.Size)}U);");
            Line($"        return new((void*)start, length);\n    }}");
        }

        private void Bitfield(NativeRecordField field, string member, string storage, int width)
        {
            Value value = Map(field.Type);
            NativeRecordType type = Canonical(field.Type);
            Value integer = type.Kind == "enum" ? Map(graph.Declarations[type.Declaration!.Value].EnumUnderlying!.Value) : value;
            if (integer.Signed is null || width is < 1 or > 128 || width > integer.Size * 8)
            {
                throw new FormatException("A native bitfield requires an observed integer representation of at most 128 bits.");
            }

            string cast = type.Kind == "enum" ? "(" + integer.Code + ")" : "";
            string raw = integer.Code == "bool" ? "(value ? (global::System.UInt128)1 : 0)" : "unchecked((global::System.UInt128)" + cast + "value)";
            string signed = $"(unchecked((global::System.Int128)(bits << {Number(128 - width)})) >> {Number(128 - width)})";
            string get = integer.Code == "bool" ? "bits != 0" : "(" + value.Code + ")" + cast + (integer.Signed.Value ? signed : "bits");
            Summary("Reads and writes the exact native bitfield, rejecting values outside its range.", "    ");
            Line($"    public {Hide(member)}{value.Code} @{member}\n    {{\n        readonly get\n        {{\n            global::System.UInt128 bits = 0;");
            Line($"            for (int index = 0; index < {Number(width)}; index++)\n            {{\n                int bit = {Number(field.OffsetBits)} + index;");
            string bit = graph.Target.IsLittleEndian ? "bit % 8" : "7 - bit % 8";
            string shift = graph.Target.IsLittleEndian ? "index" : Number(width - 1) + " - index";
            Line($"                bits |= (global::System.UInt128)((@{storage}[bit / 8] >> ({bit})) & 1) << ({shift});\n            }}\n\n            return {get};\n        }}");
            Line("        set\n        {");
            if (integer.Code != "bool" && width < integer.Size * 8)
            {
                string condition = integer.Signed.Value
                    ? $"(global::System.Int128){cast}value < -((global::System.Int128)1 << {Number(width - 1)}) || (global::System.Int128){cast}value > ((global::System.Int128)1 << {Number(width - 1)}) - 1"
                    : $"(global::System.UInt128){cast}value > ((global::System.UInt128)1 << {Number(width)}) - 1";
                Line($"            if ({condition}) {{ throw new global::System.ArgumentOutOfRangeException(nameof(value)); }}\n");
            }

            Line($"            global::System.UInt128 bits = {raw};");
            Line($"            for (int index = 0; index < {Number(width)}; index++)\n            {{\n                int bit = {Number(field.OffsetBits)} + index;");
            Line($"                int mask = 1 << ({bit});");
            Line($"                @{storage}[bit / 8] = (byte)((@{storage}[bit / 8] & ~mask) | ((bits >> ({shift}) & 1) != 0 ? mask : 0));\n            }}\n        }}\n    }}");
        }

        private void Enum(NativeRecordDeclaration declaration, string name, bool nodeTag)
        {
            Value underlying = nodeTag ? new("uint", sizeof(uint), false) : Map(declaration.EnumUnderlying!.Value);
            if (underlying.Signed is null || underlying.Size > 8 || underlying.Code == "bool")
            {
                throw new FormatException("Invalid native enum representation.");
            }

            Summary("Retains the selected-header native enum values and integer representation.");
            Line($"public enum @{name} : {underlying.Code}\n{{");
            var members = new HashSet<string>(declaration.EnumValues.Select(static constant => constant.Name), StringComparer.Ordinal) { name, "value__" };
            BigInteger limit = BigInteger.One << (underlying.Size * 8 - (underlying.Signed.Value ? 1 : 0));
            foreach (NativeRecordConstant constant in declaration.EnumValues)
            {
                BigInteger value = BigInteger.Parse(constant.Value, CultureInfo.InvariantCulture);
                if (value < (underlying.Signed.Value ? -limit : BigInteger.Zero) || value >= limit)
                {
                    throw new FormatException("Native enum value exceeds its integer representation.");
                }

                Summary("Retains the exact native enum constant.", "    ");
                string suffix = underlying.Code switch
                {
                    "uint" => "U",
                    "long" => "L",
                    "ulong" => "UL",
                    _ => ""
                };
                string member = constant.Name == name || constant.Name == "value__" ? Unique(members, "Native_" + constant.Name) : constant.Name;
                Line($"    @{member} = {constant.Value}{suffix},");
            }

            Line("}\n");
        }

        private Value Map(int index)
        {
            NativeRecordType type = Canonical(index);
            index = graph.Types[index].Canonical;
            if (_types.TryGetValue(index, out Value? known))
            {
                return known;
            }

            int size = Size(type.Size);
            Value value;
            if (type.Declaration is int declaration)
            {
                NativeRecordDeclaration native = graph.Declarations[declaration];
                bool? sign = native.EnumUnderlying is int underlying ? Map(underlying).Signed : null;
                value = new("@" + _declarations[declaration], size, sign);
            }
            else if (type.Kind == "pointer")
            {
                int element = type.Element!.Value;
                int? signature = NativeBindingIndirectModel.FunctionType(graph, element);
                string code = signature is int function
                    ? "@" + _functionPointers[function]
                    : PointerTarget(element) + "*";
                value = new(code, size, null, Pointer: signature is null);
            }
            else if (type.Kind == "array")
            {
                Value element = ContainerValue(type.Element!.Value);
                int count = Size(type.Count);
                if ((long)count * element.Size != size)
                {
                    throw new FormatException("The native array requires its exact managed element stride.");
                }

                if (!_arrays.TryGetValue((element.Code, count), out string? name))
                {
                    name = Unique(_names, "NativeArray" + Number(_arrays.Count));
                    _arrays.Add((element.Code, count), name);
                }

                value = new("@" + name, size, null);
            }
            else if (type.Kind == "scalar" && Scalar(type, size) is Value scalar)
            {
                value = scalar;
            }
            else
            {
                string name = Unique(_names, "NativeValue" + Number(index));
                _opaqueValues.Add(index, name);
                value = new("@" + name, size, null);
            }

            _types.Add(index, value);
            return value;
        }

        private Value? Scalar(NativeRecordType type, int size)
        {
            bool? sign = type.Name switch
            {
                "_Bool" => false,
                "char" => graph.Target.Numeric.CharIsSigned,
                "wchar_t" => graph.Target.Numeric.WCharIsSigned,
                "signed char" or "short" or "int" or "long" or "long long" or "__int128" => true,
                "unsigned char" or "unsigned short" or "unsigned int" or "unsigned long" or "unsigned long long" or "unsigned __int128" => false,
                _ => null,
            };
            if (type.Name == "_Bool")
            {
                if (size != 1)
                {
                    throw new FormatException("Native Boolean storage requires an exact one-byte CLR Boolean representation.");
                }

                return new("bool", 1, false);
            }

            if (sign is bool signed)
            {
                string code = (size, signed) switch
                {
                    (1, true) => "sbyte",
                    (1, false) => "byte",
                    (2, true) => "short",
                    (2, false) => "ushort",
                    (4, true) => "int",
                    (4, false) => "uint",
                    (8, true) => "long",
                    (8, false) => "ulong",
                    (16, true) => "global::System.Int128",
                    (16, false) => "global::System.UInt128",
                    _ => throw new FormatException("The native integer representation has no exact CLR integer type."),
                };
                return new(code, size, signed);
            }

            NativeFloatingModel? floating = type.Name switch
            {
                "float" => graph.Target.Numeric.Float,
                "double" => graph.Target.Numeric.Double,
                "long double" => graph.Target.Numeric.LongDouble,
                _ => null,
            };
            if (graph.Target.Numeric.Radix == 2 && floating is not null)
            {
                if (size == 4 && floating == new NativeFloatingModel(24, -125, 128))
                {
                    return new("float", 4, null);
                }

                if (size == 8 && floating == new NativeFloatingModel(53, -1021, 1024))
                {
                    return new("double", 8, null);
                }
            }

            return null;
        }

        private NativeRecordType Canonical(int index) => graph.Types[graph.Types[index].Canonical];

        private string Storage(int size)
        {
            if (!_storage.TryGetValue(size, out string? name))
            {
                name = Unique(_names, "NativeBytes" + Number(size));
                _storage.Add(size, name);
            }

            return "@" + name;
        }

        private void Identity(int size, long alignment)
        {
            Line($"    static int global::Ankus.IPgNativeType.PostgresMajor => {Number(graph.Target.PostgresVersion / 10000)};");
            Line("    static string global::Ankus.IPgNativeType.AbiIdentity => NativeBinding.Identity;");
            Line("    static string global::Ankus.IPgNativeType.RuntimeIdentifier => NativeBinding.RuntimeIdentifier;");
            Line($"    static int global::Ankus.IPgNativeType.NativeSize => {Number(size)};");
            Line($"    static int global::Ankus.IPgNativeType.NativeAlignment => {Number(Size(alignment))};");
        }

        private void Layout(int size, int pack = 1) => Line($"[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Explicit, Size = {Number(size)}, Pack = {Number(pack)})]");

        private void Summary(string value, string indent = "") => Line(indent + "/// <summary>\n" + indent + "/// " + value + "\n" + indent + "/// </summary>");

        private void Line(string value) => _source.AppendLine(value);

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static int Size(long? value) => value is > 0 and <= int.MaxValue ? (int)value.Value
            : throw new FormatException("Native storage requires a positive size representable by a CLR value. Opaque, zero-sized and flexible values require an explicit native address.");

        private static string Hide(string name) => name is "Equals" or "GetHashCode" or "GetType" or "ToString" or "MemberwiseClone" or "Finalize" ? "new " : "";

        private static string Unique(HashSet<string> names, string candidate)
        {
            NativeBindingCDeclaration.ValidateName(candidate);
            string result = candidate;
            int suffix = 0;
            while (!names.Add(result))
            {
                result = candidate + "_" + Number(++suffix);
            }

            return result;
        }

        private sealed record Value(string Code, int Size, bool? Signed, bool Pointer = false)
        {
            /// <summary>
            /// Gets the generic-compatible representation used only for frame byte transport.
            /// </summary>
            internal string Storage => Pointer ? "nint" : Code;
        }
    }
}
