using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Emits managed dispatch, native conversion and cleanup, and SQL declarations for one supported function.
/// </summary>
internal static class PgFunctionEmitter
{
    /// <summary>
    /// Appends a function's complete boundary and installation declarations to the extension artifacts.
    /// </summary>
    /// <param name="method">The attributed managed method.</param>
    /// <param name="parameterModels">The validated managed parameters and their SQL slots.</param>
    /// <param name="result">The validated scalar result conversion contract.</param>
    /// <param name="declaration">The validated SQL declaration.</param>
    /// <param name="callback">The assembly-specific managed callback symbol.</param>
    /// <param name="ensureInitialized">Whether the native entry point must complete deferred managed initialization.</param>
    /// <param name="managed">The generated managed source.</param>
    /// <param name="native">The generated native source.</param>
    /// <param name="exports">The native linker export list.</param>
    /// <param name="providers">Extension-owned types used to qualify selected SQL.</param>
    /// <returns>The SQL function contract to render after dependency resolution.</returns>
    internal static SqlFunction Emit(MethodInvocation method, FunctionParameter[] parameterModels, FunctionType result, FunctionDeclaration declaration, string callback,
        bool ensureInitialized,
        StringBuilder managed, StringBuilder native, StringBuilder exports, SqlTypeProviders providers)
    {
        FunctionEmission emission = EmitBoundary(method, parameterModels, result, callback);
        emission.AppendTo(managed, native, exports, ensureInitialized);
        return SqlFunction.Create(declaration, declaration.Arguments(providers), SqlSchemaTemplate.Type(result, providers), emission.NativeName, emission.IsPlannerSupport);
    }

    /// <summary>
    /// Renders one scalar dispatcher independently of SQL graph resolution and extension initialization.
    /// </summary>
    /// <param name="method">The detached managed invocation and return policies.</param>
    /// <param name="parameterModels">The ordered managed and SQL parameter contracts.</param>
    /// <param name="result">The validated scalar result contract.</param>
    /// <param name="callback">The assembly-specific callback identity.</param>
    /// <returns>The immutable managed, native and export artifacts.</returns>
    internal static FunctionEmission EmitBoundary(MethodInvocation method, FunctionParameter[] parameterModels, FunctionType result, string callback)
    {
        FunctionType[] parameters = [.. parameterModels.Where(static parameter => !parameter.IsInjected).Select(static parameter => parameter.Type!)];
        string nativeName = callback.Replace("ankus_managed_", "ankus_fn_");
        var managed = new StringBuilder();
        EmitManaged(method, callback, parameterModels, result, managed);
        return new(managed.ToString(), CreateNative(nativeName, callback, parameters, result), new StringBuilder().AppendLine(nativeName).AppendLine("pg_finfo_" + nativeName).ToString(), nativeName,
            parameters.Length == 1 && parameters[0].IsSqlInternal && result.IsSqlInternal &&
            !parameterModels.Any(static parameter => parameter.IsParams));
    }

    private static void EmitManaged(
        MethodInvocation method, string callback, FunctionParameter[] parameters, FunctionType result, StringBuilder source)
    {
        IEnumerable<string> arguments = parameters.Select(static parameter => parameter.Type?.HasRelations == true
            ? "relationScope.Add(" + parameter.ReadExpression(borrowVarlena: true) + ")" : parameter.ReadExpression(borrowVarlena: true));
        method.AppendAccessor(callback, source);
        string invocation = method.Invoke(arguments, callback);
        EmitManaged(callback, result, invocation, method.NumericPrecision?.Suffix ?? string.Empty,
            parameters.Any(static parameter => parameter.IsFunctionContext), source,
            parameters.Any(static parameter => parameter.Type?.HasRelations == true));
    }

