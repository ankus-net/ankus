namespace Ankus.Build;

internal static partial class NativeBindingRecordCSharp
{
    private sealed partial class Writer
    {
        private readonly Dictionary<(string Code, long Count), string> _emptyValues = [];

        /// <summary>
        /// Emits typed managed calls and pure native address imports beside their shared record declarations.
        /// </summary>
        private void Methods(IReadOnlyList<NativeBindingCall> selected)
        {
            Document("NativeMethods");
            Summary("Calls selected native PostgreSQL functions through the active callback's error guard.");
            Line("public static partial class NativeMethods\n{");
            var members = new HashSet<string>(selected.Select(static call => call.Name), StringComparer.Ordinal) { "NativeMethods" };
            foreach (NativeBindingCall call in selected)
            {
                // A parameterless Finalize declaration has special C# destructor diagnostics even on a static class.
                string method = call.Name == "NativeMethods" || call is { Name: "Finalize", Parameters.Count: 0 }
                    ? Unique(members, "Native_" + call.Name) : call.Name;
                string accessor = Unique(members, "GetNativeBody_" + call.Name);
                Member("NativeMethods." + method);
                Method(call, method, accessor);
                LongOverload(call, method);
                Summary("Obtains a native body address without entering PostgreSQL.", "    ");
                Line($"    [global::System.Runtime.InteropServices.LibraryImport(\"Ankus.NativeBodies\", EntryPoint = \"{NativeBindingCallImports.Prefix}{call.Name}\")]");
                Line("    [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = [typeof(global::System.Runtime.CompilerServices.CallConvCdecl)])]");
                Line("    [global::System.Runtime.InteropServices.SuppressGCTransition]");
                Line($"    private static partial nint @{accessor}();\n");
            }

            Line(NativeBindingDocuments.EndMarker);
            Line("}\n");
        }

        /// <summary>
        /// Adds an overload taking 64-bit integers for C long parameters where C long has 32 bits, as on Windows, so
        /// source that passes a long compiles for every target. A value the narrower C long cannot hold throws.
        /// </summary>
        private void LongOverload(NativeBindingCall call, string method)
        {
            var names = new HashSet<string>(StringComparer.Ordinal) { "allocation", "storage", "arguments", "alignment" };
            var parameters = new List<(string Name, string Code, string Argument, bool Pointer)>();
            bool narrowed = false;
            for (int index = 0; index < call.Parameters.Count; index++)
            {
                string observed = index < call.Symbol.ParameterNames.Count ? call.Symbol.ParameterNames[index] : "";
                string name = Unique(names, observed.Length == 0 ? "argument" + Number(index) : observed);
                NativeRecordType type = Canonical(call.Parameters[index].StorageType);
                Value value = CallValue(call.Parameters[index].StorageType);
                if (type is { Kind: "scalar", Name: "long" or "unsigned long", Size: 4 })
                {
                    bool signed = type.Name == "long";
                    string narrow = signed ? "NarrowLong" : "NarrowUnsignedLong";
                    parameters.Add((name, signed ? "long" : "ulong",
                        $"global::Ankus.CompilerServices.NativeRawCall.{narrow}(@{name}, \"{name}\")", false));
                    narrowed = true;
                }
                else
                {
                    parameters.Add((name, value.Code, "@" + name, value.Pointer || value.Code.Contains('*', StringComparison.Ordinal)));
                }
            }

            if (!narrowed)
            {
                return;
            }

            Value? result = call.Result is { } returned ? CallValue(returned.StorageType) : null;
            bool pointers = parameters.Any(static parameter => parameter.Pointer) ||
                (result is not null && (result.Pointer || result.Code.Contains('*', StringComparison.Ordinal)));
            Summary("Invokes " + call.Symbol.NativeName + " with 64-bit values for its C long parameters, which have 32 bits on this target.", "    ");
            foreach ((string name, _, _, _) in parameters)
            {
                Line($"    /// <param name=\"{name}\">The native value; a C long parameter must fit in 32 bits.</param>");
            }

            if (result is not null)
            {
                Line("    /// <returns>The exact native result. Referenced storage retains its native ownership and lifetime.</returns>");
            }

            Line("    /// <exception cref=\"System.ArgumentOutOfRangeException\">A C long value does not fit in 32 bits.</exception>");
            Line("    [global::Ankus.CompilerServices.NativeUnsafeAccess]");
            string signature = string.Join(", ", parameters.Select(static parameter => parameter.Code + " @" + parameter.Name));
            string arguments = string.Join(", ", parameters.Select(static parameter => parameter.Argument));
            string returns = result is null ? "void" : result.Code;
            Line($"    public static {(pointers ? "unsafe " : "")}{returns} @{method}({signature})\n        => @{method}({arguments});\n");
        }

