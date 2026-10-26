using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ankus.Build;

/// <summary>
/// Uses one explicitly selected libclang inside an isolated worker, with matched native ownership.
/// </summary>
/// <remarks>
/// Windows LLVM 20/21 release libraries retain an rpmalloc thread-exit callback after unloading
/// (LLVM issue 154361). The worker therefore keeps that module loaded until process exit, while
/// disposing every translation unit, index, diagnostic, string and printing policy normally.
/// </remarks>
/// <param name="path">The absolute library path selected for this worker process.</param>
internal sealed unsafe class NativeClang(string path) : SafeHandle(NativeLibrary.Load(path), ownsHandle: !OperatingSystem.IsWindows())
{
    private readonly Dictionary<string, nint> _exports = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override bool IsInvalid => handle == 0;

    /// <summary>
    /// Resolves a stable clang-c entry point from this exact library.
    /// </summary>
    internal nint Export(string name)
    {
        if (!_exports.TryGetValue(name, out nint address))
        {
            address = NativeLibrary.GetExport(handle, name);
            _exports.Add(name, address);
        }

        return address;
    }

    /// <summary>
    /// Loads the selected compiler's serialized AST without reconstructing its driver mode or preprocessor options.
    /// </summary>
    internal NativeClangUnit Load(string ast)
    {
        if (new FileInfo(ast).Length > 512 * 1024 * 1024)
        {
            throw new InvalidDataException("Native serialized AST exceeds the byte limit.");
        }

        bool retained = false;
        DangerousAddRef(ref retained);
        nint index = 0;
        nint result = 0;
        nint filename = 0;
        try
        {
            // Resolve destructors before acquiring their corresponding resources.
            _ = Export("clang_disposeTranslationUnit");
            _ = Export("clang_disposeIndex");
            index = ((delegate* unmanaged[Cdecl]<int, int, nint>)Export("clang_createIndex"))(0, 0);
            if (index == 0)
            {
                throw new InvalidOperationException("Cannot create the selected Clang index.");
            }

            filename = Marshal.StringToCoTaskMemUTF8(ast);
            int error = ((delegate* unmanaged[Cdecl]<nint, nint, nint*, int>)Export("clang_createTranslationUnit2"))(index, filename, &result);
            if (error != 0 || result == 0)
            {
                throw new InvalidOperationException($"Clang could not load the selected compiler's AST (error {error}). Use a matching libclang library.");
            }

            var unit = new NativeClangUnit(result, index, this);
            index = 0;
            result = 0;
            retained = false;
            try
            {
                ValidateDiagnostics(unit);
                return unit;
            }
            catch
            {
                unit.Dispose();
                throw;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(filename);
            if (result != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)Export("clang_disposeTranslationUnit"))(result);
            }

            if (index != 0)
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)Export("clang_disposeIndex"))(index);
            }

            if (retained)
            {
                DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Copies a native string before releasing its storage through the same library.
    /// </summary>
    internal string Text(NativeClangString value)
    {
        try
        {
            return Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<NativeClangString, nint>)Export("clang_getCString"))(value)) ?? "";
        }
        finally
        {
            ((delegate* unmanaged[Cdecl]<NativeClangString, void>)Export("clang_disposeString"))(value);
        }
    }

    /// <summary>
    /// Reads a cursor name without retaining its native string storage.
    /// </summary>
    internal string Name(NativeClangCursor value) => Text(((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangString>)Export("clang_getCursorSpelling"))(value));

    /// <summary>
    /// Reads the type of a native declaration or expression.
    /// </summary>
    internal NativeClangType Type(NativeClangCursor value) => ((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangType>)Export("clang_getCursorType"))(value);

    /// <summary>
    /// Applies a stable clang-c type projection while the translation unit is alive.
    /// </summary>
    internal NativeClangType Transform(NativeClangType value, string function) => ((delegate* unmanaged[Cdecl]<NativeClangType, NativeClangType>)Export(function))(value);

    /// <summary>
    /// Retrieves the declaration associated with a tagged or typedef type.
    /// </summary>
    internal NativeClangCursor Declaration(NativeClangType value) => ((delegate* unmanaged[Cdecl]<NativeClangType, NativeClangCursor>)Export("clang_getTypeDeclaration"))(value);

    /// <summary>
    /// Reads a signed native long-long layout observation, including documented unavailable-layout codes.
    /// </summary>
    internal long Measure(NativeClangType value, string function) => ((delegate* unmanaged[Cdecl]<NativeClangType, long>)Export(function))(value);

    /// <summary>
    /// Copies the compiler's type spelling for builtin names and diagnostics.
    /// </summary>
    internal string Spelling(NativeClangType value) => Text(((delegate* unmanaged[Cdecl]<NativeClangType, NativeClangString>)Export("clang_getTypeSpelling"))(value));

    /// <summary>
    /// Finds the declaration an expression names, such as the function a call names directly.
    /// </summary>
    internal NativeClangCursor Referenced(NativeClangCursor value) => ((delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangCursor>)Export("clang_getCursorReferenced"))(value);

    /// <summary>
    /// Determines whether a declaration is a definition, such as a function with its body.
    /// </summary>
    internal bool IsDefinition(NativeClangCursor value) => ((delegate* unmanaged[Cdecl]<NativeClangCursor, uint>)Export("clang_isCursorDefinition"))(value) != 0;

    /// <summary>
    /// Determines whether a function is declared inline.
    /// </summary>
    internal bool IsInlined(NativeClangCursor value) => ((delegate* unmanaged[Cdecl]<NativeClangCursor, uint>)Export("clang_Cursor_isFunctionInlined"))(value) != 0;

    /// <summary>
    /// Reads a binary or compound assignment operator's kind, as clang-c's <c>CXBinaryOperatorKind</c> numbers it.
    /// </summary>
    internal int BinaryOperator(NativeClangCursor value) => ((delegate* unmanaged[Cdecl]<NativeClangCursor, int>)Export("clang_getCursorBinaryOperatorKind"))(value);

    /// <summary>
    /// Evaluates an expression the compiler can fold to an integer constant.
    /// </summary>
    /// <param name="value">The expression.</param>
    /// <param name="result">The constant, with an unsigned value's bits kept.</param>
    /// <param name="unsigned">Whether the constant is unsigned.</param>
    /// <returns>Whether the expression is an integer constant.</returns>
    internal bool TryEvaluateInteger(NativeClangCursor value, out long result, out bool unsigned)
    {
        result = 0;
        unsigned = false;
        _ = Export("clang_EvalResult_dispose");
        nint evaluation = ((delegate* unmanaged[Cdecl]<NativeClangCursor, nint>)Export("clang_Cursor_Evaluate"))(value);
        if (evaluation == 0)
        {
            return false;
        }

        try
        {
            // CXEval_Int.
            if (((delegate* unmanaged[Cdecl]<nint, int>)Export("clang_EvalResult_getKind"))(evaluation) != 1)
            {
                return false;
            }

            unsigned = ((delegate* unmanaged[Cdecl]<nint, uint>)Export("clang_EvalResult_isUnsignedInt"))(evaluation) != 0;
            result = unsigned ? unchecked((long)((delegate* unmanaged[Cdecl]<nint, ulong>)Export("clang_EvalResult_getAsUnsigned"))(evaluation))
                : ((delegate* unmanaged[Cdecl]<nint, long>)Export("clang_EvalResult_getAsLongLong"))(evaluation);
            return true;
        }
        finally
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)Export("clang_EvalResult_dispose"))(evaluation);
        }
    }

    /// <summary>
    /// Prints a declaration with source-location-dependent anonymous names disabled.
    /// </summary>
    internal string Print(NativeClangCursor value) => PrintCore(value, null);

    /// <summary>
    /// Prints a complete type, including native annotations, without anonymous source locations.
    /// </summary>
    internal string PrintType(NativeClangType type, NativeClangCursor policyOwner) => PrintCore(policyOwner, type);

    private string PrintCore(NativeClangCursor value, NativeClangType? type)
    {
        _ = Export("clang_PrintingPolicy_dispose");
        nint policy = ((delegate* unmanaged[Cdecl]<NativeClangCursor, nint>)Export("clang_getCursorPrintingPolicy"))(value);
        if (policy == 0)
        {
            throw new InvalidOperationException("Missing Clang declaration printing policy.");
        }

        try
        {
            // SuppressInitializers and AnonymousTagLocations respectively.
            ((delegate* unmanaged[Cdecl]<nint, int, uint, void>)Export("clang_PrintingPolicy_setProperty"))(policy, 6, 1);
            ((delegate* unmanaged[Cdecl]<nint, int, uint, void>)Export("clang_PrintingPolicy_setProperty"))(policy, 8, 0);
            return type is NativeClangType observed
                ? Text(((delegate* unmanaged[Cdecl]<NativeClangType, nint, NativeClangString>)Export("clang_getTypePrettyPrinted"))(observed, policy))
                : Text(((delegate* unmanaged[Cdecl]<NativeClangCursor, nint, NativeClangString>)Export("clang_getCursorPrettyPrinted"))(value, policy));
        }
        finally
        {
            ((delegate* unmanaged[Cdecl]<nint, void>)Export("clang_PrintingPolicy_dispose"))(policy);
        }
    }

    /// <summary>
    /// Copies child cursors, containing callback failures until traversal returns to managed code.
    /// </summary>
    internal List<NativeClangCursor> Children(NativeClangCursor cursor, bool recursive = false, int limit = 100_000)
    {
        var visit = new CursorVisit(recursive, limit);
        GCHandle state = GCHandle.Alloc(visit);
        try
        {
            ((delegate* unmanaged[Cdecl]<NativeClangCursor, delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangCursor, nint, uint>, nint, uint>)Export("clang_visitChildren"))(
                cursor, &VisitChild, GCHandle.ToIntPtr(state));
            visit.Error?.Throw();
            return visit.Cursors;
        }
        finally
        {
            state.Free();
        }
    }

    /// <summary>
    /// Visits every declaration under a cursor once. A serialized AST loads a declaration context's members lazily, and
    /// measuring a record first loads only its fields, so later visits would list its nested declarations before every
    /// field; visiting first keeps source order, as parsing the headers directly does.
    /// </summary>
    internal void LoadDeclarations(NativeClangCursor cursor)
    {
        var visit = new CursorVisit(recursive: true, limit: 0);
        GCHandle state = GCHandle.Alloc(visit);
        try
        {
            ((delegate* unmanaged[Cdecl]<NativeClangCursor, delegate* unmanaged[Cdecl]<NativeClangCursor, NativeClangCursor, nint, uint>, nint, uint>)Export("clang_visitChildren"))(
                cursor, &VisitDeclaration, GCHandle.ToIntPtr(state));
            visit.Error?.Throw();
        }
        finally
        {
            state.Free();
        }
    }

    /// <summary>
    /// Copies physical field declarations, including implicit anonymous containers and unnamed bitfields.
    /// </summary>
    internal List<NativeClangCursor> Fields(NativeClangType type)
    {
        var visit = new CursorVisit(recursive: false, limit: 100_000);
        GCHandle state = GCHandle.Alloc(visit);
        try
        {
            ((delegate* unmanaged[Cdecl]<NativeClangType, delegate* unmanaged[Cdecl]<NativeClangCursor, nint, uint>, nint, uint>)Export("clang_Type_visitFields"))(
                type, &VisitField, GCHandle.ToIntPtr(state));
            visit.Error?.Throw();
            return visit.Cursors;
        }
        finally
        {
            state.Free();
        }
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        NativeLibrary.Free(handle);
        return true;
    }

    private void ValidateDiagnostics(NativeClangUnit unit)
    {
        _ = Export("clang_disposeDiagnostic");
        uint count = ((delegate* unmanaged[Cdecl]<nint, uint>)Export("clang_getNumDiagnostics"))(unit.DangerousGetHandle());
        var errors = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            nint diagnostic = ((delegate* unmanaged[Cdecl]<nint, uint, nint>)Export("clang_getDiagnostic"))(unit.DangerousGetHandle(), i);
            try
            {
                uint severity = ((delegate* unmanaged[Cdecl]<nint, uint>)Export("clang_getDiagnosticSeverity"))(diagnostic);
                if (severity >= 2)
                {
                    errors.Add(Text(((delegate* unmanaged[Cdecl]<nint, uint, NativeClangString>)Export("clang_formatDiagnostic"))(diagnostic, 0)));
                }
            }
            finally
            {
                ((delegate* unmanaged[Cdecl]<nint, void>)Export("clang_disposeDiagnostic"))(diagnostic);
            }
        }

        if (errors.Count != 0)
        {
            throw new InvalidOperationException("Native record inspection failed: " + string.Join(Environment.NewLine, errors));
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static uint VisitChild(NativeClangCursor cursor, NativeClangCursor parent, nint state)
    {
        var visit = (CursorVisit)GCHandle.FromIntPtr(state).Target!;
        try
        {
            visit.Add(cursor);
            return visit.Recursive ? 2U : 1U;
        }
        // codeql[cs/catch-of-all-exceptions] libclang calls this visitor, so every exception is carried back to the caller instead of unwinding through libclang.
        catch (Exception error)
        {
            visit.Error = ExceptionDispatchInfo.Capture(error);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static uint VisitDeclaration(NativeClangCursor cursor, NativeClangCursor parent, nint state)
    {
        var visit = (CursorVisit)GCHandle.FromIntPtr(state).Target!;
        try
        {
            visit.Count();

            // Declarations are CXCursor_FirstDecl through CXCursor_LastDecl; their members are the only lazy content.
            return cursor.Kind is >= 1 and <= 39 ? 2U : 1U;
        }
        // codeql[cs/catch-of-all-exceptions] libclang calls this visitor, so every exception is carried back to the caller instead of unwinding through libclang.
        catch (Exception error)
        {
            visit.Error = ExceptionDispatchInfo.Capture(error);
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static uint VisitField(NativeClangCursor cursor, nint state)
    {
        var visit = (CursorVisit)GCHandle.FromIntPtr(state).Target!;
        try
        {
            visit.Add(cursor);
            return 1;
        }
        // codeql[cs/catch-of-all-exceptions] libclang calls this visitor, so every exception is carried back to the caller instead of unwinding through libclang.
        catch (Exception error)
        {
            visit.Error = ExceptionDispatchInfo.Capture(error);
            return 0;
        }
    }

    /// <summary>
    /// Bounds one traversal and carries any callback exception across the native return boundary.
    /// </summary>
    /// <param name="recursive">Whether child traversal includes descendants.</param>
    /// <param name="limit">The most cursors one traversal may copy.</param>
    private sealed class CursorVisit(bool recursive, int limit)
    {
        private long _visited;

        /// <summary>
        /// Whether the caller requested descendant traversal.
        /// </summary>
        internal bool Recursive { get; } = recursive;

        /// <summary>
        /// The ordered cursors copied while the translation unit remains live.
        /// </summary>
        internal List<NativeClangCursor> Cursors { get; } = [];

        /// <summary>
        /// A managed failure to rethrow only after returning through the native frame.
        /// </summary>
        internal ExceptionDispatchInfo? Error
        {
            get;
            set;
        }

        /// <summary>
        /// Counts one visited cursor without retaining it, within a bound for whole translation units.
        /// </summary>
        internal void Count()
        {
            if (++_visited == 100_000_000)
            {
                throw new InvalidDataException("Native declaration traversal exceeds the supported limit.");
            }
        }

        /// <summary>
        /// Adds one observation within the finite traversal bound.
        /// </summary>
        internal void Add(NativeClangCursor cursor)
        {
            if (Cursors.Count == limit)
            {
                throw new InvalidDataException("Native cursor traversal exceeds the supported declaration limit.");
            }

            Cursors.Add(cursor);
        }
    }
}
