namespace Ankus.Build;

internal static partial class NativeBindingRecordCSharp
{
    private sealed partial class Writer
    {
        /// <summary>
        /// Exposes actual native objects through typed guarded properties and explicit dangerous address access.
        /// </summary>
        private void Globals(IReadOnlyList<NativeBindingGlobalContract> selected)
        {
            Summary("Accesses selected native PostgreSQL globals through the active callback's error guard.");
            Line("public static partial class NativeGlobals\n{");
            var members = new HashSet<string>(selected.Select(static global => global.Name), StringComparer.Ordinal) { "NativeGlobals" };
            foreach (NativeBindingGlobalContract global in selected)
            {
                string member = global.Name == "NativeGlobals" ? Unique(members, "Native_" + global.Name) : global.Name;
                string hide = member == "Finalize" ? "" : Hide(member);
                string address = Unique(members, "DangerousAddressOf_" + global.Name);
                if (global.Symbol.Linkage == NativeHeaderLinkage.Unavailable)
                {
                    Unavailable(global, member, hide, address);
                    continue;
                }

                string addressAccessor = Unique(members, "GetNativeAddress_" + global.Name);
                if (global.IsComplete)
                {
                    var native = new NativeBindingCallValue(global.Symbol.Type, global.StorageType);
                    Value value = CallValue(native);
                    string read = Unique(members, "Read_" + global.Name);
                    string readAccessor = Unique(members, "GetNativeRead_" + global.Name);
                    string? write = global.CanWrite ? Unique(members, "Write_" + global.Name) : null;
                    Summary("Reads" + (write is null ? "" : " or writes") + " the current native value of " + global.Symbol.NativeName + ".", "    ");
                    Line("    /// <remarks>Requires an active backend callback. Values are copies; referenced native addresses retain their original ownership and lifetime. Native synchronization remains the caller's responsibility.</remarks>");
                    Line("    [global::Ankus.CompilerServices.NativeUnsafeAccess]");
                    string pointerModifier = value.Pointer ? "unsafe " : "";
                    if (write is null)
                    {
                        Line($"    public {hide}static {pointerModifier}{value.Code} @{member} => @{read}();\n");
                    }
                    else
                    {
                        Line($"    public {hide}static {pointerModifier}{value.Code} @{member}\n    {{\n        get => @{read}();\n        set => @{write}(value);\n    }}\n");
                    }

                    if (!global.RequiresTypedAccess && !global.Symbol.IsThreadLocal && value.Size != 0)
                    {
                        InPlace(global, value, Unique(members, "Address_" + global.Name), addressAccessor, read, write);
                    }
                    else
                    {
                        Method(new(global.Name, global.Symbol, [], native), read, readAccessor, "private");
                        GlobalImport(global.Name, NativeBindingGlobalOperation.Read, readAccessor);
                        if (write is not null)
                        {
                            string writeAccessor = Unique(members, "GetNativeWrite_" + global.Name);
                            Method(new(global.Name, global.Symbol, [native], null), write, writeAccessor, "private");
                            GlobalImport(global.Name, NativeBindingGlobalOperation.Write, writeAccessor);
                        }
                    }
                }

                Summary("Obtains the original native address of " + global.Symbol.NativeName + ".", "    ");
                Line("    /// <returns>The original object address, without extending its lifetime or supplying an unknown array extent.</returns>");
                Line("    /// <remarks>Requires an active backend callback. The caller must preserve native const/volatile qualifications, bounds, synchronization and ownership. Thread-local addresses belong to the active backend thread.</remarks>");
                string addressType = PointerTarget(global.StorageType) + "*";
                Line("    [global::Ankus.CompilerServices.NativeUnsafeAccess]");
                Line($"    public static unsafe {addressType} @{address}()\n    {{");
                Line("        nint address = 0;");
                Line($"        global::Ankus.CompilerServices.NativeRawCall.Invoke(\"__ANKUS_RECORD_IDENTITY__\"u8, {Number(graph.Target.PostgresVersion / 10000)}, " +
                    $"{AcceptedReference}, &@{addressAccessor}, [], unchecked((nint)(&address)), (nuint)sizeof(nint));");
                Line($"        return unchecked(({addressType})address);\n    }}\n");
                GlobalImport(global.Name, NativeBindingGlobalOperation.Address, addressAccessor);
            }

            Line("}\n");
        }

