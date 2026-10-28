using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Emits lazily selected static callbacks with a managed exception boundary and exact native value transport.
/// </summary>
internal static class PgNativeCallbackEmitter
{
    /// <summary>
    /// Implements partial callback properties without exporting or rooting unused managed dispatchers.
    /// </summary>
    /// <param name="declaration">The validated lexical and exact transport contract.</param>
    /// <returns>Complete partial declarations and imported native registration accessors.</returns>
    internal static string Emit(NativeCallbackModel declaration)
    {
        var source = new StringBuilder();
        bool namespaced = declaration.Namespace.Length != 0;
        if (namespaced)
        {
            source.AppendLine("namespace " + declaration.Namespace);
            source.AppendLine("{");
        }

        foreach (string container in declaration.Containers)
        {
            source.AppendLine(container);
            source.AppendLine("{");
        }

        string identity = declaration.Identity;
        string dispatcher = "AnkusDispatch_" + identity;
        string accessor = "AnkusCallback_" + identity;
        string managedType = declaration.ManagedType;
        source.AppendLine($$"""
            {{declaration.Accessibility}} {{(declaration.Hides ? "new " : string.Empty)}}static partial {{managedType}} @{{declaration.Name}}
            {
                get
                {
                    global::Ankus.CompilerServices.NativeRawCallback.ValidateBinding<{{managedType}}>();
                    nint address = {{accessor}}(unchecked((nint)(delegate* unmanaged[Cdecl]<global::Ankus.CompilerServices.NativeCallArgument*, nuint, nint, nuint, global::Ankus.CompilerServices.NativeCallbackContext*, int>)&{{dispatcher}}.Invoke));
                    if (address == 0)
                    {
                        throw new global::System.InvalidOperationException("The native callback registration conflicts with an existing handler.");
                    }

                    return new {{managedType}}(unchecked((void*)address));
                }
            }

            [global::System.Runtime.InteropServices.DllImport("Ankus.NativeBodies", EntryPoint = "ankus_native_callback_{{declaration.NativeSignature.ToString(CultureInfo.InvariantCulture)}}_{{identity}}",
                ExactSpelling = true, CallingConvention = global::System.Runtime.InteropServices.CallingConvention.Cdecl)]
            private static extern nint {{accessor}}(nint target);

            private static class {{dispatcher}}
            {
            [global::System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(global::System.Runtime.CompilerServices.CallConvCdecl)])]
            internal static int Invoke(global::Ankus.CompilerServices.NativeCallArgument* arguments, nuint count,
                nint result, nuint resultSize, global::Ankus.CompilerServices.NativeCallbackContext* context)
            {
                if (context is null || context->Error == 0)
                {
                    return 1;
                }

                nint previousBackend = global::Ankus.CompilerServices.NativeBackend.Enter(context->Execute);
                nint previousRead = global::Ankus.CompilerServices.NativeGuc.Enter(context->Read);
                nint previousLog = global::Ankus.CompilerServices.NativeLog.Enter(context->Log);
                nint previousMemory = 0;
                bool memoryEntered = false;
                try
                {
                    previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(context->Memory);
                    memoryEntered = true;
                    global::Ankus.CompilerServices.NativeRawCallback.ValidateBinding<{{managedType}}>();
                    global::Ankus.CompilerServices.NativeRawCallback.ValidateFrame(arguments, count, {{declaration.Arguments.Count.ToString(CultureInfo.InvariantCulture)}},
                        result, resultSize, {{declaration.Result?.Size ?? "-1"}});
            """);
        var arguments = new List<string>();
        for (int index = 0; index < declaration.Arguments.Count; index++)
        {
            NativeCallbackModel.ValueContract type = declaration.Arguments[index];
            string number = index.ToString(CultureInfo.InvariantCulture);
            string argument = "argument" + number;
            string reader = type.Native ? "ReadNative" : "Read";
            string read = $"global::Ankus.CompilerServices.NativeRawCallback.{reader}<{type.Storage}>(arguments[{number}])";
            if (type.Pointer)
            {
                read = $"unchecked(({type.Name}){read})";
            }

            source.AppendLine($"        {type.Name} {argument} = {read};");
            arguments.Add(argument);
        }

        string invocation = declaration.Handler + "(" + string.Join(", ", arguments) + ")";
        if (declaration.Result is null)
        {
            source.AppendLine("        " + invocation + ";");
        }
        else
        {
            NativeCallbackModel.ValueContract resultType = declaration.Result;
            string writer = resultType.Native ? "WriteNative" : "Write";
            source.AppendLine($"        {resultType.Name} value = {invocation};");
            string value = resultType.Pointer ? "unchecked((nint)value)" : "value";
            source.AppendLine($"        global::Ankus.CompilerServices.NativeRawCallback.{writer}(result, resultSize, {value});");
        }

        source.AppendLine("""
                    return 0;
                }
                catch (global::System.Exception exception)
                {
                    global::Ankus.CompilerServices.NativeError.Write(exception, (global::Ankus.CompilerServices.NativeCallError*)context->Error);
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
            }

            """);
        foreach (string _ in declaration.Containers)
        {
            source.AppendLine("}");
        }

        if (namespaced)
        {
            source.AppendLine("}");
        }

        source.AppendLine();
        return source.ToString();
    }

    /// <summary>
    /// Formats explicit partial declaration accessibility during transient semantic conversion.
    /// </summary>
    internal static string Access(Accessibility value) => value switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => throw new InvalidOperationException("A callback declaration requires explicit member accessibility."),
    };
}
