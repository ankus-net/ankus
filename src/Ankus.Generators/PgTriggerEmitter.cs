using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Emits managed trigger callbacks and PostgreSQL trigger function entry points.
/// </summary>
internal static class PgTriggerEmitter
{
    /// <summary>
    /// Renders a validated callback independently of SQL and current initialization selection.
    /// </summary>
    /// <param name="target">The fully qualified managed callback invocation target.</param>
    /// <param name="callback">The assembly-specific native callback symbol.</param>
    /// <returns>The immutable managed, native and linker artifacts.</returns>
    internal static FunctionEmission EmitBoundary(string target, string callback)
    {
        var managed = new StringBuilder();
        var native = new StringBuilder();
        var header = new StringBuilder();
        var exports = new StringBuilder();
        string nativeName = callback.Replace("ankus_managed_", "ankus_fn_");
        managed.AppendLine("    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(");
        managed.AppendLine($"        EntryPoint = \"{callback}\",");
        managed.AppendLine("        CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        managed.AppendLine($"    private static int {callback}(");
        managed.AppendLine("        global::Ankus.CompilerServices.NativeValue* arguments, global::Ankus.CompilerServices.NativeValue* result,");
        managed.AppendLine("        global::Ankus.CompilerServices.NativeCallError* error, nint execute, nint memory)");
        managed.AppendLine("    {");
        managed.AppendLine("        nint previous = global::Ankus.CompilerServices.NativeBackend.Enter(execute);");
        managed.AppendLine("        nint previousMemory = 0;");
        managed.AppendLine("        bool memoryEntered = false;");
        managed.AppendLine("        try");
        managed.AppendLine("        {");
        managed.AppendLine("            previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);");
        managed.AppendLine("            memoryEntered = true;");
        managed.AppendLine("            global::Ankus.PgTriggerContext context = global::Ankus.CompilerServices.NativeValue.ReadTriggerContext(");
        managed.AppendLine("                new global::System.ReadOnlySpan<global::Ankus.CompilerServices.NativeValue>(arguments, 16));");
        managed.AppendLine("            global::Ankus.PgHeapTuple? value = " +
            target + "(context);");
        managed.AppendLine("            if (context.Timing == global::Ankus.PgTriggerTiming.After)");
        managed.AppendLine("            {");
        managed.AppendLine("                result->IsNull = 1;");
        managed.AppendLine("                return 0;");
        managed.AppendLine("            }");
        managed.AppendLine();
        managed.AppendLine("            if (context.Level == global::Ankus.PgTriggerLevel.Statement)");
        managed.AppendLine("            {");
        managed.AppendLine("                if (value is not null)");
        managed.AppendLine("                {");
        managed.AppendLine("                    throw new global::Ankus.PgException(\"39P01\", \"A BEFORE STATEMENT trigger must return null.\");");
        managed.AppendLine("                }");
        managed.AppendLine();
        managed.AppendLine("                result->IsNull = 1;");
        managed.AppendLine("                return 0;");
        managed.AppendLine("            }");
        managed.AppendLine();
        managed.AppendLine("            if (value is null)");
        managed.AppendLine("            {");
        managed.AppendLine("                result->IsNull = 1;");
        managed.AppendLine("                return 0;");
        managed.AppendLine("            }");
        managed.AppendLine();
        managed.AppendLine("            if (context.Operation == global::Ankus.PgTriggerOperation.Delete)");
        managed.AppendLine("            {");
        managed.AppendLine("                result->IsNull = 0;");
        managed.AppendLine("                return 0;");
        managed.AppendLine("            }");
        managed.AppendLine();
        managed.AppendLine("            if (global::Ankus.CompilerServices.NativeValue.IsUnchangedTriggerRow(context, value))");
        managed.AppendLine("            {");
        managed.AppendLine("                // PostgreSQL keeps the row it passed in, as a C trigger returning it does.");
        managed.AppendLine("                result->IsNull = 0;");
        managed.AppendLine("                return 0;");
        managed.AppendLine("            }");
        managed.AppendLine();
        managed.AppendLine("            *result = global::Ankus.CompilerServices.NativeValue.FromTriggerRow(context, value);");
        managed.AppendLine("            return 0;");
        managed.AppendLine("        }");
        managed.AppendLine("        catch (global::System.Exception exception)");
        managed.AppendLine("        {");
        managed.AppendLine("            global::Ankus.CompilerServices.NativeError.Write(exception, error);");
        managed.AppendLine("            return 1;");
        managed.AppendLine("        }");
        managed.AppendLine("        finally");
        managed.AppendLine("        {");
        managed.AppendLine("            if (memoryEntered)");
        managed.AppendLine("            {");
        managed.AppendLine("                global::Ankus.CompilerServices.NativeMemoryContext.Exit(previousMemory);");
        managed.AppendLine("            }");
        managed.AppendLine("            global::Ankus.CompilerServices.NativeBackend.Exit(previous);");
        managed.AppendLine("        }");
        managed.AppendLine("    }");
        managed.AppendLine();
        header.AppendLine($"extern int {callback}(const AnkusValue *, AnkusValue *, AnkusError *, AnkusExecute, AnkusMemoryApi *);");
        header.AppendLine($"PGDLLEXPORT Datum {nativeName}(PG_FUNCTION_ARGS);");
        header.AppendLine($"PG_FUNCTION_INFO_V1({nativeName});");
        header.AppendLine($"PGDLLEXPORT Datum {nativeName}(PG_FUNCTION_ARGS)");
        header.AppendLine("{");

        native.AppendLine($"    return ankus_trigger_call(fcinfo, {callback});");
        native.AppendLine("}");
        native.AppendLine();
        exports.AppendLine(nativeName);
        exports.AppendLine("pg_finfo_" + nativeName);
        return new(managed.ToString(), new(header.ToString(), native.ToString()), exports.ToString(), nativeName, false);
    }
}
