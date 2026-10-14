using System.Diagnostics;
using System.Text.Json;

namespace Ankus.Build;

/// <summary>
/// Regenerates native declaration catalogs from the pinned read-only pgrx reference.
/// </summary>
internal static class NativeBindingCatalogCommand
{
    /// <summary>
    /// Writes catalogs to the supplied directory, or checks their exact existing contents.
    /// </summary>
    /// <param name="arguments">The pgrx checkout, output directory, and optional --check flag.</param>
    internal static async Task RunAsync(string[] arguments)
    {
        if (arguments.Length is < 2 or > 3 || arguments.Length == 3 && arguments[2] != "--check")
        {
            throw new ArgumentException("Expected binding-catalogs <pgrx-checkout> <output-directory> [--check].", nameof(arguments));
        }

        const string Revision = "fc91c63ebad11784647b50ee7e265c1fd9c9924f";
        var options = new JsonSerializerOptions { WriteIndented = true };
        for (int major = 13; major <= 19; major++)
        {
            string source = await ReadSourceAsync(arguments[0], Revision, $"pgrx-pg-sys/src/include/pg{major}.rs");
            NativeBindingCatalog catalog = NativeBindingParser.Parse(source, major);
            string path = Path.Join(arguments[1], $"pg{major}.json");
            string content = JsonSerializer.Serialize(catalog, options) + "\n";
            await WriteAsync(path, content, arguments.Length == 3);
            NativeBindingRawCatalog raw = NativeBindingRawParser.Parse(source, major) with
            {
                SourceRevision = Revision
            };
            await WriteAsync(Path.Join(arguments[1], $"pg{major}.raw.json"),
                JsonSerializer.Serialize(raw, options) + "\n", arguments.Length == 3);
            if (arguments.Length == 3)
            {
                string headers = await ReadSourceAsync(arguments[0], Revision, $"pgrx-pg-sys/include/pg{major}.h");
                await WriteAsync(Path.Join(arguments[1], $"pg{major}.h"), headers, check: true);
            }

            NativeBindingType[] nodes = [.. catalog.Types.Values.Where(static type => type.IsNode)];
            Console.WriteLine($"PG{major}: {catalog.Tags.Count} tags, {nodes.Length} nodes, {nodes.Sum(static type => type.Fields.Count)} node fields, " +
                $"{raw.Functions.Count} functions, {raw.Globals.Count} globals, {raw.ReferenceConstants.Count} reference constants.");
        }

        string license = await ReadSourceAsync(arguments[0], Revision, "LICENSE");
        await WriteAsync(Path.Join(arguments[1], "LICENSE.pgrx"), license, arguments.Length == 3);
    }

    private static async Task WriteAsync(string path, string content, bool check)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        if (check)
        {
            throw new InvalidOperationException($"Native binding input is stale: {path}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    /// <summary>
    /// Reads committed bindings without fetching or modifying the reference checkout.
    /// </summary>
    /// <param name="checkout">The existing pgrx checkout.</param>
    /// <param name="revision">The exact committed source revision.</param>
    /// <param name="path">The committed declaration, header manifest or license path.</param>
    /// <returns>The exact contents of the committed input file.</returns>
    private static async Task<string> ReadSourceAsync(string checkout, string revision, string path)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[] { "-C", Path.GetFullPath(checkout), "show", $"{revision}:{path}" })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start git.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Cannot read {path}: {(await errors).Trim()}");
        }

        return await output;
    }
}