    /// <summary>
    /// Emits the shared managed exception and capability boundary around a statically bound invocation.
    /// </summary>
    internal static void EmitManaged(string callback, FunctionType result, string invocation, string numericSuffix,
        bool hasFunctionContext, StringBuilder source, bool hasRelationArguments = false)
    {
        source.AppendLine("    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(");
        source.AppendLine($"        EntryPoint = \"{callback}\",");
        source.AppendLine("        CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        source.AppendLine($"    private static int {callback}(");
        source.AppendLine("        global::Ankus.CompilerServices.NativeValue* arguments, global::Ankus.CompilerServices.NativeValue* result,");
        source.AppendLine("        global::Ankus.CompilerServices.NativeCallError* error, nint execute, nint memory, nint functionCall)");
        source.AppendLine("    {");
        source.AppendLine("        nint previous = global::Ankus.CompilerServices.NativeBackend.Enter(execute);");
        source.AppendLine("        nint previousMemory = 0;");
        source.AppendLine("        bool memoryEntered = false;");
        source.AppendLine("        try");
        source.AppendLine("        {");
        source.AppendLine("            previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);");
        source.AppendLine("            memoryEntered = true;");
        if (hasRelationArguments || result.HasRelations)
        {
            source.AppendLine("            using var relationScope = new global::Ankus.CompilerServices.NativeRelationScope();");
        }

        if (hasFunctionContext)
        {
            source.AppendLine("            global::Ankus.PgFunctionContext functionContext = global::Ankus.CompilerServices.NativeBackend.CaptureFunction(functionCall);");
        }

        if (result.Managed == "void")
        {
            source.AppendLine($"            {invocation};");
        }
        else
        {
            string call = result.HasRelations ? "relationScope.Add(" + invocation + ")" : invocation;
            source.AppendLine($"            {result.Managed}{(result.Nullable ? "?" : string.Empty)} value = {call};");
            bool canBeNull = result.Nullable || result.Reference;
            string value = result.Nullable && !result.Reference ? "value.Value" : "value";
            if (canBeNull)
            {
                source.AppendLine("            if (value is null)");
                source.AppendLine("            {");
                source.AppendLine("                result->IsNull = 1;");
                source.AppendLine("                return 0;");
                source.AppendLine("            }");
                source.AppendLine();
            }

            source.AppendLine("            " + ManagedConversion.Write(result, value, "result", numericSuffix, consumedAtReturn: true));
        }

        source.AppendLine("            return 0;");
        source.AppendLine("        }");
        source.AppendLine("        catch (global::System.Exception exception)");
        source.AppendLine("        {");
        source.AppendLine("            global::Ankus.CompilerServices.NativeError.Write(exception, error);");
        source.AppendLine("            return 1;");
        source.AppendLine("        }");
        source.AppendLine("        finally");
        source.AppendLine("        {");
        source.AppendLine("            if (memoryEntered)");
        source.AppendLine("            {");
        source.AppendLine("                global::Ankus.CompilerServices.NativeMemoryContext.Exit(previousMemory);");
        source.AppendLine("            }");
        source.AppendLine("            global::Ankus.CompilerServices.NativeBackend.Exit(previous);");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
    }

    /// <summary>
    /// Emits a native scalar boundary shared by attributed methods and generated type I/O callbacks.
    /// </summary>
    internal static void EmitNative(
        string name, string callback, FunctionType[] parameters, FunctionType result, bool ensureInitialized, StringBuilder source)
        => CreateNative(name, callback, parameters, result).AppendTo(source, ensureInitialized);

    /// <summary>
    /// Renders native conversion and cleanup while keeping initialization selection outside the cached body.
    /// </summary>
    /// <param name="name">The native entry identity.</param>
    /// <param name="callback">The managed callback identity.</param>
    /// <param name="parameters">The ordered SQL argument conversions.</param>
    /// <param name="result">The scalar result conversion.</param>
    /// <returns>The independently rendered header and body.</returns>
    internal static NativeFunctionEmission CreateNative(string name, string callback, FunctionType[] parameters, FunctionType result)
    {
        var header = new StringBuilder();
        header.AppendLine($"extern int {callback}(const AnkusValue *, AnkusValue *, AnkusError *, AnkusExecute, AnkusMemoryApi *, FunctionCallInfo);");
        header.AppendLine($"PGDLLEXPORT Datum {name}(PG_FUNCTION_ARGS);");
        header.AppendLine($"PG_FUNCTION_INFO_V1({name});");
        header.AppendLine($"PGDLLEXPORT Datum {name}(PG_FUNCTION_ARGS)");
        header.AppendLine("{");
        var source = new StringBuilder();
        string count = parameters.Length.ToString(CultureInfo.InvariantCulture);
        string capacity = Math.Max(1, parameters.Length).ToString(CultureInfo.InvariantCulture);
        bool hasBuffers = parameters.Any(static parameter => parameter.IsBuffer);
        source.AppendLine($"    AnkusValue arguments[{capacity}] = {{0}};");
        if (hasBuffers)
        {
            source.AppendLine($"    AnkusInputBuffer owned[{capacity}] = {{0}};");
        }

        source.AppendLine("    AnkusValue result = {0};");
        source.AppendLine("    AnkusError error;");
        source.AppendLine("    int status;");
        source.AppendLine("    AnkusMemoryApi memory = {0};");
        source.AppendLine("    ankus_error_clear(&error);");
        source.AppendLine("    ankus_memory_initialize(&memory);");
        source.AppendLine("    Oid previous_function;");
        source.AppendLine("    volatile Datum datum = (Datum) 0;");
        source.AppendLine($"    if (PG_NARGS() < {count})");
        source.AppendLine("    {");
        source.AppendLine("        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg(\"Incorrect argument count for generated Ankus function\")));");
        source.AppendLine("    }");
        source.AppendLine();
        string[] required = [.. Enumerable.Range(0, parameters.Length)
            .Where(index => !parameters[index].Nullable)
            .Select(static index => index.ToString(CultureInfo.InvariantCulture))];
        if (required.Length == 1)
        {
            source.AppendLine("    /* SQL NULL in a parameter that cannot hold it returns NULL, as a STRICT function does. */");
            source.AppendLine($"    if (PG_ARGISNULL({required[0]}))");
            source.AppendLine("    {");
            source.AppendLine("        PG_RETURN_NULL();");
            source.AppendLine("    }");
            source.AppendLine();
        }
        else if (required.Length > 1)
        {
            bool every = required.Length == parameters.Length;
            source.AppendLine("    /* SQL NULL in a parameter that cannot hold it returns NULL, as a STRICT function does. */");
            if (every)
            {
                source.AppendLine($"    for (int index = 0; index < {count}; index++)");
            }
            else
            {
                source.AppendLine($"    static const int required[] = {{{string.Join(", ", required)}}};");
                source.AppendLine("    for (int index = 0; index < (int) lengthof(required); index++)");
            }

            source.AppendLine("    {");
            source.AppendLine(every ? "        if (PG_ARGISNULL(index))" : "        if (PG_ARGISNULL(required[index]))");
            source.AppendLine("        {");
            source.AppendLine("            PG_RETURN_NULL();");
            source.AppendLine("        }");
            source.AppendLine("    }");
            source.AppendLine();
        }

        bool compositeArguments = parameters.Any(static parameter => (parameter.Element ?? parameter).IsComposite ||
            (parameter.Element ?? parameter).CustomType is not null);
        string inputIndent = compositeArguments ? "    " : string.Empty;
        source.AppendLine("    /* Read each argument into its managed transport. */");
        if (compositeArguments)
        {
            source.AppendLine("    previous_function = ankus_function_oid;");
            source.AppendLine("    ankus_function_oid = fcinfo->flinfo->fn_oid;");
            source.AppendLine("    PG_TRY();");
            source.AppendLine("    {");
        }

        for (int index = 0; index < parameters.Length; index++)
        {
            FunctionType parameter = parameters[index];
            string argument = index.ToString(CultureInfo.InvariantCulture);
            if (parameter.UsesRawTransport)
            {
                source.AppendLine($"{inputIndent}    ankus_read_polymorphic(fcinfo, {argument}, &arguments[{argument}]);");
                continue;
            }

            source.AppendLine($"{inputIndent}    arguments[{argument}].is_null = PG_ARGISNULL({argument});");
            source.AppendLine($"{inputIndent}    if (!arguments[{argument}].is_null)");
            source.AppendLine(inputIndent + "    {");
            if (parameter.IsInternal)
            {
                source.AppendLine($"{inputIndent}        arguments[{argument}].integral = (int64) (uintptr_t) PG_GETARG_DATUM({argument});");
            }
            else if (parameter.Reader == "cstring")
            {
                source.AppendLine($"{inputIndent}        ankus_read_cstring(PG_GETARG_DATUM({argument}), &arguments[{argument}], &owned[{argument}]);");
            }
            else if (parameter.Reader == "type_receive")
            {
                source.AppendLine($"{inputIndent}        ankus_read_type_receive(PG_GETARG_DATUM({argument}), get_func_rettype(fcinfo->flinfo->fn_oid), &arguments[{argument}]);");
            }
            else if (parameter.CustomType is not null)
            {
                string reader = parameter.IsVarlena ? "ankus_read_varlena" : "ankus_read_custom";
                source.AppendLine($"{inputIndent}        {reader}(PG_GETARG_DATUM({argument}), ankus_declared_argument_type(fcinfo, {argument}), &arguments[{argument}], &owned[{argument}]);");
            }
            else if (parameter.Element is not null)
            {
                source.AppendLine($"{inputIndent}        ankus_read_array(PG_GETARG_DATUM({argument}), &arguments[{argument}], &owned[{argument}]);");
            }
            else if (parameter.RangeSubtype is not null)
            {
                source.AppendLine($"{inputIndent}        ankus_read_range(PG_GETARG_DATUM({argument}), &arguments[{argument}], &owned[{argument}]);");
            }
            else if (parameter.Enumeration is not null)
            {
                source.AppendLine($"{inputIndent}        ankus_read_enum(PG_GETARG_DATUM({argument}), &arguments[{argument}], &owned[{argument}]);");
            }
            else if (parameter.IsComposite)
            {
                source.AppendLine($"{inputIndent}        ankus_read_tuple(PG_GETARG_DATUM({argument}), &arguments[{argument}], &owned[{argument}]);");
            }
            else if (parameter.Reader == "tid")
            {
                source.AppendLine($"{inputIndent}        ankus_read_item_pointer(PG_GETARG_DATUM({argument}), &arguments[{argument}]);");
            }
            else if (parameter.IsTemporal)
            {
                source.AppendLine(
                    $"{inputIndent}        ankus_read_temporal(PG_GETARG_DATUM({argument}), &arguments[{argument}], {parameter.BufferOid});");
            }
            else if (parameter.IsBuffer)
            {
                source.AppendLine($"{inputIndent}        ankus_read_typed_buffer(PG_GETARG_DATUM({argument}),");
                source.AppendLine($"{inputIndent}            &arguments[{argument}], &owned[{argument}], {parameter.BufferOid});");
            }
            else if (parameter.Managed is "float" or "double")
            {
                string bits = parameter.Managed == "float" ? "int32" : "int64";
                source.AppendLine($"{inputIndent}        {parameter.Managed} floating = PG_GETARG_{parameter.Reader}({argument});");
                source.AppendLine($"{inputIndent}        {bits} bits;");
                source.AppendLine(inputIndent + "        memcpy(&bits, &floating, sizeof(bits));");
                source.AppendLine($"{inputIndent}        arguments[{argument}].integral = bits;");
            }
            else
            {
                string field = parameter.Field.ToLowerInvariant();
                string cast = parameter.Managed == "sbyte" ? "(int8) " : string.Empty;
                string reader = parameter.Reader == "TRANSACTIONID"
                    ? $"DatumGetTransactionId(PG_GETARG_DATUM({argument}))"
                    : $"PG_GETARG_{parameter.Reader}({argument})";
                source.AppendLine($"{inputIndent}        arguments[{argument}].{field} = {cast}{reader};");
            }

            source.AppendLine(inputIndent + "    }");
            source.AppendLine();
        }

        if (compositeArguments)
        {
            source.AppendLine("    }");
            source.AppendLine("    PG_FINALLY();");
            source.AppendLine("    {");
            source.AppendLine("        ankus_function_oid = previous_function;");
            source.AppendLine("    }");
            source.AppendLine("    PG_END_TRY();");
        }

        source.AppendLine("    /* Managed code reports every exception as a status, so nothing unwinds through this frame. */");
        source.AppendLine("    previous_function = ankus_function_oid;");
        source.AppendLine("    ankus_function_oid = fcinfo->flinfo->fn_oid;");
        source.AppendLine($"    ANKUS_MANAGED_INVOKE(status, &error, {callback}(arguments, &result, &error, ankus_spi_execute, &memory, fcinfo));");
        source.AppendLine("    ankus_function_oid = previous_function;");
        if (hasBuffers)
        {
            source.AppendLine($"    for (int index = 0; index < {count}; index++)");
            source.AppendLine("    {");
            source.AppendLine("        ankus_free_input(&owned[index]);");
            source.AppendLine("    }");
            source.AppendLine();
        }

        source.AppendLine("    if (status != 0)");
        source.AppendLine("    {");
        source.AppendLine("        if (result.release != NULL)");
        source.AppendLine("        {");
        source.AppendLine("            result.release(result.data);");
        source.AppendLine("        }");
        source.AppendLine();
        source.AppendLine("        ankus_raise_error(&error);");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    if (result.is_null" + (result.IsRaw || result.IsMapped ? " && result.data == NULL" : string.Empty) + ")");
        source.AppendLine("    {");
        if (result.IsComposite || result.UsesRawTransport)
        {
            source.AppendLine("        AnkusParameter parameter = {0};");
            source.AppendLine("        parameter.type_oid = get_func_rettype(fcinfo->flinfo->fn_oid);");
            if (result.UsesRawTransport)
            {
                source.AppendLine("        if (IsPolymorphicType(parameter.type_oid))");
                source.AppendLine("            parameter.type_oid = get_fn_expr_rettype(fcinfo->flinfo);");
            }

            source.AppendLine("        parameter.value.is_null = true;");
            source.AppendLine("        (void) ankus_parameter_datum(&parameter);");
        }

        source.AppendLine("        PG_RETURN_NULL();");
        source.AppendLine("    }");
        source.AppendLine();
        source.AppendLine("    previous_function = ankus_function_oid;");
        source.AppendLine("    ankus_function_oid = fcinfo->flinfo->fn_oid;");
        source.AppendLine("    PG_TRY();");
        source.AppendLine("    {");
        if (result.IsInternal)
        {
            source.AppendLine("        AnkusParameter parameter = {0};");
            source.AppendLine("        parameter.type_oid = INTERNALOID;");
            source.AppendLine("        parameter.value = result;");
            source.AppendLine("        datum = ankus_parameter_datum(&parameter);");
        }
        else if (result.UsesRawTransport)
        {
            source.AppendLine("        AnkusParameter parameter = {0};");
            source.AppendLine("        parameter.type_oid = get_func_rettype(fcinfo->flinfo->fn_oid);");
            source.AppendLine("        if (IsPolymorphicType(parameter.type_oid))");
            source.AppendLine("            parameter.type_oid = get_fn_expr_rettype(fcinfo->flinfo);");
            source.AppendLine("        parameter.value = result;");
            source.AppendLine("        datum = ankus_parameter_datum(&parameter);");
        }
        else if (result.Element is not null)
        {
            source.AppendLine($"        datum = ankus_write_array(&result, {(result.Element.Enumeration is null && result.Element.CustomType is null && !result.Element.IsComposite ? result.Element.ScalarOid : "get_element_type(get_func_rettype(fcinfo->flinfo->fn_oid))")});");
        }
        else if (result.RangeSubtype is not null)
        {
            source.AppendLine($"        datum = ankus_write_range(&result, {result.BufferOid});");
        }
        else if (result.Enumeration is not null)
        {
            source.AppendLine("        datum = ankus_write_enum(&result, get_func_rettype(fcinfo->flinfo->fn_oid));");
        }
        else if (result.IsComposite)
        {
            source.AppendLine("        Oid result_type = get_fn_expr_rettype(fcinfo->flinfo);");
            source.AppendLine("        TupleDesc descriptor = NULL;");
            source.AppendLine("        if (!OidIsValid(result_type))");
            source.AppendLine("            result_type = get_func_rettype(fcinfo->flinfo->fn_oid);");
            source.AppendLine("        /* A named result type describes the value itself; only a record needs the caller's columns. */");
            source.AppendLine("        if (result_type == RECORDOID)");
            source.AppendLine("            (void) get_call_result_type(fcinfo, NULL, &descriptor);");
            source.AppendLine("        datum = ankus_write_tuple(&result, result_type, descriptor);");
        }
        else if (result.CustomType is not null)
        {
            source.AppendLine("        datum = ankus_write_custom(&result, get_func_rettype(fcinfo->flinfo->fn_oid));");
        }
        else if (result.Reader == "cstring")
        {
            source.AppendLine("        datum = ankus_write_cstring(&result);");
        }
        else if (result.Reader == "tid")
        {
            source.AppendLine("        datum = ankus_write_item_pointer(&result);");
        }
        else if (result.IsTemporal)
        {
            source.AppendLine($"        datum = ankus_write_temporal(&result, {result.BufferOid});");
        }
        else if (result.IsBuffer)
        {
            source.AppendLine($"        datum = ankus_write_typed_buffer(&result, {result.BufferOid});");
        }
        else if (result.Managed is "float" or "double")
        {
            string bits = result.Managed == "float" ? "int32" : "int64";
            source.AppendLine($"        {bits} bits = ({bits}) result.integral;");
            source.AppendLine($"        {result.Managed} floating;");
            source.AppendLine("        memcpy(&floating, &bits, sizeof(bits));");
            source.AppendLine($"        datum = {result.Writer}GetDatum(floating);");
        }
        else if (result.Managed != "void")
        {
            source.AppendLine($"        datum = {result.Writer}GetDatum(result.{result.Field.ToLowerInvariant()});");
        }

        source.AppendLine("    }");
        source.AppendLine("    PG_FINALLY();");
        source.AppendLine("    {");
        source.AppendLine("        ankus_function_oid = previous_function;");
        source.AppendLine("        if (result.release != NULL)");
        source.AppendLine("        {");
        source.AppendLine("            result.release(result.data);");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine("    PG_END_TRY();");
        if (result.IsRaw || result.IsMapped)
        {
            source.AppendLine("    if (result.is_null)");
            source.AppendLine("        PG_RETURN_NULL();");
        }

        source.AppendLine("    return datum;");
        source.AppendLine("}");
        source.AppendLine();
        return new(header.ToString(), source.ToString());
    }
}
