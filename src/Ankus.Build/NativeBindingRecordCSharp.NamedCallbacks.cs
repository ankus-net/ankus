namespace Ankus.Build;

internal static partial class NativeBindingRecordCSharp
{
    private sealed partial class Writer
    {
        private readonly List<(string Name, NativeBindingIndirectCall Call)> _namedCallbacks = [];

        /// <summary>
        /// Names callback values by their owning declaration and field without replacing shared canonical pointer types.
        /// </summary>
        private void NameFieldCallbacks()
        {
            Dictionary<int, NativeBindingIndirectCall> callsBySignature = _indirectCalls.ToDictionary(static call => call.FunctionType);
            foreach ((int index, string owner) in _declarations.OrderBy(static pair => pair.Value, StringComparer.Ordinal))
            {
                foreach (NativeRecordField field in _members.Get(index).Select(static member => member.Field).Where(static field => field.Name.Length != 0)
                    .OrderBy(static field => field.Name, StringComparer.Ordinal))
                {
                    NameCallback(field.Type, owner + "_" + field.Name + "Callback", callsBySignature);
                }
            }
        }

        /// <summary>
        /// Names selected global callbacks independently of unrelated typedef aliases for the same signature.
        /// </summary>
        private void NameGlobalCallbacks(IReadOnlyList<NativeBindingGlobalContract> selected)
        {
            Dictionary<int, NativeBindingIndirectCall> callsBySignature = _indirectCalls.ToDictionary(static call => call.FunctionType);
            foreach (NativeBindingGlobalContract global in selected.OrderBy(static global => global.Name, StringComparer.Ordinal))
            {
                NameCallback(global.StorageType, "NativeGlobals_" + global.Name + "Callback", callsBySignature);
            }
        }

        /// <summary>
        /// Assigns a declaration-based name to a function pointer or its containing array's elements.
        /// </summary>
        private void NameCallback(int typeIndex, string name, Dictionary<int, NativeBindingIndirectCall> callsBySignature)
        {
            NativeRecordType type = Canonical(typeIndex);
            while (type.Kind == "array")
            {
                type = Canonical(type.Element!.Value);
            }

            if (type.Kind == "pointer" && NativeBindingIndirectModel.FunctionType(graph, type.Element!.Value) is int signature)
            {
                _namedCallbacks.Add((Unique(_names, name), callsBySignature[signature]));
            }
        }

        /// <summary>
        /// Exposes discoverable callback names while sharing native storage, signature identity and guarded invocation.
        /// </summary>
        private void NamedCallbacks()
        {
            foreach ((string name, NativeBindingIndirectCall call) in _namedCallbacks)
            {
                string canonical = _functionPointers[call.FunctionType];
                Document(name);
                Summary("Borrows a native callback address using its field or global declaration name.");
                Line("/// <param name=\"address\">A native callback with the exact selected-header signature and a lifetime covering every use.</param>");
                Line("/// <remarks>This value shares its canonical pointer's representation and does not own or extend the callback lifetime.</remarks>");
                Line($"[global::Ankus.CompilerServices.NativeFunctionPointer({Number(call.FunctionType)})]");
                Line("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential)]");
                Line($"public readonly unsafe struct @{name}(void* address) : global::Ankus.IPgNativeType\n{{");
                NativeRecordType storage = graph.Types[call.PointerType];
                Identity(Size(storage.Size), storage.Alignment!.Value);
                Summary("Retains the shared canonical callback value.", "    ");
                Line($"    private readonly @{canonical} _value = new(address);\n");
                Summary("Reports whether this value contains no native target.", "    ");
                Line("    public bool IsNull => _value.IsNull;\n");
                Summary("Returns the borrowed address without establishing native ownership or callback lifetime.", "    ");
                Line("    public void* DangerousGetAddress() => _value.DangerousGetAddress();\n");
                Summary("Preserves the address when assigning this callback to its native storage.", "    ");
                Line("    /// <param name=\"value\">The borrowed named callback.</param>");
                Line("    /// <returns>The same address with its canonical native signature.</returns>");
                Line($"    public static implicit operator @{canonical}(@{name} value) => value._value;\n");
                Summary("Preserves the address when reading native callback storage.", "    ");
                Line("    /// <param name=\"value\">The borrowed canonical callback.</param>");
                Line("    /// <returns>The same address using the declaration's named callback type.</returns>");
                Line($"    public static implicit operator @{name}(@{canonical} value) => new(value.DangerousGetAddress());\n");
                if (headers is not null && call.CanInvoke)
                {
                    Summary("Invokes the callback beneath the active PostgreSQL error guard.", "    ");
                    string[] parameters = [.. call.Parameters.Select((type, index) => CallValue(type).Code + " argument" + Number(index))];
                    string[] arguments = [.. call.Parameters.Select((_, index) => "argument" + Number(index))];
                    foreach (string argument in arguments)
                    {
                        Line($"    /// <param name=\"{argument}\">The exact native value; referenced storage must remain valid for the complete call.</param>");
                    }

                    string result = call.Result is int returned ? CallValue(returned).Code : "void";
                    if (call.Result is not null)
                    {
                        Line("    /// <returns>The exact native result with its original ownership and lifetime.</returns>");
                    }

                    Line("    /// <remarks>Requires the matching native binding and an active backend callback.</remarks>");
                    Line("    [global::Ankus.CompilerServices.NativeUnsafeAccess]");
                    Line($"    public {result} Invoke({string.Join(", ", parameters)}) => _value.Invoke({string.Join(", ", arguments)});\n");
                }

                Line("}\n");
            }
        }
    }
}
