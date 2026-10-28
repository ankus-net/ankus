namespace Ankus.Build;

internal static partial class NativeBindingRecordCSharp
{
    private sealed partial class Writer
    {
        /// <summary>
        /// Maps each exact pointer signature to its generic-compatible carrier.
        /// </summary>
        private readonly Dictionary<string, string> _pointerValues = new(StringComparer.Ordinal);

        /// <summary>
        /// Retains separate identities for incomplete native pointees.
        /// </summary>
        private readonly Dictionary<int, string> _opaquePointees = [];

        /// <summary>
        /// Identifies a pointee without treating void or an incomplete declaration as allocatable storage.
        /// </summary>
        private string PointerTarget(int index)
        {
            NativeRecordType type = Canonical(index);
            if (type is { Kind: "scalar", Name: "void" })
            {
                return "void";
            }

            if (type.Declaration is int declaration)
            {
                return "@" + _declarations[declaration];
            }

            if (type.Size is null or 0)
            {
                int canonical = graph.Types[index].Canonical;
                if (!_opaquePointees.TryGetValue(canonical, out string? name))
                {
                    name = Unique(_names, "NativeIncomplete" + Number(canonical));
                    _opaquePointees.Add(canonical, name);
                }

                return "@" + name;
            }

            return Map(index).Code;
        }

        /// <summary>
        /// Keeps pointer type identity inside generic containers without an illegal pointer type argument.
        /// </summary>
        private Value ContainerValue(int index)
        {
            Value value = Map(index);
            if (!value.Pointer)
            {
                return value;
            }

            if (!_pointerValues.TryGetValue(value.Code, out string? name))
            {
                name = Unique(_names, "NativePointer" + Number(_pointerValues.Count));
                _pointerValues.Add(value.Code, name);
            }

            return new("@" + name, value.Size, null);
        }

        /// <summary>
        /// Emits pointer-only opaque identities and exact pointer-value carriers for Span and inline arrays.
        /// </summary>
        private void PointerValues()
        {
            foreach ((int _, string name) in _opaquePointees)
            {
                Document(name);
                Summary("Identifies an incomplete native pointee without declaring its size or allocation contract.");
                Line($"public readonly struct @{name}\n{{\n}}\n");
            }

            foreach ((string pointer, string name) in _pointerValues)
            {
                Document(name);
                Summary("Retains a borrowed typed pointer in a generic container without extending its native lifetime.");
                Line("/// <param name=\"address\">The native address with caller-guaranteed type and lifetime.</param>");
                Line("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential)]");
                Line($"public readonly unsafe struct @{name}({pointer} address)\n{{");
                Summary("Retains the original borrowed pointer bits.", "    ");
                Line("    private readonly nint _address = unchecked((nint)address);\n");
                Summary("Reports whether this value contains a null pointer.", "    ");
                Line("    public bool IsNull => _address == 0;\n");
                Summary("Returns the typed address without establishing native ownership or lifetime.", "    ");
                Line($"    public {pointer} DangerousGetAddress() => unchecked(({pointer})_address);\n");
                Summary("Copies the pointer into a generic-compatible value without acquiring ownership.", "    ");
                Line("    /// <param name=\"address\">The borrowed typed pointer.</param>");
                Line("    /// <returns>The same native pointer value.</returns>");
                Line($"    public static implicit operator @{name}({pointer} address) => new(address);\n");
                Summary("Restores the typed pointer without extending its original lifetime.", "    ");
                Line("    /// <param name=\"value\">The borrowed pointer value.</param>");
                Line("    /// <returns>The original typed native address.</returns>");
                Line($"    public static implicit operator {pointer}(@{name} value) => value.DangerousGetAddress();\n");
                Line("}\n");
            }
        }
    }
}