        /// <summary>
        /// Copies exact value bytes into independently aligned native storage and releases owned storage on every exit.
        /// </summary>
        private void Method(NativeBindingCall call, string method, string accessor, string visibility = "public")
            => Method(call.Symbol.NativeName,
                [.. call.Parameters.Select((parameter, index) =>
                    (index < call.Symbol.ParameterNames.Count ? call.Symbol.ParameterNames[index] : "", parameter.StorageType))],
                call.Result?.StorageType, method, accessor, visibility, direct: call.Symbol.CannotRaise);

        /// <summary>
        /// Shares managed frame transport independently of the native target's declaration or lookup mechanism.
        /// </summary>
        private void Method(string nativeName, (string Name, int Type)[] nativeParameters, int? nativeResult,
            string method, string accessor, string visibility = "public", bool indirect = false, bool direct = false)
        {
            NativeBindingCallFrame frame = NativeBindingCallFrameLayout.Create(graph,
                [.. nativeParameters.Select(static parameter => parameter.Type)], nativeResult);
            var names = new HashSet<string>(StringComparer.Ordinal) { "allocation", "storage", "arguments", "alignment" };
            var parameters = new List<(string Name, Value Value)>();
            for (int index = 0; index < nativeParameters.Length; index++)
            {
                string observed = nativeParameters[index].Name;
                string name = Unique(names, observed.Length == 0 ? "argument" + Number(index) : observed);
                parameters.Add((name, CallValue(nativeParameters[index].Type)));
            }

            Value? result = nativeResult is int resultType ? CallValue(resultType) : null;
            bool heap = frame.AllocationSize > 4096;
            bool separate = indirect || heap;
            // A body the headers show cannot raise an error runs as a C caller would run it, without the guard.
            direct &= !separate;
            Summary("Invokes " + nativeName + (direct ? ", which cannot raise a PostgreSQL error, without the error guard."
                : " beneath the active PostgreSQL error guard."), "    ");
            foreach ((string name, _) in parameters.Skip(indirect ? 1 : 0))
            {
                Line($"    /// <param name=\"{name}\">The exact native value; referenced addresses must remain valid for the complete call.</param>");
            }

            if (result is not null)
            {
                Line("    /// <returns>The exact native result. Referenced storage retains its native ownership and lifetime.</returns>");
            }

            Line("    /// <remarks>Requires the matching native binding and an active backend callback. Raw pointers and callback addresses remain the caller's responsibility.</remarks>");
            if (visibility == "public")
            {
                Line("    [global::Ankus.CompilerServices.NativeUnsafeAccess]");
            }

            string signature = string.Join(", ", parameters.Skip(indirect ? 1 : 0).Select(static parameter => parameter.Value.Code + " @" + parameter.Name));
            // A call with a stack frame validates its binding in the call that runs it, which looks the body up only once
            // the binding is accepted. An indirect call first rejects a null target, and a heap frame is allocated only
            // after validation, so both validate first.
            string entry = direct ? "InvokeDirect" : "Invoke";
            string major = Number(graph.Target.PostgresVersion / 10000);
            string body = separate ? $"@{accessor}()" : $"\"__ANKUS_RECORD_IDENTITY__\"u8, {major}, {AcceptedReference}, &@{accessor}";
            string unsafeModifier = frame.AllocationSize == 0 && separate ? "" : "unsafe ";
            string hide = parameters.Count == 0 && method != "Equals" ? Hide(method) : "";
            string staticModifier = indirect ? "" : "static ";
            Line($"    {visibility} {hide}{staticModifier}{unsafeModifier}{result?.Code ?? "void"} @{method}({signature})\n    {{");
            if (separate)
            {
                Line($"        global::Ankus.CompilerServices.NativeRawCall.ValidateBinding(\"__ANKUS_RECORD_IDENTITY__\"u8, {major}, {AcceptedReference});");
            }

            if (indirect)
            {
                Line("        if (IsNull)\n        {\n            throw new global::System.InvalidOperationException(\"A null native function pointer cannot be invoked.\");\n        }\n");
            }

            if (frame.AllocationSize == 0)
            {
                Line($"        global::Ankus.CompilerServices.NativeRawCall.{entry}({body}, [], 0, 0);\n    }}\n");
                return;
            }

            Line(heap ? $"        byte* allocation = (byte*)global::System.Runtime.InteropServices.NativeMemory.Alloc({NativeSize(frame.AllocationSize)});"
                : $"        byte* allocation = stackalloc byte[{Number(frame.AllocationSize)}];");
            if (heap)
            {
                Line("        try\n        {");
            }

            string indent = heap ? "            " : "        ";
            Line($"{indent}nuint alignment = {NativeSize(frame.Alignment - 1)};");
            Line($"{indent}nuint storage = checked((nuint)allocation + alignment) & ~alignment;");
            if (parameters.Count != 0)
            {
                Line($"{indent}global::System.Span<global::Ankus.CompilerServices.NativeCallArgument> arguments = new((void*)storage, {Number(parameters.Count)});");
                for (int index = 0; index < parameters.Count; index++)
                {
                    (string name, Value value) = parameters[index];
                    string address = "storage + " + NativeSize(frame.Arguments[index]);
                    Line($"{indent}arguments[{Number(index)}] = new(unchecked((nint)checked({address})), {NativeSize(value.Size)});");
                    if (value.Size != 0)
                    {
                        string argument = indirect && index == 0 ? "this" : value.Pointer ? $"unchecked((nint)@{name})" : "@" + name;
                        Line($"{indent}global::System.Runtime.CompilerServices.Unsafe.WriteUnaligned((void*)({address}), {argument});");
                    }
                }
            }

            string resultAddress = result is null ? "0" : "unchecked((nint)checked(storage + " + NativeSize(frame.Result) + "))";
            Line($"{indent}global::Ankus.CompilerServices.NativeRawCall.{entry}({body}, {(parameters.Count == 0 ? "[]" : "arguments")}, {resultAddress}, {NativeSize(result?.Size ?? 0)});");
            if (result is not null)
            {
                string read = $"global::System.Runtime.CompilerServices.Unsafe.ReadUnaligned<{result.Storage}>((void*)checked(storage + {NativeSize(frame.Result)}))";
                if (result.Pointer)
                {
                    read = $"unchecked(({result.Code}){read})";
                }

                Line(result.Size == 0 ? indent + "return default;"
                    : $"{indent}return {read};");
            }

            if (heap)
            {
                Line("        }\n        finally\n        {\n            global::System.Runtime.InteropServices.NativeMemory.Free(allocation);\n        }");
            }

            Line("    }\n");
        }

