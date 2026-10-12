using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators;

/// <summary>
/// Emits native configuration descriptors, typed property implementations, and managed hook dispatchers.
/// </summary>
internal static class PgGucEmitter
{
    /// <summary>
    /// Emits a native descriptor and optional managed dispatcher, returning its stable native symbol.
    /// </summary>
    /// <param name="declaration">The validated configuration contract.</param>
    /// <param name="callback">The assembly-scoped managed symbol.</param>
    /// <param name="managed">The shared managed dispatch class.</param>
    /// <param name="native">The native library source.</param>
    /// <returns>The native descriptor symbol.</returns>
    internal static string Emit(GucModel declaration, string callback, StringBuilder managed, StringBuilder native)
    {
        string symbol = callback.Replace("ankus_managed_", "ankus_guc_");
        string field = declaration.Kind switch
        {
            0 => "boolean",
            1 or 4 => "integer",
            2 => "real",
            _ => "string"
        };
        string type = declaration.Kind switch
        {
            0 => "bool",
            1 or 4 => "int",
            2 => "double",
            _ => "char *"
        };
        string boot = NativeConstant(declaration.Default.Value);
        native.AppendLine($"static {type} {symbol}_value = {boot};");
        if (declaration.Kind == 4)
        {
            native.AppendLine($"static const struct config_enum_entry {symbol}_options[] = {{");
            foreach (GucModel.Label label in declaration.Labels)
            {
                native.AppendLine($"    {{ {Text(label.Name)}, {label.Ordinal}, {(label.Hidden ? "true" : "false")} }},");
            }

            native.AppendLine("    { NULL, 0, false }");
            native.AppendLine("};");
        }

        string hookField = declaration.Kind == 4 ? "enumeration" : field;
        if (declaration.HasHooks)
        {
            EmitManaged(declaration, callback, managed);
            native.AppendLine($"extern int {callback}(int, AnkusValue *, AnkusValue *, int, AnkusError *, AnkusGucRead, AnkusExecute, AnkusGucLog, AnkusMemoryApi *);");
            native.AppendLine($"static AnkusGuc {symbol};");
            if (declaration.Check is not null)
            {
                native.AppendLine($"static bool {symbol}_check({type} *value, void **extra, GucSource source)");
                native.AppendLine("{");
                native.AppendLine($"    return ankus_guc_check(&{symbol}, value, extra, source);");
                native.AppendLine("}");
            }

            string acceptedType = declaration.Kind == 3 ? "const char *" : type;
            native.AppendLine($"static void {symbol}_assign({acceptedType} value, void *extra)");
            native.AppendLine("{");
            native.AppendLine($"    ankus_guc_assign(&{symbol}, &value, extra);");
            native.AppendLine("}");
            if (declaration.Show is not null)
            {
                native.AppendLine($"static const char *{symbol}_show(void)");
                native.AppendLine("{");
                native.AppendLine($"    return ankus_guc_show(&{symbol});");
                native.AppendLine("}");
            }
        }

        native.AppendLine($"static AnkusGuc {symbol} = {{");
        native.AppendLine($"    .name_utf8 = {Text(declaration.Name)}, .short_utf8 = {Text(declaration.ShortDescription)}, .long_utf8 = {Text(declaration.LongDescription)},");
        native.AppendLine($"    .kind = {declaration.Kind}, .context = {declaration.Context}, .flags = {declaration.Flags}U, .unit = {declaration.Unit},");
        native.AppendLine($"    .variable = &{symbol}_value, .boot.{field} = {boot},");
        if (declaration.Kind is 1 or 2)
        {
            native.AppendLine($"    .minimum.{field} = {NativeConstant(declaration.Minimum.Value)}, .maximum.{field} = {NativeConstant(declaration.Maximum.Value)},");
        }

        if (declaration.Kind == 4)
        {
            native.AppendLine($"    .options_utf8 = {symbol}_options,");
        }

        if (declaration.HasHooks)
        {
            native.AppendLine($"    .hook = {callback}, .has_check = {(declaration.Check is not null ? "true" : "false")}, " +
                $".has_assign = {(declaration.Assign is not null ? "true" : "false")}, .has_show = {(declaration.Show is not null ? "true" : "false")},");
            native.AppendLine($"    .assign.{hookField} = {symbol}_assign,");
            if (declaration.Check is not null)
            {
                native.AppendLine($"    .check.{hookField} = {symbol}_check,");
            }

            if (declaration.Show is not null)
            {
                native.AppendLine($"    .show = {symbol}_show,");
            }
        }

        native.AppendLine("};");
        native.AppendLine();
        return symbol;
    }