        /// <summary>
        /// Keeps the members of a global that the server does not export, marked unsupported on Windows, so code that
        /// guards its use with <see cref="System.OperatingSystem.IsWindows"/> compiles for every target.
        /// </summary>
        private void Unavailable(NativeBindingGlobalContract global, string member, string hide, string address)
        {
            string name = global.Symbol.NativeName;
            string reason = $"PostgreSQL {Number(graph.Target.PostgresVersion / 10000)} does not export {name} from postgres.exe, " +
                "so an extension cannot reach it on Windows. Read a configuration setting with GetConfigOption instead.";
            string platform = $"    [global::System.Runtime.Versioning.UnsupportedOSPlatform(\"windows\", \"{reason}\")]";
            string failure = $"throw new global::System.PlatformNotSupportedException(\"{reason}\")";
            if (global.IsComplete)
            {
                Value value = CallValue(new NativeBindingCallValue(global.Symbol.Type, global.StorageType));
                Summary("Reads" + (global.CanWrite ? " or writes" : "") + " the current native value of " + name + ".", "    ");
                Line($"    /// <remarks>{reason}</remarks>");
                Line(platform);
                Line("    [global::Ankus.CompilerServices.NativeUnsafeAccess]");
                string pointerModifier = value.Pointer ? "unsafe " : "";
                Line(global.CanWrite
                    ? $"    public {hide}static {pointerModifier}{value.Code} @{member}\n    {{\n        get => {failure};\n        set => {failure};\n    }}\n"
                    : $"    public {hide}static {pointerModifier}{value.Code} @{member} => {failure};\n");
            }

            Summary("Obtains the original native address of " + name + ".", "    ");
            Line("    /// <returns>Never returns, because the server does not export the variable.</returns>");
            Line($"    /// <remarks>{reason}</remarks>");
            Line(platform);
            Line("    [global::Ankus.CompilerServices.NativeUnsafeAccess]");
            Line($"    public static unsafe {PointerTarget(global.StorageType)}* @{address}() => {failure};\n");
        }

        /// <summary>
        /// Reads and writes a plain global through its cached address. Copying a variable cannot raise a PostgreSQL error,
        /// so these accesses need no guard; qualified, atomic and thread-local globals keep their guarded C bodies.
        /// </summary>
        private void InPlace(NativeBindingGlobalContract global, Value value, string cache, string accessor, string read, string? write)
        {
            string target = $"(void*)global::Ankus.CompilerServices.NativeRawCall.GlobalAddress(\"__ANKUS_RECORD_IDENTITY__\"u8, " +
                $"{Number(graph.Target.PostgresVersion / 10000)}, {AcceptedReference}, ref @{cache}, &@{accessor})";
            Summary("Caches the address of " + global.Symbol.NativeName + " after its first checked access.", "    ");
            Line($"    private static nint @{cache};\n");
            Summary("Reads " + global.Symbol.NativeName + " in place once its binding and the native guard admit the access.", "    ");
            Line("    /// <returns>A copy of the current native value.</returns>");
            string load = $"global::System.Runtime.CompilerServices.Unsafe.ReadUnaligned<{value.Storage}>({target})";
            Line($"    private static unsafe {value.Code} @{read}() => {(value.Pointer ? $"unchecked(({value.Code}){load})" : load)};\n");
            if (write is not null)
            {
                Summary("Writes " + global.Symbol.NativeName + " in place once its binding and the native guard admit the access.", "    ");
                Line("    /// <param name=\"value\">The exact native value to store.</param>");
                string stored = value.Pointer ? "unchecked((nint)@value)" : "@value";
                Line($"    private static unsafe void @{write}({value.Code} @value) => global::System.Runtime.CompilerServices.Unsafe.WriteUnaligned({target}, {stored});\n");
            }
        }

        /// <summary>
        /// Imports a pure address accessor in the disjoint native global-body namespace.
        /// </summary>
        private void GlobalImport(string name, NativeBindingGlobalOperation operation, string accessor)
        {
            string entry = NativeBindingGlobalImports.Prefix + NativeBindingGlobalSource.OperationName(operation) + "_" + name;
            Summary("Obtains a native global body address without entering PostgreSQL.", "    ");
            Line($"    [global::System.Runtime.InteropServices.LibraryImport(\"Ankus.NativeBodies\", EntryPoint = \"{entry}\")]");
            Line("    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = [typeof(global::System.Runtime.CompilerServices.CallConvCdecl)])]");
            Line("    [global::System.Runtime.InteropServices.SuppressGCTransition]");
            Line($"    private static partial nint @{accessor}();\n");
        }
    }
}
