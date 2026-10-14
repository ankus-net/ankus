using System.Text.Json;

namespace Ankus.Build;

/// <summary>
/// Reads a header manifest's serialized AST through libclang in a dedicated process, as the record collector does,
/// and publishes only the finished catalogs.
/// </summary>
internal static class NativeBindingHeaderCatalogWorker
{
    /// <summary>
    /// Runs once in a dedicated process; the Windows compiler module remains loaded until process exit.
    /// </summary>
    /// <param name="arguments">The request file.</param>
    internal static async Task RunAsync(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("Expected binding-catalog-worker <request-json>.", nameof(arguments));
        }

        var input = new FileInfo(arguments[0]);
        if (input.Length > 64 * 1024)
        {
            throw new InvalidDataException("Native catalog request exceeds the byte limit.");
        }

        NativeCatalogRequest request;
        await using (FileStream stream = input.OpenRead())
        {
            request = await JsonSerializer.DeserializeAsync<NativeCatalogRequest>(stream, NativeBindingRecordWorker.JsonOptions)
                ?? throw new FormatException("Empty native catalog request.");
        }

        Validate(request);
        using var library = new NativeClang(request.Library);
        using NativeClangUnit unit = library.Load(request.Ast);
        NativeBindingHeaderAst ast = new NativeBindingHeaderReader(library, unit).Read();
        NativeHeaderCatalogs catalogs = NativeBindingHeaderCatalog.Build(ast, request.Major, request.IncludeRoot);
        await using Stream output = Console.OpenStandardOutput();
        await JsonSerializer.SerializeAsync(output, catalogs);
    }

    /// <summary>
    /// Builds the catalog in a worker process.
    /// </summary>
    /// <param name="request">The serialized AST, library and selection.</param>
    /// <param name="directory">The worker's scratch directory.</param>
    /// <param name="cancellationToken">Stops the worker.</param>
    /// <returns>The type catalog and raw inventory.</returns>
    internal static async Task<NativeHeaderCatalogs> InspectAsync(NativeCatalogRequest request, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(request);
        string input = Path.Join(directory, "native-catalog-request.json");
        string output = Path.Join(directory, "native-catalog.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(request, NativeBindingRecordWorker.JsonOptions), cancellationToken);
        await NativeBindingRecordWorker.RunProcessAsync("binding-catalog-worker", "Native catalog worker", input, output, directory,
            cancellationToken);
        await using FileStream result = File.OpenRead(output);
        NativeHeaderCatalogs catalogs = await JsonSerializer.DeserializeAsync<NativeHeaderCatalogs>(result, cancellationToken: cancellationToken)
            ?? throw new FormatException("Missing native catalog worker result.");
        return catalogs.Types.PostgresMajor == request.Major && catalogs.Raw.PostgresMajor == request.Major ? catalogs
            : throw new FormatException("The native catalogs have the wrong major.");
    }

    private static void Validate(NativeCatalogRequest request)
    {
        if (request.Major is < 13 or > 19 || string.IsNullOrEmpty(request.Ast) || string.IsNullOrEmpty(request.Library) ||
            string.IsNullOrEmpty(request.IncludeRoot) || !Path.IsPathFullyQualified(request.Ast) ||
            !Path.IsPathFullyQualified(request.Library) || !Path.IsPathFullyQualified(request.IncludeRoot))
        {
            throw new FormatException("Invalid native catalog worker request.");
        }
    }
}

/// <summary>
/// Carries the serialized AST, the libclang library and the catalog selection into the worker.
/// </summary>
/// <param name="Ast">The compiler's serialized AST of the header manifest.</param>
/// <param name="Library">The explicitly selected libclang module.</param>
/// <param name="Major">The PostgreSQL major.</param>
/// <param name="IncludeRoot">The server include directory, whose declarations are allowlisted.</param>
internal sealed record NativeCatalogRequest(string Ast, string Library, int Major, string IncludeRoot);