    /// <summary>
    /// Generates partial property implementations without caching PostgreSQL's mutable native values.
    /// </summary>
    /// <param name="declaration">The configuration contract.</param>
    /// <returns>Compilable partial class and getter source.</returns>
    internal static string EmitProperty(GucModel declaration)
    {
        var source = new StringBuilder();
        GucModel.PropertyContract property = declaration.Property;
        bool namespaced = property.Namespace.Length != 0;
        if (namespaced)
        {
            source.AppendLine("namespace " + property.Namespace + " {");
        }

        foreach (string container in property.Containers)
        {
            source.AppendLine(container);
        }

        string reader = declaration.Kind switch
        {
            0 => "ReadBoolean",
            1 => "ReadInt32",
            2 => "ReadDouble",
            4 => "ReadEnum",
            _ => property.Nullable ? "ReadString" : "ReadRequiredString",
        };
        // Scalars read the variable PostgreSQL assigns, located once; text converts from the server encoding on each read.
        bool located = declaration.Kind != 3;
        string variable = "global::Ankus.Generated.GucVariables." + declaration.Callback;
        string read = $"global::Ankus.CompilerServices.NativeGuc.{reader}({SymbolDisplay.FormatLiteral(declaration.Name, quote: true)}" +
            (located ? $", ref {variable})" : ")");
        if (declaration.Kind == 4)
        {
            read = EnumFromOrdinal(declaration, read);
        }

        source.AppendLine($"{property.Accessibility} {(property.Hides ? "new " : string.Empty)}static partial " +
            $"{declaration.ManagedType} @{property.Name} => {read};");
        foreach (string _ in property.Containers)
        {
            source.AppendLine("}");
        }

        if (namespaced)
        {
            source.AppendLine("}");
        }

        if (located)
        {
            source.AppendLine("namespace Ankus.Generated { internal static partial class GucVariables { internal static nint " +
                declaration.Callback + "; } }");
        }

        return source.ToString();
    }

