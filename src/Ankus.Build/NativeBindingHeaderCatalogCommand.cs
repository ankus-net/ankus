using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Derives a major's declaration catalog from its installed headers, and reports how it differs from the pinned catalog.
/// </summary>
internal static partial class NativeBindingHeaderCatalogCommand
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    /// <summary>
    /// Writes the catalog derived from the selected installation's headers and, with <c>--compare</c>, prints its
    /// differences from the catalog pinned from pgrx's generated bindings.
    /// </summary>
    /// <param name="arguments">The <c>pg_config</c> path, the output path and optionally <c>--compare</c>.</param>
    /// <param name="cancellationToken">Cancels the frontend.</param>
    /// <returns>The number of differences found, or zero when not comparing.</returns>
    internal static async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is not (2 or 3) || arguments.Length == 3 && arguments[2] != "--compare")
        {
            throw new ArgumentException("Expected binding-catalog-headers <pg_config> <output> [--compare].", nameof(arguments));
        }

        PostgresInstallation installation = await PostgresInstallation.CreateAsync(arguments[0], cancellationToken);
        string directory = Directory.CreateTempSubdirectory("ankus-header-catalog-").FullName;
        try
        {
            NativeHeaderCatalogs catalogs = await BuildAsync(installation, directory, cancellationToken);
            NativeBindingCatalog catalog = catalogs.Types;
            await File.WriteAllTextAsync(arguments[1], JsonSerializer.Serialize(catalogs, s_indented) + "\n", cancellationToken);
            if (arguments.Length == 2)
            {
                return 0;
            }

            List<string> differences = Compare(NativeBindingResources.ReadCatalog(catalog.PostgresMajor), catalog);
            differences.AddRange(CompareRaw(NativeBindingResources.ReadRawCatalog(catalog.PostgresMajor), catalogs.Raw));
            foreach (string difference in differences)
            {
                Console.WriteLine(difference);
            }

            Console.WriteLine($"PG{catalog.PostgresMajor}: {differences.Count} differences from the pinned catalog.");
            return differences.Count;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Builds a major's type catalog from its pinned header manifest, compiled against the selected installation as its
    /// extensions are and read through the compiler's own libclang.
    /// </summary>
    /// <param name="installation">The selected PostgreSQL installation.</param>
    /// <param name="directory">A scratch directory for the frontend's input and output.</param>
    /// <param name="cancellationToken">Cancels the frontend.</param>
    /// <returns>The type catalog and raw inventory.</returns>
    internal static async Task<NativeHeaderCatalogs> BuildAsync(PostgresInstallation installation, string directory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        List<string> options = await NativeBindingHeaderCommand.CreateArgumentsAsync(installation, [], cancellationToken);
        options.Add(OperatingSystem.IsWindows() ? "/Zs" : "-fsyntax-only");
        string compiler = NativeBindingRecordCommand.FindCompiler(OperatingSystem.IsWindows() ? "clang-cl.exe" : "clang");
        string library = await NativeBindingRecordCommand.FindLibraryAsync(compiler, await ClangMajorAsync(compiler, cancellationToken),
            cancellationToken);
        int major = installation.Version.Major;
        return await BuildAsync(compiler, options, NativeBindingResources.ReadManifest(major), major, installation.ServerIncludeDirectory,
            library, directory, cancellationToken);
    }

    /// <summary>
    /// Builds a type catalog from a header manifest: Clang serializes the manifest's AST, and a worker reads it through
    /// libclang as bindgen does.
    /// </summary>
    /// <param name="compiler">The Clang frontend.</param>
    /// <param name="options">The frontend's language, include and target options, including syntax-only compilation.</param>
    /// <param name="headers">The header manifest's source.</param>
    /// <param name="major">The PostgreSQL major.</param>
    /// <param name="includeRoot">The server include directory, whose declarations are allowlisted.</param>
    /// <param name="library">The libclang module matching the compiler.</param>
    /// <param name="directory">A scratch directory for the frontend's input and output.</param>
    /// <param name="cancellationToken">Cancels the frontend.</param>
    /// <returns>The type catalog and raw inventory.</returns>
    internal static async Task<NativeHeaderCatalogs> BuildAsync(string compiler, IReadOnlyList<string> options, string headers, int major,
        string includeRoot, string library, string directory, CancellationToken cancellationToken)
    {
        string source = Path.Join(directory, "catalog.c");
        string ast = Path.Join(directory, "catalog.ast");
        await File.WriteAllTextAsync(source, headers, cancellationToken);
        await NativeBindingHeaderCommand.CompileAsync(compiler,
            // The frontend includes PostgreSQL as system headers, whose comments Clang otherwise drops; pgrx's bindgen
            // includes them as ordinary headers and keeps their documentation.
            [.. options, "-Xclang", "-fretain-comments-from-system-headers", "-Xclang", "-detailed-preprocessing-record",
                "-Xclang", "-emit-pch", "-Xclang", "-o", "-Xclang", ast, source],
            Path.Join(directory, "catalog.txt"), directory, cancellationToken);
        return await NativeBindingHeaderCatalogWorker.InspectAsync(
            new NativeCatalogRequest(ast, Path.GetFullPath(library), major, Path.GetFullPath(includeRoot)), directory, cancellationToken);
    }

    /// <summary>
    /// Reads the compiler's major version, which names its libclang module on Linux.
    /// </summary>
    internal static async Task<int> ClangMajorAsync(string compiler, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo(compiler)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--version");
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Cannot query the selected Clang version.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        Match version = ClangVersion().Match(await output);
        _ = await errors;
        return process.ExitCode == 0 && version.Success
            ? int.Parse(version.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException("The selected Clang did not report its version.");
    }

    /// <summary>
    /// Lists every type, field, alias, enum and node tag difference, comparing representations after normalizing their
    /// formatting.
    /// </summary>
    internal static List<string> Compare(NativeBindingCatalog reference, NativeBindingCatalog derived)
    {
        var differences = new List<string>();
        foreach ((string name, uint value) in reference.Tags)
        {
            if (!derived.Tags.TryGetValue(name, out uint actual) || actual != value)
            {
                differences.Add($"tag {name}: {value} != {(derived.Tags.TryGetValue(name, out uint found) ? found.ToString(CultureInfo.InvariantCulture) : "missing")}");
            }
        }

        differences.AddRange(derived.Tags.Keys.Except(reference.Tags.Keys).Select(static name => $"tag {name}: extra"));
        foreach ((string name, NativeBindingType type) in reference.Types)
        {
            if (!derived.Types.TryGetValue(name, out NativeBindingType? actual))
            {
                differences.Add($"type {name}: missing");
                continue;
            }

            if (type.IsUnion != actual.IsUnion || type.IsNode != actual.IsNode || !type.CastTags.SequenceEqual(actual.CastTags))
            {
                differences.Add($"type {name}: union {type.IsUnion}/{actual.IsUnion}, node {type.IsNode}/{actual.IsNode}, " +
                    $"tags {type.CastTags.Count}/{actual.CastTags.Count}");
            }

            if (type.Fields.Count != actual.Fields.Count)
            {
                differences.Add($"type {name}: fields [{string.Join(", ", type.Fields.Select(static field => field.Name))}] != " +
                    $"[{string.Join(", ", actual.Fields.Select(static field => field.Name))}]");
                continue;
            }

            for (int index = 0; index < type.Fields.Count; index++)
            {
                NativeBindingField expected = type.Fields[index];
                NativeBindingField field = actual.Fields[index];
                if (expected.Name != field.Name || expected.NativeName != field.NativeName ||
                    Normalize(expected.Representation) != Normalize(field.Representation))
                {
                    differences.Add($"field {name}.{expected.Name}: {Normalize(expected.Representation)} != " +
                        $"{field.Name}: {Normalize(field.Representation)}");
                }
            }
        }

        differences.AddRange(derived.Types.Keys.Except(reference.Types.Keys).Select(static name => $"type {name}: extra"));
        foreach ((string name, string representation) in reference.Aliases)
        {
            if (!derived.Aliases.TryGetValue(name, out string? actual))
            {
                differences.Add($"alias {name}: missing ({Normalize(representation)})");
            }
            else if (Normalize(representation) != Normalize(actual))
            {
                differences.Add($"alias {name}: {Normalize(representation)} != {Normalize(actual)}");
            }
        }

        differences.AddRange(derived.Aliases.Keys.Except(reference.Aliases.Keys)
            .Select(name => $"alias {name}: extra ({Normalize(derived.Aliases[name])})"));
        HashSet<string> anonymousValues = [.. derived.Enums.Where(static pair => pair.Key.StartsWith("_bindgen_ty_", StringComparison.Ordinal))
            .Select(static pair => Constants(pair.Value))];
        foreach ((string name, NativeBindingEnum value) in reference.Enums)
        {
            if (name.StartsWith("_bindgen_ty_", StringComparison.Ordinal))
            {
                if (!anonymousValues.Contains(Constants(value)))
                {
                    differences.Add($"enum {name}: no anonymous enum with constants {Constants(value)}");
                }

                continue;
            }

            if (!derived.Enums.TryGetValue(name, out NativeBindingEnum? actual))
            {
                differences.Add($"enum {name}: missing");
            }
            else if (value.Storage != actual.Storage || !value.Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(actual.Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)))
            {
                differences.Add($"enum {name}: {value.Storage} {Constants(value)} != {actual.Storage} {Constants(actual)}");
            }
        }

        HashSet<string> referenceAnonymous = [.. reference.Enums.Where(static pair => pair.Key.StartsWith("_bindgen_ty_", StringComparison.Ordinal))
            .Select(static pair => Constants(pair.Value))];
        differences.AddRange(derived.Enums.Where(pair => !reference.Enums.ContainsKey(pair.Key) &&
            !(pair.Key.StartsWith("_bindgen_ty_", StringComparison.Ordinal) && referenceAnonymous.Contains(Constants(pair.Value))))
            .Select(static pair => $"enum {pair.Key}: extra {Constants(pair.Value)}"));
        return differences;
    }

    /// <summary>
    /// Lists every foreign function and global difference, comparing type expressions after normalizing their formatting.
    /// </summary>
    internal static List<string> CompareRaw(NativeBindingRawCatalog reference, NativeBindingRawCatalog derived)
    {
        var differences = new List<string>();
        foreach ((string name, NativeBindingFunction function) in reference.Functions)
        {
            if (!derived.Functions.TryGetValue(name, out NativeBindingFunction? actual))
            {
                differences.Add($"function {name}: missing");
                continue;
            }

            string expected = Signature(function);
            string observed = Signature(actual);
            if (expected != observed)
            {
                differences.Add($"function {name}: {expected} != {observed}");
            }
        }

        differences.AddRange(derived.Functions.Keys.Except(reference.Functions.Keys).Select(static name => $"function {name}: extra"));
        foreach ((string name, NativeBindingGlobal global) in reference.Globals)
        {
            if (!derived.Globals.TryGetValue(name, out NativeBindingGlobal? actual))
            {
                differences.Add($"global {name}: missing");
            }
            else if (Global(global) != Global(actual))
            {
                differences.Add($"global {name}: {Global(global)} != {Global(actual)}");
            }
        }

        differences.AddRange(derived.Globals.Keys.Except(reference.Globals.Keys).Select(static name => $"global {name}: extra"));
        foreach ((string name, NativeBindingConstant constant) in reference.ReferenceConstants)
        {
            if (!derived.ReferenceConstants.TryGetValue(name, out NativeBindingConstant? actual))
            {
                differences.Add($"constant {name}: missing ({constant.Representation} = {constant.Expression})");
            }
            else if (Compact(constant.Representation) != Compact(actual.Representation) || constant.Expression != actual.Expression)
            {
                differences.Add($"constant {name}: {constant.Representation} = {constant.Expression} != {actual.Representation} = {actual.Expression}");
            }
        }

        differences.AddRange(derived.ReferenceConstants.Where(pair => !reference.ReferenceConstants.ContainsKey(pair.Key))
            .Select(static pair => $"constant {pair.Key}: extra ({pair.Value.Representation} = {pair.Value.Expression})"));
        return differences;
    }

    /// <summary>
    /// Removes the spaces a token printer leaves inside a type path, such as <c>&amp; :: core :: ffi :: CStr</c>.
    /// </summary>
    private static string Compact(string representation) => Whitespace().Replace(representation, string.Empty);

    private static string Signature(NativeBindingFunction function)
        => $"{function.NativeSymbol} {function.Abi} ({string.Join(", ", function.Parameters.Select(static parameter => parameter.Name + ": " + Normalize(parameter.Representation)))}" +
            $"{(function.IsVariadic ? ", ..." : string.Empty)}) -> {Normalize(function.ReturnType)} [{string.Join(" ", function.Attributes.Select(Normalize))}]";

    private static string Global(NativeBindingGlobal global)
        => $"{global.NativeSymbol} {global.Abi} {(global.IsMutable ? "mut " : string.Empty)}{Normalize(global.Representation)} [{string.Join(" ", global.Attributes.Select(Normalize))}]";

    private static string Constants(NativeBindingEnum value)
        => string.Join(",", value.Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Key + "=" + pair.Value));

    /// <summary>
    /// Removes formatting and trailing commas from a bindgen type expression.
    /// </summary>
    internal static string Normalize(string representation)
    {
        string text = Whitespace().Replace(representation, " ").Trim();
        // Spacing around brackets and a trailing comma are formatting only.
        text = SpaceInsideBrackets().Replace(text, "$1");
        text = TrailingComma().Replace(text, "$1");
        return text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"clang version (?<major>\d+)\.")]
    private static partial Regex ClangVersion();

    [GeneratedRegex(@"\s*([<>()\[\],])\s*")]
    private static partial Regex SpaceInsideBrackets();

    [GeneratedRegex(@",([)>])")]
    private static partial Regex TrailingComma();
}
