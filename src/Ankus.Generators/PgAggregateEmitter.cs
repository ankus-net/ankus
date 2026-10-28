using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Emits aggregate support dispatchers and PostgreSQL aggregate declarations.
/// </summary>
internal static class PgAggregateEmitter
{
    /// <summary>
    /// Renders a closed aggregate helper with initialization selection outside its cached native body.
    /// </summary>
    /// <param name="helper">The minimal immutable conversion and invocation inputs.</param>
    /// <returns>The managed, native and linker-export fragments.</returns>
    internal static HelperEmission CreateHelperBoundary(AggregateHelperBoundaryModel helper)
    {
        string callback = helper.Callback;
        var managed = new StringBuilder();
        var header = new StringBuilder();
        var native = new StringBuilder();
        var exports = new StringBuilder();
        string nativeName = callback.Replace("ankus_managed_", "ankus_fn_");
        helper.Invocation.Emit(managed, callback);
        managed.AppendLine("    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(");
        managed.AppendLine($"        EntryPoint = \"{callback}\",");
        managed.AppendLine("        CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        managed.AppendLine($"    private static int {callback}(");
        managed.AppendLine("        global::Ankus.CompilerServices.NativeValue* arguments, global::Ankus.CompilerServices.NativeValue* result,");
        managed.AppendLine("        global::Ankus.CompilerServices.NativeCallError* error, nint execute, global::Ankus.CompilerServices.NativeValue* metadata,");
        managed.AppendLine("        int metadataCount, nint owner, nint api, nint memory)");
        managed.AppendLine("    {");
        managed.AppendLine("        nint previous = global::Ankus.CompilerServices.NativeBackend.Enter(execute);");
        managed.AppendLine("        nint previousMemory = 0;");
        managed.AppendLine("        bool memoryEntered = false;");
        managed.AppendLine("        try");
        managed.AppendLine("        {");
        managed.AppendLine("            previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);");
        managed.AppendLine("            memoryEntered = true;");
        managed.AppendLine("            global::Ankus.PgAggregateContext context = global::Ankus.CompilerServices.NativeAggregate.Enter(");
        managed.AppendLine("                new global::System.ReadOnlySpan<global::Ankus.CompilerServices.NativeValue>(metadata, metadataCount), owner, api);");
        managed.AppendLine("            try");
        managed.AppendLine("            {");
        if (helper.Result.Datum?.HasRelations == true || helper.Types.Any(static type => type.Datum?.HasRelations == true))
        {
            managed.AppendLine("                using var relationScope = new global::Ankus.CompilerServices.NativeRelationScope();");
        }

        var arguments = new List<string>();
        for (int index = 0; index < helper.Types.Count; index++)
        {
            string argument = helper.Types[index].Read("arguments[" + index.ToString(CultureInfo.InvariantCulture) + "]", helper.Parameters[index].Precision);
            arguments.Add(helper.Types[index].Datum?.HasRelations == true ? "relationScope.Add(" + argument + ")" : argument);
        }

        string invocation = helper.Invocation.Read(callback, arguments);
        if (helper.Result.Datum?.HasRelations == true)
        {
            invocation = "relationScope.Add(" + invocation + ")";
        }

        managed.AppendLine("                " + helper.Result.Managed + " value = " + invocation + ";");
        if (helper.Result.IsManagedState)
        {
            managed.AppendLine("                *result = global::Ankus.CompilerServices.NativeAggregate.Write(value);");
        }
        else if (helper.Result.Datum?.IsInternal == true)
        {
            managed.AppendLine("                *result = global::Ankus.CompilerServices.NativeAggregate.WriteInternal(value);");
        }
        else
        {
            FunctionType datum = helper.Result.Datum!;
            if (datum.Nullable || datum.Reference)
            {
                managed.AppendLine("                if (value is null)");
                managed.AppendLine("                {");
                managed.AppendLine("                    result->IsNull = 1;");
                managed.AppendLine("                    return 0;");
                managed.AppendLine("                }");
                managed.AppendLine();
            }

            string value = datum.Nullable && !datum.Reference ? "value.Value" : "value";
            managed.AppendLine("                " + ManagedConversion.Write(datum, value, "result", helper.ResultPrecision?.Suffix ?? string.Empty));
        }

        managed.AppendLine("                return 0;");
        managed.AppendLine("            }");
        managed.AppendLine("            finally");
        managed.AppendLine("            {");
        managed.AppendLine("                global::Ankus.CompilerServices.NativeAggregate.Exit(context);");
        managed.AppendLine("            }");
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

        string[] required = [.. helper.Types.Select(static value => value.Nullable ? "false" : "true"), .. helper.Deserialize ? new[] { "false" } : []];
        string[] internalArguments = [.. helper.Types.Select(static value => value.IsManagedState ? "true" : "false"), .. helper.Deserialize ? new[] { "false" } : []];
        string[] polymorphic = [.. helper.Types.Select(static value => value.Datum?.UsesRawTransport == true ? "true" : "false"), .. helper.Deserialize ? new[] { "false" } : []];
        string count = required.Length.ToString(CultureInfo.InvariantCulture);
        string capacity = Math.Max(1, required.Length).ToString(CultureInfo.InvariantCulture);
        header.AppendLine($"extern int {callback}(const AnkusValue *, AnkusValue *, AnkusError *, AnkusExecute, const AnkusValue *, int, void *, void *, AnkusMemoryApi *);");
        header.AppendLine($"PGDLLEXPORT Datum {nativeName}(PG_FUNCTION_ARGS);");
        header.AppendLine($"PG_FUNCTION_INFO_V1({nativeName});");
        header.AppendLine($"PGDLLEXPORT Datum {nativeName}(PG_FUNCTION_ARGS)");
        header.AppendLine("{");
        native.AppendLine($"    const bool required[{capacity}] = {{{(required.Length == 0 ? "false" : string.Join(", ", required))}}};");
        native.AppendLine($"    const bool internal_arguments[{capacity}] = {{{(internalArguments.Length == 0 ? "false" : string.Join(", ", internalArguments))}}};");
        native.AppendLine($"    const bool polymorphic[{capacity}] = {{{(polymorphic.Length == 0 ? "false" : string.Join(", ", polymorphic))}}};");
        native.AppendLine($"    return ankus_aggregate_call(fcinfo, {callback}, {count}, required, internal_arguments, " +
            $"{(helper.Result.IsManagedState ? "true" : "false")}, {(helper.Deserialize ? "true" : "false")}, polymorphic, " +
            $"{(helper.Result.Datum?.UsesRawTransport == true ? "true" : "false")});");
        native.AppendLine("}");
        native.AppendLine();
        exports.AppendLine(nativeName);
        exports.AppendLine("pg_finfo_" + nativeName);
        return new(managed.ToString(), new(header.ToString(), native.ToString()), exports.ToString());
    }

    /// <summary>
    /// Carries an aggregate helper's rendered owned-state boundary independently of graph composition.
    /// </summary>
    /// <param name="Managed">The constrained call and managed ownership/error boundary.</param>
    /// <param name="Native">The native header and body with initialization selected during composition.</param>
    /// <param name="Exports">The native function and function-info linker exports.</param>
    internal sealed record HelperEmission(string Managed, NativeFunctionEmission Native, string Exports);

}