    /// <summary>
    /// Emits a shared phase dispatcher with independently restored read, logging, and backend scopes.
    /// </summary>
    private static void EmitManaged(GucModel declaration, string callback, StringBuilder managed)
    {
        managed.AppendLine($$"""
                [global::System.Runtime.InteropServices.UnmanagedCallersOnly(
                    EntryPoint = "{{callback}}", CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                private static int {{callback}}(int phase, global::Ankus.CompilerServices.NativeValue* arguments, global::Ankus.CompilerServices.NativeValue* results,
                    int source, global::Ankus.CompilerServices.NativeCallError* error, nint read, nint execute, nint log, nint memory)
                {
                    nint previousBackend = global::Ankus.CompilerServices.NativeBackend.Enter(execute);
                    nint previousRead = global::Ankus.CompilerServices.NativeGuc.Enter(read);
                    nint previousLog = global::Ankus.CompilerServices.NativeLog.Enter(log);
                    nint previousMemory = 0;
                    bool memoryEntered = false;
                    try
                    {
                        previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);
                        memoryEntered = true;
                        {{declaration.ManagedType}} value = {{ReadValue(declaration, "arguments[0]")}};
                        switch (phase)
                        {
            """);
        if (declaration.Check is { } check)
        {
            managed.AppendLine($$"""
                            case 0:
                            {
                                global::Ankus.PgGucCheckResult<{{declaration.ManagedType}}> result = {{check}}(value, (global::Ankus.PgGucSource)source)
                                    ?? throw new global::System.InvalidOperationException("A GUC check hook returned null.");
                                if (!result.IsAccepted)
                                {
                                    global::Ankus.CompilerServices.NativeGuc.WriteCheckError(result.Error, error);
                                    return 2;
                                }

                                results[0] = {{WriteValue(declaration, "result.Value")}};
                                results[1] = global::Ankus.CompilerServices.NativeGuc.FromExtra(result.Extra);
                                return 0;
                            }

                """);
        }

        if (declaration.Assign is { } assign)
        {
            managed.AppendLine($$"""
                            case 1:
                                {{assign}}(value, global::Ankus.CompilerServices.NativeGuc.ReadExtra(arguments[1]));
                                return 0;

                """);
        }

        if (declaration.Show is { } show)
        {
            managed.AppendLine($$"""
                            case 2:
                                results[0] = global::Ankus.CompilerServices.NativeValue.FromString({{show}}(value, global::Ankus.CompilerServices.NativeGuc.ReadExtra(arguments[1]))
                                    ?? throw new global::System.InvalidOperationException("A GUC show hook returned null."));
                                return 0;

                """);
        }

        managed.AppendLine("""
                            default:
                                throw new global::System.InvalidOperationException("The native GUC hook phase is invalid.");
                        }
                    }
                    catch (global::System.Exception exception)
                    {
                        global::Ankus.CompilerServices.NativeError.Write(exception, error);
                        return 1;
                    }
                    finally
                    {
                        if (memoryEntered)
                        {
                            global::Ankus.CompilerServices.NativeMemoryContext.Exit(previousMemory);
                        }

                        global::Ankus.CompilerServices.NativeLog.Exit(previousLog);
                        global::Ankus.CompilerServices.NativeGuc.Exit(previousRead);
                        global::Ankus.CompilerServices.NativeBackend.Exit(previousBackend);
                    }
                }

            """);
    }

    /// <summary>
    /// Gets the single managed entry for hooks on run-time definitions, which selects the delegates registered for the
    /// slot that the native hook passes as its third argument.
    /// </summary>
    internal const string RuntimeHookDispatcher = """
                [global::System.Runtime.InteropServices.UnmanagedCallersOnly(
                    EntryPoint = "ankus_guc_runtime_hook", CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                private static int AnkusGucRuntimeHook(int phase, global::Ankus.CompilerServices.NativeValue* arguments,
                    global::Ankus.CompilerServices.NativeValue* results, int source, global::Ankus.CompilerServices.NativeCallError* error,
                    nint read, nint execute, nint log, nint memory)
                {
                    nint previousBackend = global::Ankus.CompilerServices.NativeBackend.Enter(execute);
                    nint previousRead = global::Ankus.CompilerServices.NativeGuc.Enter(read);
                    nint previousLog = global::Ankus.CompilerServices.NativeLog.Enter(log);
                    nint previousMemory = 0;
                    bool memoryEntered = false;
                    try
                    {
                        previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);
                        memoryEntered = true;
                        return global::Ankus.CompilerServices.NativeGuc.DispatchRuntimeHook(phase, arguments, results, source, error);
                    }
                    catch (global::System.Exception exception)
                    {
                        global::Ankus.CompilerServices.NativeError.Write(exception, error);
                        return 1;
                    }
                    finally
                    {
                        if (memoryEntered)
                        {
                            global::Ankus.CompilerServices.NativeMemoryContext.Exit(previousMemory);
                        }

                        global::Ankus.CompilerServices.NativeLog.Exit(previousLog);
                        global::Ankus.CompilerServices.NativeGuc.Exit(previousRead);
                        global::Ankus.CompilerServices.NativeBackend.Exit(previousBackend);
                    }
                }

        """;

