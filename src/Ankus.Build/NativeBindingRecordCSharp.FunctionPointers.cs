namespace Ankus.Build;

internal static partial class NativeBindingRecordCSharp
{
    private sealed partial class Writer
    {
        /// <summary>
        /// Emits exact borrowed pointer values with finite guarded invocation for complete fixed prototypes.
        /// </summary>
        private void FunctionPointers()
        {
            foreach (NativeBindingIndirectCall call in _indirectCalls)
            {
                string name = _functionPointers[call.FunctionType];
                Document(name);
                Summary("Borrows a native function address with the selected header's exact signature.");
                Line("/// <param name=\"address\">A native function with the matching signature and a lifetime covering every use.</param>");
                Line("/// <remarks>This value does not own its target or register, guard or root a managed callback.</remarks>");
                Line($"[global::Ankus.CompilerServices.NativeFunctionPointer({Number(call.FunctionType)})]");
                Line("[global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential)]");
                Line($"public readonly unsafe partial struct @{name}(void* address) : global::Ankus.IPgNativeType\n{{");
                NativeRecordType storage = graph.Types[call.PointerType];
                Identity(Size(storage.Size), storage.Alignment!.Value);
                Summary("Retains the original borrowed native function address.", "    ");
                Line("    private readonly nint _address = unchecked((nint)address);\n");
                Summary("Reports whether this value contains no native target.", "    ");
                Line("    public bool IsNull => _address == 0;\n");
                Summary("Returns the borrowed address without establishing native ownership or callback lifetime.", "    ");
                Line("    public void* DangerousGetAddress() => unchecked((void*)_address);\n");
                if (headers is not null && call.CanInvoke)
                {
                    (string Name, int Type)[] parameters = [("target", call.PointerType),
                        .. call.Parameters.Select((type, index) => ("argument" + Number(index), type))];
                    Method(name, parameters, call.Result, "Invoke", "GetNativeBody", indirect: true);
                    Summary("Obtains a native body address without invoking its target or entering PostgreSQL.", "    ");
                    Line($"    [global::System.Runtime.InteropServices.LibraryImport(\"Ankus.NativeBodies\", EntryPoint = \"{NativeBindingIndirectImports.Prefix}{Number(call.FunctionType)}\")]");
                    Line("    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = [typeof(global::System.Runtime.CompilerServices.CallConvCdecl)])]");
                    Line("    [global::System.Runtime.InteropServices.SuppressGCTransition]");
                    Line("    private static partial nint GetNativeBody();\n");
                }

                Line("}\n");
            }
        }
    }
}
