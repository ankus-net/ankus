using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Emits typed managed iterator callbacks and native PostgreSQL set entry points.
/// </summary>
internal static class PgSetEmitter
{
    /// <summary>
    /// Appends a set function's managed callbacks, native wrapper, SQL, and export names.
    /// </summary>
    /// <param name="method">The attributed managed method.</param>
    /// <param name="parameters">The validated managed parameters and their SQL slots.</param>
    /// <param name="declaration">The validated SQL declaration.</param>
    /// <param name="set">The validated set result.</param>
    /// <param name="callback">The assembly-specific managed callback symbol.</param>
    /// <param name="ensureInitialized">Whether the native entry point must complete deferred managed initialization.</param>
    /// <param name="managed">The generated managed source.</param>
    /// <param name="native">The generated native source.</param>
    /// <param name="exports">The native linker export list.</param>
    /// <param name="providers">Extension-owned types used to qualify selected SQL.</param>
    /// <returns>The SQL function contract to render after dependency resolution.</returns>
    internal static SqlFunction Emit(MethodInvocation method, FunctionParameter[] parameters, FunctionDeclaration declaration, SetResult set, string callback,
        bool ensureInitialized,
        StringBuilder managed, StringBuilder native, StringBuilder exports, SqlTypeProviders providers)
    {
        FunctionEmission emission = EmitBoundary(method, parameters, set, callback);
        emission.AppendTo(managed, native, exports, ensureInitialized);
        return SqlFunction.Create(declaration, declaration.Arguments(providers), set.TemplateSql(providers), emission.NativeName, false);
    }

