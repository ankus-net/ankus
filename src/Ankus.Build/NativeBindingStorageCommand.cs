using System.Globalization;
using System.Text;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Collects selected-header types and compiler-evaluated constants before publishing measured storage.
/// </summary>
internal static class NativeBindingStorageCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes a measured function/global storage contract only after complete native validation.
    /// </summary>
    internal static async Task<NativeHeaderStorage> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is < 4 or > 8)
        {
            throw new ArgumentException("Expected binding-storage <symbol-list> <major> <pg_config> <output-directory> [clang] [windows-library-directories] [runtime-identifier] [target-triple].", nameof(arguments));
        }

        NativeHeaderCatalog catalog = await NativeBindingHeaderCommand.RunAsync(arguments, cancellationToken);
        int major = catalog.Target.PostgresVersion / 10000;
        PostgresInstallation installation = arguments[2].Length == 0
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
            : await PostgresInstallation.CreateAsync(arguments[2], cancellationToken);
        string output = Path.GetFullPath(arguments[3]);
        var observations = new StringBuilder();
        KeyValuePair<string, NativeHeaderSymbol>[][] batches = [.. catalog.Symbols.Chunk(256)];
        if (batches.Length == 0)
        {
            batches = [[]];
        }

        for (int index = 0; index < batches.Length; index++)
        {
            string suffix = batches.Length == 1 ? "" : "-" + index.ToString("D4", CultureInfo.InvariantCulture);
            string source = Path.Join(output, "native-storage" + suffix + ".c");
            string ast = Path.Join(output, "native-storage" + suffix + ".ast.json");
            var batch = new NativeHeaderCatalog(catalog.Target, batches[index].ToDictionary());
            await File.WriteAllTextAsync(source, NativeBindingStorageProbe.GenerateSource(batch, NativeBindingResources.ReadHeaders(major)), cancellationToken);
            await NativeBindingHeaderCommand.InspectAsync(installation, arguments, source, ast, output, cancellationToken);
            await using FileStream stream = File.OpenRead(ast);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 512 }, cancellationToken);
            string measured = NativeBindingStorageProbe.ReadObservations(batch, document.RootElement);
            _ = NativeBindingStorageProbe.Read(batch, measured);
            observations.Append(index == 0 ? measured : measured[(measured.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
        }

        NativeHeaderStorage storage = NativeBindingStorageProbe.Read(catalog, observations.ToString());
        await File.WriteAllTextAsync(Path.Join(output, "native-storage.txt"), observations.ToString(), cancellationToken);
        await File.WriteAllTextAsync(Path.Join(output, "native-storage.json"),
            JsonSerializer.Serialize(storage, s_jsonOptions) + "\n", cancellationToken);
        Console.WriteLine($"PG{major}: measured {storage.Symbols.Count} native header symbol storage contracts.");
        return storage;
    }
}
