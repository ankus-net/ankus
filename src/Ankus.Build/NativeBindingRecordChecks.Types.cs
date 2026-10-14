namespace Ankus.Build;

internal static partial class NativeBindingRecordChecks
{
    private sealed partial class Writer
    {
        /// <summary>
        /// Reconstructs declared C shapes while anchoring every tag to its actual native identity.
        /// </summary>
        private string Declare(int index, string name, NativeHeaderQualifiers inherited = NativeHeaderQualifiers.None, int depth = 0)
        {
            if (depth >= 128)
            {
                throw new FormatException("Native record declaration nesting exceeds the supported limit.");
            }

            NativeRecordType type = _graph.Types[index];
            NativeHeaderQualifiers modifiers = inherited | type.Qualifiers;
            string qualifiers = NativeHeaderType.Qualifiers(modifiers);
            switch (type.Kind)
            {
                case "scalar":
                    return qualifiers + (type.Name == "bool" ? "_Bool" : type.Name) + " " + name;
                case "record":
                case "enum":
                    return qualifiers + _anchors[type.Declaration!.Value] + " " + name;
                case "alias":
                    return qualifiers + AliasName(type) + " " + name;
                case "elaborated":
                case "attributed":
                case "typeof":
                    return Declare(type.Element!.Value, name, modifiers, depth + 1);
                case "pointer":
                    string pointer = "*" + qualifiers + name;
                    int element = type.Element!.Value;
                    int nesting = depth;
                    while (_graph.Types[element].Kind is "elaborated" or "attributed" or "typeof")
                    {
                        if (++nesting >= 128)
                        {
                            throw new FormatException("Native record declaration nesting exceeds the supported limit.");
                        }

                        element = _graph.Types[element].Element!.Value;
                    }

                    if (_graph.Types[element].Kind is "array" or "function")
                    {
                        pointer = "(" + pointer + ")";
                    }

                    return Declare(type.Element!.Value, pointer, depth: depth + 1);
                case "array":
                    return Declare(type.Element!.Value, name + "[" + (type.Count is long count ? Number(count) : "") + "]", modifiers, depth + 1);
                case "function":
                    NativeRecordFunction function = type.Function!;
                    string parameters = string.Join(", ", function.Parameters.Select((parameter, position) =>
                        Declare(parameter, "ankus_parameter_" + Number(position), depth: depth + 1)));
                    if (function.HasPrototype && function.IsVariadic)
                    {
                        parameters += ", ...";
                    }
                    else if (function.HasPrototype && function.Parameters.Count == 0)
                    {
                        parameters = "void";
                    }

                    if (_graph.Types[function.Result].Qualifiers != NativeHeaderQualifiers.None)
                    {
                        // C ignores qualifiers on the returned value, but a typedef can retain its exact object type without redundant-return warnings.
                        string resultAlias = "ankus_record_function_result_" + Number(function.Result);
                        if (_functionResults.Add(function.Result))
                        {
                            string resultDeclaration = Declare(function.Result, resultAlias, depth: depth + 1);
                            _source.Append("typedef ").Append(resultDeclaration).AppendLine(";");
                        }

                        return resultAlias + " " + name + "(" + parameters + ")" + Convention(function);
                    }

                    return Declare(function.Result, name + "(" + parameters + ")", modifiers, depth + 1) + Convention(function);
                case "complex":
                    return qualifiers + "_Complex " + Declare(type.Element!.Value, name, depth: depth + 1);
                case "atomic":
                    return qualifiers + "_Atomic(" + Declare(type.Element!.Value, "", depth: depth + 1).Trim() + ") " + name;
                case "vector":
                    string attribute = type.Spelling.Contains("ext_vector_type", StringComparison.Ordinal) ? "ext_vector_type" : "vector_size";
                    long extent = attribute == "ext_vector_type" ? type.Count!.Value : type.Size!.Value;
                    return Declare(type.Element!.Value, name + " __attribute__((" + attribute + "(" + Number(extent) + ")))", modifiers, depth + 1);
                default:
                    throw new FormatException("Unsupported native record declaration shape.");
            }
        }

        /// <summary>
        /// Removes only parameter-level qualification when comparing C function types, preserving pointees and arrays.
        /// </summary>
        private string NormalizeFunction(int index, string name)
        {
            int depth = 0;
            NativeRecordType type = _graph.Types[index];
            while (type.Function is null && type.Element is int element)
            {
                if (++depth >= 128)
                {
                    throw new FormatException("Native function alias nesting exceeds the supported limit.");
                }

                type = _graph.Types[element];
            }

            NativeRecordFunction function = type.Function ?? throw new FormatException("Native function has no prototype representation.");
            string parameters = string.Join(", ", function.Parameters.Select(NormalizeParameter));
            if (function.HasPrototype && function.IsVariadic)
            {
                parameters += ", ...";
            }
            else if (function.HasPrototype && function.Parameters.Count == 0)
            {
                parameters = "void";
            }

            _source.Append("typedef ").Append(TypeName(function.Result)).Append(' ').Append(name).Append('(').Append(parameters).Append(')')
                .Append(Convention(function)).AppendLine(";");
            return name;
        }

        /// <summary>
        /// Removes top-level parameter qualifiers without converting incomplete values or promoting enums.
        /// </summary>
        private string NormalizeParameter(int index)
        {
            NativeRecordType type = Canonical(index);
            if (type.Declaration is int declaration && !_graph.Declarations[declaration].IsComplete)
            {
                return _anchors[declaration];
            }

            return type.Kind is "array" or "function" ? TypeName(index) : "ANKUS_RECORD_UNQUAL(*(" + TypeName(index) + " *)0)";
        }

        /// <summary>
        /// Preserves the compiler's native function calling convention independently of parameter normalization.
        /// </summary>
        private static string Convention(NativeRecordFunction function) => function.CallingConvention switch
        {
            1 => "",
            2 => " __attribute__((stdcall))",
            3 => " __attribute__((fastcall))",
            4 => " __attribute__((thiscall))",
            10 => " __attribute__((ms_abi))",
            11 => " __attribute__((sysv_abi))",
            12 => " __attribute__((vectorcall))",
            _ => throw new FormatException("Unsupported native record calling convention."),
        };
    }
}
