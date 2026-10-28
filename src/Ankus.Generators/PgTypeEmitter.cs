using System.Collections.Generic;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Emits base-type SQL and managed I/O callbacks through the ordinary scalar error boundary.
/// </summary>
internal static class PgTypeEmitter
{
    /// <summary>
    /// Emits the shell, I/O functions, and completed variable-length base type in dependency order.
    /// </summary>
    internal static Emission Create(CustomTypeIoModel type)
    {
        FunctionType custom = FunctionType.CustomIo(type);
        var managed = new StringBuilder();
        var exports = new StringBuilder();
        var native = new List<NativeFunctionEmission>();
        FunctionType text = FunctionType.CreateIoBuffer("cstring", "cstring");
        string prefix = type.Schema is null ? "\0" : string.Empty;
        var sql = new StringBuilder("CREATE TYPE " + prefix + type.Sql + ";\n");
        EmitFunction("in", "Input", FunctionType.CreateIoBuffer("cstring", "cstring", type.NullInputErrorMessage is not null), custom);
        EmitFunction("out", "Output", custom, text);
        if (type.BinaryProtocol)
        {
            EmitFunction("recv", "Binary", FunctionType.CreateIoBuffer("internal", "type_receive"), custom);
            EmitFunction("send", "Binary", custom, FunctionType.CreateIoBuffer("bytea", "bytea"));
        }

        sql.Append("CREATE TYPE " + prefix + type.Sql + " (").Append('\n');
        sql.Append("    INTERNALLENGTH = variable, INPUT = " + prefix + type.Function("in") + ", OUTPUT = " + prefix + type.Function("out") + ",").Append('\n');
        if (type.BinaryProtocol)
        {
            sql.Append("    RECEIVE = " + prefix + type.Function("recv") + ", SEND = " + prefix + type.Function("send") + ",").Append('\n');
        }

        sql.Append("    ALIGNMENT = " + type.Alignment + ", STORAGE = extended);").Append('\n');
        return new(sql.ToString(), managed.ToString(), new(native), exports.ToString());

        void EmitFunction(string role, string operation, FunctionType input, FunctionType result)
        {
            string callback = "ankus_managed_" + type.Symbol + "_type_" + role;
            string symbol = type.NativeFunction(role);
            EmitManaged(callback, operation, type, managed);
            native.Add(PgFunctionEmitter.CreateNative(symbol, callback, [input], result));
            sql.Append("CREATE FUNCTION " + prefix + type.Function(role) + "(" + SqlSchemaTemplate.Type(input) + ") RETURNS " + SqlSchemaTemplate.Type(result) +
                " AS 'MODULE_PATHNAME', '" + symbol + "' LANGUAGE c IMMUTABLE " +
                (role == "in" && type.NullInputErrorMessage is not null ? "CALLED ON NULL INPUT" : "STRICT") + " PARALLEL SAFE;").Append('\n');
            exports.AppendLine(symbol);
            exports.AppendLine("pg_finfo_" + symbol);
        }
    }

    /// <summary>
    /// Runs a closed codec operation with owned diagnostics and deterministic backend-scope restoration.
    /// </summary>
    private static void EmitManaged(string callback, string operation, CustomTypeIoModel type, StringBuilder source)
    {
        source.AppendLine("    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(");
        source.AppendLine("        EntryPoint = \"" + callback + "\",");
        source.AppendLine("        CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]");
        source.AppendLine("    private static int " + callback + "(global::Ankus.CompilerServices.NativeValue* arguments, global::Ankus.CompilerServices.NativeValue* result,");
        source.AppendLine("        global::Ankus.CompilerServices.NativeCallError* error, nint execute, nint memory, nint functionCall)");
        source.AppendLine("    {");
        source.AppendLine("        nint previous = global::Ankus.CompilerServices.NativeBackend.Enter(execute);");
        source.AppendLine("        nint previousMemory = 0;");
        source.AppendLine("        bool memoryEntered = false;");
        source.AppendLine("        try");
        source.AppendLine("        {");
        source.AppendLine("            previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);");
        source.AppendLine("            memoryEntered = true;");
        if (operation == "Input" && type.NullInputErrorMessage is { } nullMessage)
        {
            source.AppendLine("            if (arguments[0].IsNull != 0) throw new global::Ankus.PgException(\"22004\", " +
                Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(nullMessage, true) + ");");
        }

        source.AppendLine("            *result = global::Ankus.CompilerServices.PgTypeRegistry." + operation + "<" + type.Managed + ">(arguments[0]);");
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
        source.AppendLine();
        source.AppendLine("            global::Ankus.CompilerServices.NativeBackend.Exit(previous);");
        source.AppendLine("        }");
        source.AppendLine("    }");
        source.AppendLine();
    }

    /// <summary>
    /// Keeps type I/O source independent of extension-wide initialization and graph metadata.
    /// </summary>
    /// <param name="Sql">The shell, I/O declarations and completed type SQL.</param>
    /// <param name="Managed">The closed managed codec entry points.</param>
    /// <param name="Native">The native conversion and cleanup boundaries.</param>
    /// <param name="Exports">The exact exported entry and function-info symbols.</param>
    internal sealed record Emission(string Sql, string Managed, EquatableArray<NativeFunctionEmission> Native, string Exports);
}
