using System.Collections.ObjectModel;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Finds the header helpers whose C bodies cannot raise a PostgreSQL error, so their calls need no error guard.
/// </summary>
/// <remarks>
/// A helper qualifies when it is an inline function or one of Ankus's macro wrappers, and everything its body runs is
/// visible in the headers: calls only reach other qualifying helpers, the C library's memory and string comparisons,
/// compiler builtins or <c>ExceptionalCondition</c>, which aborts the process instead of raising an ERROR. A call
/// through a function pointer, inline assembly or integer division or remainder by anything but a constant other than
/// zero and minus one disqualifies it, since PostgreSQL turns a division fault into an ERROR from its signal handler. A
/// helper that calls itself, directly or through others, is treated as able to raise. The bodies are read with libclang
/// from a serialized AST, since a JSON dump of them can exceed half a gigabyte with the Windows SDK headers.
/// </remarks>
internal sealed class NativeBindingRaiseAnalysis
{
    private static readonly HashSet<string> s_library =
        new(["memcpy", "memmove", "memset", "memcmp", "memchr", "strlen", "strnlen", "strcmp", "strncmp", "ExceptionalCondition"],
            StringComparer.Ordinal);

    private readonly NativeClang _clang;
    private readonly Dictionary<string, NativeClangCursor> _definitions;
    private readonly Dictionary<string, bool> _results = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

    /// <summary>
    /// Indexes the translation unit's helper definitions.
    /// </summary>
    /// <param name="clang">The libclang module that loaded the unit.</param>
    /// <param name="unit">A translation unit compiled with function bodies.</param>
    private unsafe NativeBindingRaiseAnalysis(NativeClang clang, NativeClangUnit unit)
    {
        _clang = clang;
        NativeClangCursor root = ((delegate* unmanaged[Cdecl]<nint, NativeClangCursor>)clang.Export("clang_getTranslationUnitCursor"))(unit.DangerousGetHandle());
        // CXCursor_FunctionDecl definitions of inline functions and Ankus's macro wrappers.
        _definitions = clang.Children(root, limit: 10_000_000)
            .Where(cursor => cursor.Kind == 8 && clang.IsDefinition(cursor))
            .Select(cursor => (Name: clang.Name(cursor), Cursor: cursor))
            .Where(function => clang.IsInlined(function.Cursor) ||
                function.Name.StartsWith(NativeBindingHeaderHelpers.WrapperPrefix, StringComparison.Ordinal))
            .ToDictionary(static function => function.Name, static function => function.Cursor, StringComparer.Ordinal);
    }

    /// <summary>
    /// Marks the selected functions whose bodies, compiled with their headers, cannot raise an error.
    /// </summary>
    /// <param name="symbols">The selected headers' declarations.</param>
    /// <param name="installation">The installation whose headers are compiled.</param>
    /// <param name="frontend">The header command's frontend arguments.</param>
    /// <param name="source">The translation unit whose declarations were parsed without bodies.</param>
    /// <param name="major">The selected PostgreSQL major, whose macro wrappers are defined for the analysis.</param>
    /// <param name="library">The libclang module matching the frontend.</param>
    /// <param name="directory">A scratch directory for the compiler's output.</param>
    /// <param name="cancellationToken">Cancels compilation.</param>
    /// <returns>
    /// The declarations with each proven function marked, or unchanged when Clang cannot compile or read the bodies,
    /// since declaration collection never needs them and a guarded call is always correct.
    /// </returns>
    internal static async Task<IReadOnlyDictionary<string, NativeHeaderSymbol>> ApplyAsync(IReadOnlyDictionary<string, NativeHeaderSymbol> symbols,
        PostgresInstallation installation, string[] frontend, string source, int major, string library, string directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        string[] functions = [.. symbols.Values.Where(static symbol => symbol.IsFunction).Select(static symbol => symbol.NativeName)];
        // The wrappers' bodies expand PostgreSQL's macros, so they are read as a system header, as PostgreSQL's own headers
        // are. Their diagnostics belong to the compile that builds them, such as MSVC's for a deprecated C library call.
        string include = Path.Join(directory, "native-bodies");
        Directory.CreateDirectory(include);
        await File.WriteAllTextAsync(Path.Join(include, "native-bodies.h"), NativeBindingHeaderHelpers.WrapperDefinitions(major, functions) + source,
            cancellationToken);
        string file = Path.Join(directory, "native-bodies.c");
        string ast = Path.Join(directory, "native-bodies.ast");
        await File.WriteAllTextAsync(file, "#include <native-bodies.h>\n", cancellationToken);
        IReadOnlySet<string> safe;
        try
        {
            await NativeBindingHeaderCommand.InspectAsync(installation, frontend, file, Path.Join(directory, "native-bodies.txt"), directory,
                cancellationToken, dumpAst: false, serializedAst: ast, inspectBodies: true, systemIncludes: [include]);
            safe = await InspectAsync(ast, library, functions, directory, cancellationToken);
        }
        catch (InvalidOperationException error)
        {
            Console.WriteLine("Ankus: every native call keeps its error guard, because Clang could not read the header function bodies: " +
                error.Message.ReplaceLineEndings("\n").Split('\n')[0]);
            return symbols;
        }
        finally
        {
            File.Delete(ast);
        }

        var marked = new SortedDictionary<string, NativeHeaderSymbol>(StringComparer.Ordinal);
        foreach ((string name, NativeHeaderSymbol symbol) in symbols)
        {
            marked.Add(name, symbol.IsFunction && safe.Contains(symbol.NativeName) ? symbol with { CannotRaise = true } : symbol);
        }

        return new ReadOnlyDictionary<string, NativeHeaderSymbol>(marked);
    }

