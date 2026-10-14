using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Build;

/// <summary>
/// Isolates synchronous native parsing in a cancellable process and publishes only a complete graph.
/// </summary>
internal static class NativeBindingRecordWorker
{
    /// <summary>
    /// Uses an exact, required-property protocol for requests and observations.
    /// </summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 512,
    };

    /// <summary>
    /// Runs once in a dedicated process; the Windows compiler module remains loaded until process exit.
    /// </summary>
    internal static async Task RunAsync(string[] arguments)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("Expected binding-records-worker <request-json>.", nameof(arguments));
        }

        var input = new FileInfo(arguments[0]);
        if (input.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Native record request exceeds the byte limit.");
        }

        await using FileStream stream = input.OpenRead();
        NativeRecordRequest request = await JsonSerializer.DeserializeAsync<NativeRecordRequest>(stream, JsonOptions)
            ?? throw new FormatException("Empty native record request.");
        ValidateRequest(request);
        using var library = new NativeClang(request.Library);
        using NativeClangUnit unit = library.Load(request.Ast);
        var reader = new NativeBindingRecordReader(library, unit);
        NativeRecordGraph graph = reader.Read(request.Target, request.Symbols);
        NativeBindingRecordValidation.Validate(graph, request.Target, request.Symbols.Keys);
        await using Stream output = Console.OpenStandardOutput();
        await JsonSerializer.SerializeAsync(output, graph, JsonOptions);
    }

    /// <summary>
    /// Starts a worker only after request validation and bounds both output streams while it runs.
    /// </summary>
    internal static async Task<NativeRecordGraph> InspectAsync(NativeRecordRequest request, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        string input = Path.Join(directory, "native-record-request.json");
        string output = Path.Join(directory, "native-record-observations.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(request, JsonOptions), cancellationToken);
        await RunProcessAsync("binding-records-worker", "Native record worker", input, output, directory, cancellationToken);
        await using FileStream result = File.OpenRead(output);
        NativeRecordGraph graph = await JsonSerializer.DeserializeAsync<NativeRecordGraph>(result, JsonOptions, cancellationToken)
            ?? throw new FormatException("Missing native record worker result.");
        NativeBindingRecordValidation.Validate(graph, request.Target, request.Symbols.Keys);
        return graph;
    }

    /// <summary>
    /// Runs one worker command over a request file in a dedicated build-tool process, bounding both output streams
    /// while it runs and keeping its standard output in a file.
    /// </summary>
    /// <param name="command">The worker command.</param>
    /// <param name="description">The worker's name in diagnostics.</param>
    /// <param name="input">The request file.</param>
    /// <param name="output">The file that receives the worker's standard output.</param>
    /// <param name="directory">The worker's working directory.</param>
    /// <param name="cancellationToken">Stops the worker.</param>
    internal static async Task RunProcessAsync(string command, string description, string input, string output, string directory,
        CancellationToken cancellationToken)
    {
        string? configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        string executable = !string.IsNullOrEmpty(configuredHost) ? configuredHost
            : Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath!
            : OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(NativeBindingRecordWorker).Assembly.Location);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(input);
        await using (FileStream observations = File.Create(output))
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {description}.");
            using var errors = new MemoryStream();
            Task copy = NativeBindingHeaderCommand.CopyAsync(process.StandardOutput.BaseStream, observations, 512 * 1024 * 1024, cancellationToken);
            Task diagnostics = NativeBindingHeaderCommand.CopyAsync(process.StandardError.BaseStream, errors, 4 * 1024 * 1024, cancellationToken);
            Task exit = process.WaitForExitAsync(cancellationToken);
            try
            {
                // Observe either stream's bound/cancellation failure before waiting indefinitely for native parsing.
                var pending = new List<Task> { copy, diagnostics, exit };
                while (pending.Count != 0)
                {
                    Task completed = await Task.WhenAny(pending);
                    await completed;
                    pending.Remove(completed);
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException($"{description} exited with {process.ExitCode}: {System.Text.Encoding.UTF8.GetString(errors.ToArray())}");
                }
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(copy, diagnostics);
                throw;
            }
        }
    }

    private static void ValidateRequest(NativeRecordRequest request)
    {
        if (request.Target is null || request.Target.ClangMajor < 20 ||
            string.IsNullOrEmpty(request.Target.RuntimeIdentifier) ||
            !NativeBindingTarget.IsValid(request.Target.RuntimeIdentifier, request.Target.PointerSize, request.Target.IsLittleEndian) ||
            request.Target.PostgresVersion / 10000 is < 13 or > 19 || request.Symbols is null || request.Symbols.Count > 100_000 ||
            string.IsNullOrEmpty(request.Ast) || string.IsNullOrEmpty(request.Library) ||
            !Path.IsPathFullyQualified(request.Ast) || !Path.IsPathFullyQualified(request.Library))
        {
            throw new FormatException("Invalid native record worker request.");
        }

        foreach ((string name, NativeHeaderRequest symbol) in request.Symbols)
        {
            NativeBindingCDeclaration.ValidateName(name);
            if (symbol is null || symbol.Name != name)
            {
                throw new FormatException("Inconsistent native record symbol selection.");
            }

            NativeBindingCDeclaration.ValidateName(symbol.NativeName);
        }
    }
}

/// <summary>
/// Carries only the selected compiler's serialized AST, library, target and symbol identities into the worker.
/// </summary>
/// <param name="Ast">The selected compiler's serialized declaration-only AST.</param>
/// <param name="Library">The explicitly selected libclang module.</param>
/// <param name="Target">The identity already observed by the executable frontend.</param>
/// <param name="Symbols">The complete selected symbol identities.</param>
internal sealed record NativeRecordRequest(string Ast, string Library,
    NativeHeaderTarget Target, IReadOnlyDictionary<string, NativeHeaderRequest> Symbols);