    /// <summary>
    /// Converts a native proposed/current value without reflection or lossy enum casts.
    /// </summary>
    private static string ReadValue(GucModel declaration, string slot) => declaration.Kind switch
    {
        0 => slot + ".Integral != 0",
        1 => "checked((int)" + slot + ".Integral)",
        2 => "global::System.BitConverter.Int64BitsToDouble(" + slot + ".Integral)",
        3 => slot + ".IsNull != 0 ? " + (declaration.Property.Nullable ? "null" :
            "throw new global::System.InvalidOperationException(\"A nonnullable GUC string became null.\")") + " : " + slot + ".ReadString()",
        _ => EnumFromOrdinal(declaration, slot + ".Integral"),
    };

    /// <summary>
    /// Produces allocator-owned output for the native hook's cleanup boundary.
    /// </summary>
    private static string WriteValue(GucModel declaration, string value) => declaration.Kind switch
    {
        0 => "new global::Ankus.CompilerServices.NativeValue { Integral = " + value + " ? 1 : 0 }",
        1 => "new global::Ankus.CompilerServices.NativeValue { Integral = " + value + " }",
        2 => "new global::Ankus.CompilerServices.NativeValue { Integral = global::System.BitConverter.DoubleToInt64Bits(" + value + ") }",
        3 when declaration.Property.Nullable => value +
            " is null ? new global::Ankus.CompilerServices.NativeValue { IsNull = 1 } : global::Ankus.CompilerServices.NativeValue.FromString(" + value + ")",
        3 => "global::Ankus.CompilerServices.NativeValue.FromString(" + value + " ?? throw new global::System.InvalidOperationException(\"A nonnullable GUC check returned null.\"))",
        _ => "new global::Ankus.CompilerServices.NativeValue { Integral = " + value + " switch { " + string.Join(", ", UniqueLabels(declaration).Select(
            label => declaration.ManagedType + ".@" + label.Member + " => " + label.Ordinal.ToString(CultureInfo.InvariantCulture))) +
            ", _ => throw new global::System.InvalidOperationException(\"A GUC check returned an undefined enum value.\") } }",
    };

    /// <summary>
    /// Maps one dense native value to its first declared managed enum member.
    /// </summary>
    private static string EnumFromOrdinal(GucModel declaration, string ordinal)
        => ordinal + " switch { " + string.Join(", ", UniqueLabels(declaration).Select(label => label.Ordinal.ToString(CultureInfo.InvariantCulture) +
            " => " + declaration.ManagedType + ".@" + label.Member)) +
            ", _ => throw new global::System.InvalidOperationException(\"The native GUC contains an undefined enum value.\") }";

    /// <summary>
    /// Omits alias arms from switches while retaining every alias in the native label table.
    /// </summary>
    private static IEnumerable<GucModel.Label> UniqueLabels(GucModel declaration)
        => declaration.Labels.GroupBy(static label => label.Ordinal).Select(static group => group.First());

    /// <summary>
    /// Formats a retained C literal without encoding ambiguity or adjacent escape consumption.
    /// </summary>
    private static string Text(string? value)
        => value is null ? "NULL" : "\"" + string.Concat(Encoding.UTF8.GetBytes(value).Select(static item => "\\" + Convert.ToString(item, 8).PadLeft(3, '0'))) + "\"";

    /// <summary>
    /// Formats native numeric constants, preserving infinity and the sign of zero.
    /// </summary>
    private static string NativeConstant(object? value) => value switch
    {
        null => "NULL",
        bool boolean => boolean ? "true" : "false",
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        double real when double.IsPositiveInfinity(real) => "HUGE_VAL",
        double real when double.IsNegativeInfinity(real) => "(-HUGE_VAL)",
        double real when real == 0 => BitConverter.DoubleToInt64Bits(real) < 0 ? "-0.0" : "0.0",
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        string text => Text(text),
        _ => throw new InvalidOperationException("Unsupported GUC boot constant."),
    };

    /// <summary>
    /// Formats the accessibility levels supported by generated partial declarations.
    /// </summary>
    /// <param name="accessibility">The validated assembly-visible accessibility.</param>
    /// <returns>The corresponding C# declaration keyword.</returns>
    internal static string AccessibilityText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.ProtectedOrInternal => "protected internal",
        _ => "internal",
    };
}