    /// <summary>
    /// Renders the iterator callback and native entry independently of graph resolution and initialization selection.
    /// </summary>
    /// <param name="method">The detached managed invocation and return policies.</param>
    /// <param name="parameters">The ordered managed and SQL parameter contracts.</param>
    /// <param name="set">The validated iterator output contract.</param>
    /// <param name="callback">The assembly-specific callback identity.</param>
    /// <returns>The immutable managed, native and export artifacts.</returns>
    internal static FunctionEmission EmitBoundary(MethodInvocation method, FunctionParameter[] parameters, SetResult set, string callback)
    {
        var managed = new StringBuilder();
        var native = new StringBuilder();
        var header = new StringBuilder();
        string nativeName = callback.Replace("ankus_managed_", "ankus_fn_");
        method.AppendAccessor(callback, managed);
        managed.AppendLine("    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(");
        managed.AppendLine($"        EntryPoint = \"{callback}\",");
        managed.AppendLine("        CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        managed.AppendLine($"    private static int {callback}(int operation, nint* iterator, global::Ankus.CompilerServices.NativeValue* arguments,");
        managed.AppendLine("        global::Ankus.CompilerServices.NativeValue* columns, global::Ankus.CompilerServices.NativeCallError* error, nint execute, nint memory, nint functionCall)");
        managed.AppendLine("    {");
        managed.AppendLine("        nint previous = global::Ankus.CompilerServices.NativeBackend.Enter(execute, operation == 3);");
        managed.AppendLine("        nint previousMemory = 0;");
        managed.AppendLine("        bool memoryEntered = false;");
        managed.AppendLine("        try");
        managed.AppendLine("        {");
        managed.AppendLine("            previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);");
        managed.AppendLine("            memoryEntered = true;");
        managed.AppendLine("            if (operation is 2 or 3)");
        managed.AppendLine("            {");
        managed.AppendLine("                global::Ankus.CompilerServices.NativeSet.Dispose(ref *iterator);");
        managed.AppendLine("                return 0;");
        managed.AppendLine("            }");
        managed.AppendLine();
        managed.AppendLine("            if (operation == 0)");
        managed.AppendLine("            {");
        bool hasRelations = parameters.Any(static parameter => parameter.Type?.HasRelations == true);
        if (hasRelations)
        {
            managed.AppendLine("                using var relationScope = new global::Ankus.CompilerServices.NativeRelationScope();");
        }

        if (parameters.Any(static parameter => parameter.IsFunctionContext))
        {
            managed.AppendLine("                global::Ankus.PgFunctionContext functionContext = global::Ankus.CompilerServices.NativeBackend.CaptureFunction(functionCall);");
        }

        IEnumerable<string> arguments = parameters.Select(static parameter => parameter.Type?.HasRelations == true
            ? "relationScope.Add(" + parameter.ReadExpression() + ")" : parameter.ReadExpression());
        managed.AppendLine($"                *iterator = global::Ankus.CompilerServices.NativeSet.Create<{set.Managed}>(" +
            method.Invoke(arguments, callback) +
            (hasRelations ? ", relationScope.Detach()" : string.Empty) + ");");
        managed.AppendLine("                return 0;");
        managed.AppendLine("            }");
        managed.AppendLine();
        bool relationResults = set.Columns.Any(static column => column.HasRelations);
        if (relationResults)
        {
            managed.AppendLine("            using var resultRelations = global::Ankus.CompilerServices.NativeRelationScope.ForIterator(*iterator);");
        }

        managed.AppendLine($"            if (!global::Ankus.CompilerServices.NativeSet.MoveNext<{set.Managed}>(*iterator, out {set.Managed} value))");
        managed.AppendLine("            {");
        managed.AppendLine("                return 2;");
        managed.AppendLine("            }");
        managed.AppendLine();
        if (relationResults)
        {
            managed.AppendLine("            try");
            managed.AppendLine("            {");
            for (int index = 0; index < set.Columns.Count; index++)
            {
                if (set.Columns[index].HasRelations)
                {
                    managed.AppendLine($"                resultRelations.Add({set.Values[index]});");
                }
            }

            managed.AppendLine("            }");
            managed.AppendLine("            catch (global::System.Exception captureFailure)");
            managed.AppendLine("            {");
            for (int index = 0; index < set.Columns.Count; index++)
            {
                if (set.Columns[index].HasRelations)
                {
                    managed.AppendLine($"                resultRelations.ReleaseFailed({set.Values[index]}, captureFailure);");
                }
            }

            managed.AppendLine("                throw;");
            managed.AppendLine("            }");
            managed.AppendLine();
        }

        for (int index = 0; index < set.Columns.Count; index++)
        {
            FunctionType column = set.Columns[index];
            string ordinal = index.ToString(CultureInfo.InvariantCulture);
            string value = "cell" + ordinal;
            managed.AppendLine($"            {column.Managed}{(column.Nullable ? "?" : string.Empty)} {value} = {set.Values[index]};");
            bool optional = column.Nullable || column.Reference;
            if (optional)
            {
                managed.AppendLine($"            if ({value} is null)");
                managed.AppendLine("            {");
                managed.AppendLine($"                columns[{ordinal}].IsNull = 1;");
                managed.AppendLine("            }");
                managed.AppendLine("            else");
                managed.AppendLine("            {");
            }

            string present = column.Nullable && !column.Reference ? value + ".Value" : value;
            managed.AppendLine((optional ? "                " : "            ") + ManagedConversion.Write(column, present,
                "(columns + " + ordinal + ")", method.NumericPrecision?.Suffix ?? string.Empty));
            if (optional)
            {
                managed.AppendLine("            }");
                managed.AppendLine();
            }
        }

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
        managed.AppendLine("            global::Ankus.CompilerServices.NativeBackend.Exit(previous, operation == 3);");
        managed.AppendLine("        }");
        managed.AppendLine("    }");
        managed.AppendLine();

        int mode = method.SetMode;
        FunctionParameter[] sqlParameters = [.. parameters.Where(static parameter => !parameter.IsInjected)];
        string required = sqlParameters.Length == 0 ? "false" : string.Join(", ", sqlParameters.Select(static parameter =>
            parameter.Type!.Nullable ? "false" : "true"));
        string polymorphic = sqlParameters.Length == 0 ? "false" : string.Join(", ", sqlParameters.Select(static parameter =>
            parameter.Type!.UsesRawTransport ? "true" : "false"));
        header.AppendLine($"extern int {callback}(int, void **, const AnkusValue *, AnkusValue *, AnkusError *, AnkusExecute, AnkusMemoryApi *, FunctionCallInfo);");
        header.AppendLine($"PGDLLEXPORT Datum {nativeName}(PG_FUNCTION_ARGS);");
        header.AppendLine($"PG_FUNCTION_INFO_V1({nativeName});");
        header.AppendLine($"PGDLLEXPORT Datum {nativeName}(PG_FUNCTION_ARGS)");
        header.AppendLine("{");
        native.AppendLine($"    const bool required[] = {{ {required} }};");
        native.AppendLine($"    const bool polymorphic[] = {{ {polymorphic} }};");
        native.AppendLine($"    return ankus_set_execute(fcinfo, {callback}, {set.Columns.Count.ToString(CultureInfo.InvariantCulture)}, " +
            $"{sqlParameters.Length.ToString(CultureInfo.InvariantCulture)}, required, {mode.ToString(CultureInfo.InvariantCulture)}, " +
            $"{(set.Columns.Count == 1 && set.Columns[0].IsComposite ? "true" : "false")}, polymorphic, " +
            $"{(set.Columns.Count == 1 && set.Columns[0].UsesRawTransport ? "true" : "false")});");
        native.AppendLine("}");
        native.AppendLine();
        return new(managed.ToString(), new(header.ToString(), native.ToString()), new StringBuilder().AppendLine(nativeName).AppendLine("pg_finfo_" + nativeName).ToString(), nativeName, false);
    }
}