    /// <summary>
    /// Finds which of the given functions cannot raise an error, reading a serialized AST in a worker process.
    /// </summary>
    /// <param name="ast">A serialized AST compiled with function bodies.</param>
    /// <param name="library">The libclang module matching the compiler that wrote it.</param>
    /// <param name="functions">The C names of the functions to check.</param>
    /// <param name="directory">A scratch directory for the worker's request and output.</param>
    /// <param name="cancellationToken">Stops the worker.</param>
    /// <returns>The functions whose calls cannot raise.</returns>
    internal static async Task<IReadOnlySet<string>> InspectAsync(string ast, string library, IReadOnlyList<string> functions, string directory,
        CancellationToken cancellationToken)
    {
        string input = Path.Join(directory, "native-raise-request.json");
        string output = Path.Join(directory, "native-raise-observations.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(new NativeRaiseRequest(ast, library, functions), NativeBindingRecordWorker.JsonOptions),
            cancellationToken);
        await NativeBindingRecordWorker.RunProcessAsync("binding-raise-worker", "Native raise analysis worker", input, output, directory, cancellationToken);
        await using FileStream result = File.OpenRead(output);
        string[] found = await JsonSerializer.DeserializeAsync<string[]>(result, NativeBindingRecordWorker.JsonOptions, cancellationToken)
            ?? throw new FormatException("Missing native raise analysis result.");
        return found.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Runs once in a dedicated process and writes the functions that cannot raise as a JSON array.
    /// </summary>
    /// <param name="arguments">The request file.</param>
    internal static async Task RunWorkerAsync(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("Expected binding-raise-worker <request-json>.", nameof(arguments));
        }

        var input = new FileInfo(arguments[0]);
        if (input.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Native raise analysis request exceeds the byte limit.");
        }

        NativeRaiseRequest request;
        await using (FileStream stream = input.OpenRead())
        {
            request = await JsonSerializer.DeserializeAsync<NativeRaiseRequest>(stream, NativeBindingRecordWorker.JsonOptions)
                ?? throw new FormatException("Empty native raise analysis request.");
        }

        if (string.IsNullOrEmpty(request.Ast) || string.IsNullOrEmpty(request.Library) || !Path.IsPathFullyQualified(request.Ast) ||
            !Path.IsPathFullyQualified(request.Library) || request.Functions is null || request.Functions.Count > 100_000)
        {
            throw new FormatException("Invalid native raise analysis request.");
        }

        foreach (string function in request.Functions)
        {
            NativeBindingCDeclaration.ValidateName(function);
        }

        using var library = new NativeClang(request.Library);
        using NativeClangUnit unit = library.Load(request.Ast);
        var analysis = new NativeBindingRaiseAnalysis(library, unit);
        string[] found = [.. request.Functions.Where(analysis.CannotRaise).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        await using Stream output = Console.OpenStandardOutput();
        await JsonSerializer.SerializeAsync(output, found, NativeBindingRecordWorker.JsonOptions);
    }

    /// <summary>
    /// Determines whether calling a function can raise a PostgreSQL error.
    /// </summary>
    /// <param name="name">The function's C name.</param>
    /// <returns>Whether the headers prove that the call cannot raise.</returns>
    private bool CannotRaise(string name)
    {
        if (_results.TryGetValue(name, out bool known))
        {
            return known;
        }

        if (!_definitions.TryGetValue(name, out NativeClangCursor definition) || !_visiting.Add(name))
        {
            return false;
        }

        bool result = Safe(definition);
        _visiting.Remove(name);
        _results[name] = result;
        return result;
    }

    private bool Safe(NativeClangCursor definition)
    {
        foreach (NativeClangCursor cursor in _clang.Children(definition, recursive: true))
        {
            switch (cursor.Kind)
            {
                // CXCursor_GCCAsmStmt and CXCursor_MSAsmStmt.
                case 215 or 229:
                    return false;

                // CXCursor_CallExpr naming a CXCursor_FunctionDecl.
                case 103:
                    NativeClangCursor callee = _clang.Referenced(cursor);
                    if (callee.Kind != 8 || _clang.Name(callee) is not string function ||
                        !(s_library.Contains(function) || function.StartsWith("__builtin_", StringComparison.Ordinal) || CannotRaise(function)))
                    {
                        return false;
                    }

                    break;

                // CXCursor_BinaryOperator and CXCursor_CompoundAssignOperator, as Div, Rem, DivAssign or RemAssign.
                case 114 or 115 when _clang.BinaryOperator(cursor) is 4 or 5 or 24 or 25:
                    if (!Floating(cursor) && !ConstantDivisor(cursor))
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    private bool Floating(NativeClangCursor operation)
        => _clang.Transform(_clang.Type(operation), "clang_getCanonicalType").Kind is 21 or 22 or 23 or 30 or 31 or 32 or 39 or 40;

    private bool ConstantDivisor(NativeClangCursor operation)
        => _clang.Children(operation) is [_, NativeClangCursor divisor] &&
            _clang.TryEvaluateInteger(divisor, out long value, out bool unsigned) && value != 0 && (unsigned || value != -1);
}

/// <summary>
/// Carries a serialized AST, its libclang module and the functions to check into the raise analysis worker.
/// </summary>
/// <param name="Ast">The serialized AST, compiled with function bodies.</param>
/// <param name="Library">The libclang module matching its compiler.</param>
/// <param name="Functions">The C names of the functions to check.</param>
internal sealed record NativeRaiseRequest(string Ast, string Library, IReadOnlyList<string> Functions);
