using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Measures native value layouts using the selected installation's headers and the host C toolchain.
/// </summary>
internal static class NativeBindingLayoutCommand
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Compiles, executes and validates one complete native layout probe.
    /// </summary>
    /// <param name="arguments">The major, pg_config path, output directory, optional compiler, Windows library paths, target RID and compiler triple.</param>
    /// <param name="cancellationToken">Cancels installation discovery and child processes.</param>
    /// <returns>The complete validated layout measured from the selected installation.</returns>
    internal static async Task<NativeBindingLayout> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is < 3 or > 7)
        {
            throw new ArgumentException("Expected binding-layouts <major> <pg_config> <output-directory> [compiler] [windows-library-directories] [runtime-identifier] [target-triple].", nameof(arguments));
        }

        int major = int.Parse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture);
        NativeBindingCatalog catalog = NativeBindingResources.ReadCatalog(major);
        NativeBindingCollection collection = await NativeBindingCollectionCommand.RunAsync(catalog, arguments, cancellationToken);
        NativeBindingSelectedNodes selected = NativeBindingNodeAvailability.Read(catalog, collection.Records.Graph);
        return await MeasureAsync(selected, arguments, cancellationToken);
    }

    /// <summary>
    /// Executes an independent native probe using reference fields confirmed by the complete selected-header graph.
    /// </summary>
    internal static async Task<NativeBindingLayout> MeasureAsync(NativeBindingSelectedNodes selected, string[] arguments, CancellationToken cancellationToken)
    {
        NativeBindingCatalog catalog = selected.Catalog;
        int major = catalog.PostgresMajor;
        PostgresInstallation installation = string.IsNullOrEmpty(arguments[1])
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken: cancellationToken)
            : await PostgresInstallation.CreateAsync(arguments[1], cancellationToken);
        if (installation.Version.Major != major)
        {
            throw new InvalidOperationException($"Expected PostgreSQL {major}, but the selected installation is {installation.Label}.");
        }

        string output = Path.GetFullPath(arguments[2]);
        Directory.CreateDirectory(output);
        string source = Path.Combine(output, "native-layout.c");
        string executable = Path.Combine(output, OperatingSystem.IsWindows() ? "native-layout.exe" : "native-layout");
        await File.WriteAllTextAsync(source, NativeBindingProbe.GenerateSource(catalog, NativeBindingResources.ReadHeaders(major)), cancellationToken);
        string observations = await CompileProbeAsync(installation, arguments, source, executable, output, cancellationToken);
        NativeBindingLayout layout = NativeBindingProbe.Read(catalog, observations);
        string expectedRuntime = arguments.Length >= 6 && arguments[5].Length != 0 ? arguments[5] : RuntimeInformation.RuntimeIdentifier;
        if (layout.RuntimeIdentifier != expectedRuntime)
        {
            throw new InvalidOperationException($"Native bindings target {layout.RuntimeIdentifier}, but the requested runtime is {expectedRuntime}.");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "native-layout.txt"), observations, cancellationToken);
        string json = JsonSerializer.Serialize(layout, s_jsonOptions) + "\n";
        await File.WriteAllTextAsync(Path.Combine(output, "native-layout.json"), json, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "native-node-availability.json"),
            JsonSerializer.Serialize(selected.AbsentFields, s_jsonOptions) + "\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "native-node-declarations.json"),
            JsonSerializer.Serialize(selected.Declarations, s_jsonOptions) + "\n", cancellationToken);
        Console.WriteLine($"PG{major}: measured {layout.Types.Count} native values and {layout.Types.Values.Sum(static type => type.Fields.Count)} fields.");
        return layout;
    }

    /// <summary>
    /// Compiles and executes a generated probe using the selected installation and shared toolchain arguments.
    /// </summary>
    /// <param name="installation">The authoritative header installation.</param>
    /// <param name="arguments">The layout command's installation and optional toolchain arguments.</param>
    /// <param name="source">The generated C source path.</param>
    /// <param name="executable">The probe executable path.</param>
    /// <param name="output">The probe working directory.</param>
    /// <param name="cancellationToken">Cancels compilation or execution.</param>
    /// <param name="requireC11">Whether the probe uses C11 compile-time type checks; MSVC always compiles as C11.</param>
    /// <returns>The successful probe's observations.</returns>
    internal static async Task<string> CompileProbeAsync(PostgresInstallation installation, string[] arguments,
        string source, string executable, string output, CancellationToken cancellationToken, bool requireC11 = false)
    {
        string compiler = arguments.Length >= 4 && arguments[3].Length != 0 ? arguments[3] : OperatingSystem.IsWindows() ? "cl.exe" : "cc";
        var options = new List<string>();
        // MSVC's default C mode predates C11, which PostgreSQL 19's headers require; older headers compile unchanged
        // under it, as the header and link commands already show.
        if (requireC11 || OperatingSystem.IsWindows())
        {
            options.Add(OperatingSystem.IsWindows() ? "/std:c11" : "-std=gnu11");
        }

        if (OperatingSystem.IsWindows())
        {
            string libraries = arguments.Length >= 5 ? arguments[4] : string.Empty;
            // PostgreSQL must configure the CRT before the standalone probe's own
            // I/O definitions are compiled outside backend implementation sections.
            options.AddRange(["/nologo", "/O2", "/MD", "/WX", "/FIpostgres.h", "/Fo" + Path.ChangeExtension(executable, ".obj"), "/Fe" + executable]);
            options.AddRange(["/I" + installation.ServerIncludeDirectory, "/I" + installation.IncludeDirectory,
                "/I" + Path.Combine(installation.ServerIncludeDirectory, "port", "win32"),
                "/I" + Path.Combine(installation.ServerIncludeDirectory, "port", "win32_msvc")]);
            options.AddRange(WindowsToolchain.GetIncludeDirectories(libraries).Select(static path => "/I" + path));
            options.Add(source);
            if (libraries.Length != 0)
            {
                options.Add("/link");
                options.AddRange(libraries.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(static path => "/LIBPATH:" + path));
            }
        }
        else
        {
            options.AddRange(await installation.GetPreprocessorArgumentsAsync(cancellationToken));
            if (arguments.Length == 7 && arguments[6].Length != 0)
            {
                options.Add("--target=" + arguments[6]);
            }

            options.AddRange(["-O2", "-Wall", "-Wextra", "-Werror", "-isystem", installation.ServerIncludeDirectory,
                "-isystem", installation.IncludeDirectory, source, "-o", executable]);
        }

        AddProbeLinkOptions(options);
        await CompileAndLinkProbeAsync(compiler, options, executable, output, cancellationToken);
        return await RunProcessAsync(executable, [], output, cancellationToken);
    }

    /// <summary>
    /// Compiles with the selected C compiler and links the standalone probe implementation.
    /// </summary>
    /// <param name="compiler">The selected native C compiler.</param>
    /// <param name="options">Compiler-driver arguments including the object and executable output paths.</param>
    /// <param name="executable">The probe executable path.</param>
    /// <param name="directory">The probe working directory.</param>
    /// <param name="cancellationToken">Cancels compilation or linking.</param>
    internal static async Task CompileAndLinkProbeAsync(string compiler, List<string> options, string executable,
        string directory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            await RunProcessAsync(compiler, options, directory, cancellationToken);
            return;
        }

        // COFF linkers resolve imports before discarding unused COMDATs. Compile
        // every header declaration, then separate backend implementations from the
        // standalone program. Any probe relocation into removed code still fails.
        int linker = options.FindIndex(static option => option.Equals("/link", StringComparison.OrdinalIgnoreCase));
        await RunProcessAsync(compiler, [.. options.Take(linker).Where(static option => !option.StartsWith("/Fe", StringComparison.OrdinalIgnoreCase)),
            "/c", "/DANKUS_STANDALONE_PROBE"], directory, cancellationToken);
        string compiled = Path.ChangeExtension(executable, ".obj");
        string standalone = Path.ChangeExtension(executable, ".standalone.obj");
        byte[] image = await File.ReadAllBytesAsync(compiled, cancellationToken);
        NativeObjectSymbols.IdentifyCoffProbeUnwindSections(image);
        await File.WriteAllBytesAsync(compiled, image, cancellationToken);
        await RunProcessAsync("llvm-objcopy.exe", ["--remove-section=ankus_header_code", "--remove-section=ankus_header_data",
            "--remove-section=ankus_header_const", "--remove-section=ankus_header_bss", "--remove-section=ankus_eh", compiled, standalone], directory, cancellationToken);
        image = await File.ReadAllBytesAsync(standalone, cancellationToken);
        NativeObjectImports unused = NativeObjectSymbols.ReadUnreferencedCoffImports(image);
        if (unused.Symbols.Count != 0)
        {
            await RunProcessAsync("llvm-objcopy.exe", [.. unused.Symbols.Select(static name => "--strip-symbol=" + name), standalone],
                directory, cancellationToken);
        }

        await RunProcessAsync("link.exe", ["/nologo", "/WX", "/OUT:" + executable,
            standalone, .. options.Skip(linker + 1)], directory, cancellationToken);
    }

    /// <summary>
    /// Enables separate function/data sections and linker elimination for standalone probes.
    /// </summary>
    /// <param name="options">The compiler-driver arguments, including any Windows linker arguments.</param>
    internal static void AddProbeLinkOptions(List<string> options)
    {
        if (OperatingSystem.IsWindows())
        {
            options.InsertRange(0, ["/Gy", "/Gw"]);
            if (!options.Contains("/link", StringComparer.OrdinalIgnoreCase))
            {
                options.Add("/link");
            }

            options.Add("/OPT:REF");
        }
        else
        {
            options.AddRange(["-ffunction-sections", "-fdata-sections",
                OperatingSystem.IsMacOS() ? "-Wl,-dead_strip" : "-Wl,--gc-sections"]);
        }
    }

    /// <summary>
    /// Runs a native tool and joins its diagnostics and descendants before releasing caller-owned staging.
    /// </summary>
    internal static async Task<string> RunProcessAsync(string program, IEnumerable<string> arguments, string directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Task<string> output = Task.FromResult(string.Empty);
        Task<string> errors = Task.FromResult(string.Empty);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Cannot start {program}.");
        try
        {
            output = ReadProcessOutputAsync(process.StandardOutput, cancellationToken);
            errors = ReadProcessOutputAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(output, errors);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll((Task)output, errors).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        string standardOutput = await output;
        string standardError = await errors;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{program} exited with {process.ExitCode}: {standardOutput}{standardError}");
        }

        return standardOutput;
    }

    /// <summary>
    /// Drains synchronous Windows pipes without occupying thread-pool workers when no read cancellation is required.
    /// </summary>
    /// <param name="reader">The redirected stream owned by the caller's child process.</param>
    /// <param name="cancellationToken">Cancels a read through the stream's asynchronous implementation.</param>
    /// <returns>The complete redirected text; the caller must join the reader before disposing its process.</returns>
    internal static Task<string> ReadProcessOutputAsync(StreamReader reader, CancellationToken cancellationToken)
        => OperatingSystem.IsWindows() && !cancellationToken.CanBeCanceled
            ? Task.Factory.StartNew(reader.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : reader.ReadToEndAsync(cancellationToken);
}