        /// <summary>
        /// Maps actual native bytes, using a distinct logical token for complete objects with no bytes.
        /// </summary>
        private Value CallValue(NativeBindingCallValue value)
            => CallValue(value.StorageType);

        /// <summary>
        /// Shares empty logical values by declaration or array shape, independently of native qualifications.
        /// </summary>
        private Value CallValue(int index)
        {
            NativeRecordType type = Canonical(index);
            if (type.Size == 0)
            {
                (string Code, long Count) key = type.Kind switch
                {
                    "record" => ("@" + _declarations[type.Declaration!.Value], -1),
                    "array" => (CallValue(type.Element!.Value).Code, type.Count!.Value),
                    _ => throw new FormatException("A zero-size native value requires a record or fixed array."),
                };
                if (!_emptyValues.TryGetValue(key, out string? name))
                {
                    string prefix = type.Declaration is int declaration ? _declarations[declaration] : "NativeEmpty" + Number(graph.Types[index].Canonical);
                    name = Unique(_names, prefix + "Value");
                    _emptyValues.Add(key, name);
                }

                return new("@" + name, 0, null);
            }

            Value mapped = Map(index);
            if (mapped.Size != graph.Types[index].Size)
            {
                throw new FormatException("A managed native call requires the exact declared object size.");
            }

            return mapped;
        }

        /// <summary>
        /// Emits a size already proved representable by the selected target, including 64-bit frame offsets.
        /// </summary>
        private static string NativeSize(long value) => value <= uint.MaxValue ? Number(value) + "U" : "unchecked((nuint)" + Number(value) + "UL)";
    }
}
