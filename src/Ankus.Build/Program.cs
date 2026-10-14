using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ankus.Build;
using Ankus.PgConfig;

try
{
    if (args.Length > 0 && args[0] == "control-file")
    {
        await ExtensionControlCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "publish-sql")
    {
        await UpgradeSqlCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "plugin-aliases")
    {
        await OutputPluginAliasCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-catalogs")
    {
        await NativeBindingCatalogCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-catalog-headers")
    {
        return await NativeBindingHeaderCatalogCommand.RunAsync(args[1..]) == 0 ? 0 : 2;
    }

    if (args.Length > 0 && args[0] == "binding-header-manifest")
    {
        await NativeBindingHeaderManifestCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-layouts")
    {
        await NativeBindingLayoutCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-sources")
    {
        await NativeBindingSourceCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "target-export")
    {
        await NativeTargetCommand.ExportAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-compile")
    {
        await NativeBindingCompilationCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-link")
    {
        await NativeBindingLinkCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-signatures")
    {
        await NativeBindingSignatureCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-header-types")
    {
        await NativeBindingHeaderCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-storage")
    {
        await NativeBindingStorageCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-records")
    {
        await NativeBindingRecordCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-catalog-worker")
    {
        await NativeBindingHeaderCatalogWorker.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-records-worker")
    {
        await NativeBindingRecordWorker.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-record-checks")
    {
        await NativeBindingRecordCheckCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length > 0 && args[0] == "binding-call-sources")
    {
        await NativeBindingCallCommand.RunAsync(args[1..]);
        return 0;
    }

    if (args.Length is not (11 or 12 or 13 or 14 or 15))
    {
        throw new ArgumentException(
            "Expected assembly, artifact directory, PostgreSQL major, linker, toolchain libraries, " +
            "extension name, version, library, runtime identifier, optional pg_config path, target triple, optional control file, " +
            "optional secondary control list, optional saved schema, and optional versioned-library mode.");
    }

    string assembly = Path.GetFullPath(args[0]);
    string output = Path.GetFullPath(args[1]);
    int major = int.Parse(args[2], CultureInfo.InvariantCulture);
    PostgresInstallation installation = string.IsNullOrEmpty(args[9])
        ? await PostgresInstallation.DiscoverAsync(major)
        : await PostgresInstallation.CreateAsync(args[9]);
    if (installation.Version.Major != major)
    {
        throw new InvalidOperationException($"Expected PostgreSQL {major}, but '{installation.PgConfigPath}' is {installation.Label}.");
    }

    ExtensionManifest manifest = ExtensionManifest.Read(assembly);
    bool versionedLibrary = args.Length == 15 && VersionedLibrary.ParseMode(args[14]);
    SchemaSnapshot schema = SchemaSnapshot.Select(args.Length >= 14 && args[13].Length != 0 ? args[13] : null,
        manifest, args[5], args[6], args[7], major, args[8]);
    if (versionedLibrary)
    {
        // Without module_pathname, PostgreSQL leaves the marker unresolved; name this version's library directly.
        schema = schema.WithModulePath(VersionedLibrary.GetModulePath(VersionedLibrary.GetBaseName(args[7], args[6]), args[6]));
    }

    string? authored = args.Length >= 12 && args[11].Length != 0 ? File.ReadAllText(args[11]) : null;
    var package = new Dictionary<string, string>(ExtensionPackage.Create(args[5], args[6], args[7],
        schema.Sql, schema.Relocatable, authored, major, versionedLibrary));
    IReadOnlyDictionary<string, string> primaryControl = ExtensionControlFile.Parse(package[args[5] + ".control"]);
    bool relocatable = primaryControl["relocatable"] == "true";
    primaryControl.TryGetValue("schema", out string? defaultSchema);

    string[] controls = args.Length >= 13 ? File.ReadAllLines(args[12]) : [];
    ExtensionControlFile.Parse(package[args[5] + ".control"]).TryGetValue("directory", out string? scriptDirectory);
    var publication = new PublishedExtension(major, args[8], args[7], args[5] + ".control",
        args[5] + "--" + args[6] + ".sql", [], [.. controls.Select(static path => Path.GetFileName(path))], scriptDirectory);
    foreach (string path in controls)
    {
        string name = Path.GetFileName(path);
        bool currentVersion = name == args[5] + "--" + args[6] + ".control";
        (string control, bool effectiveRelocatable) = ExtensionControlSettings.MergeVersion(package[publication.Control],
            File.ReadAllText(path), major, currentVersion, schema.Relocatable);
        package.Add(name, control);
        if (currentVersion)
        {
            relocatable = effectiveRelocatable;
            if (ExtensionControlFile.Parse(control).TryGetValue("schema", out string? versionSchema))
            {
                defaultSchema = versionSchema;
            }
        }
    }

    Directory.CreateDirectory(output);
    publication.Write(output);
    string extensionDirectory = Path.Join(output, "extension");
    Directory.CreateDirectory(extensionDirectory);
    foreach ((string name, string content) in package)
    {
        WriteIfDifferent(Path.Join(extensionDirectory, name), content);
    }

    if (publication.ScriptDirectory is not null)
    {
        _ = publication.GetScriptDirectory(output, installation.SharedDirectory);
    }

    string source = Path.Join(output, "bridge.c");
    string nativeObject = Path.Join(output, OperatingSystem.IsWindows() ? "bridge.obj" : "bridge.o");
    WriteIfDifferent(source, manifest.NativeSource + NativeSchemaEmitter.Emit(args[5], args[6], args[7], major,
        args[8], relocatable, schema.Sql, schema.Graph, defaultSchema));
    WriteIfDifferent(Path.Join(output, "schema.sql"), schema.Sql);
    schema.Write(Path.Join(output, "schema.generated.json"));
    WriteIfDifferent(Path.Join(output, "exports.txt"), manifest.Exports + NativeSchemaEmitter.Symbol +
        (OperatingSystem.IsWindows() ? ",DATA\n" : "\n"));

    var libraries = new List<string> { nativeObject };
    var compilerArguments = new List<string>();
    string compiler;
    if (OperatingSystem.IsWindows())
    {
        compiler = Path.Join(Path.GetDirectoryName(args[3]) ?? string.Empty, "cl.exe");
        // PostgreSQL 19's headers require C11, which MSVC's default C mode predates.
        compilerArguments.AddRange(["/nologo", "/std:c11", "/c", "/O2", "/MT", "/WX", "/utf-8", $"/Fo{nativeObject}"]);
        compilerArguments.Add($"/I{installation.ServerIncludeDirectory}");
        compilerArguments.Add($"/I{installation.IncludeDirectory}");
        compilerArguments.Add($"/I{Path.Join(installation.ServerIncludeDirectory, "port", "win32")}");
        compilerArguments.Add($"/I{Path.Join(installation.ServerIncludeDirectory, "port", "win32_msvc")}");
        foreach (string directory in WindowsToolchain.GetIncludeDirectories(args[4]))
        {
            compilerArguments.Add($"/I{directory}");
        }

        string libraryDirectory = installation.TargetInfo?.MapPath(installation.LibraryDirectory) ?? installation.LibraryDirectory;
        libraries.Add(Path.Join(libraryDirectory, "postgres.lib"));
    }
    else
    {
        compiler = args[3];
        compilerArguments.AddRange(await installation.GetPreprocessorArgumentsAsync(CancellationToken.None));
        if (!string.IsNullOrEmpty(args[10]))
        {
            compilerArguments.Add($"--target={args[10]}");
        }

        compilerArguments.AddRange(["-c", "-O2", "-fPIC", "-Wall", "-Wextra", "-Werror"]);
        compilerArguments.AddRange(["-isystem", installation.ServerIncludeDirectory, "-o", nativeObject]);
        compilerArguments.AddRange(["-isystem", installation.IncludeDirectory]);
    }

    compilerArguments.Add(source);
    WriteIfDifferent(Path.Join(output, "libraries.txt"), string.Join(Environment.NewLine, libraries));
    // Compile against the installed headers each publish; no stale ABI objects survive an installation change.
    await RunCompilerAsync(compiler, compilerArguments);
    Console.WriteLine($"Ankus: generated PostgreSQL {major} native boundaries and SQL.");
    return 0;
}
// codeql[cs/catch-of-all-exceptions] MSBuild runs this tool, so any failure becomes one reported error and exit code instead of a crash.
catch (Exception error)
{
    Console.Error.WriteLine($"Ankus build failed: {error}");
    return 1;
}

static async Task RunCompilerAsync(string compiler, IEnumerable<string> arguments)
{
    using var process = new Process { StartInfo = new ProcessStartInfo(compiler) { UseShellExecute = false } };
    foreach (string argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    process.Start();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Native compiler exited with code {process.ExitCode}.");
    }
}

static void WriteIfDifferent(string path, string content)
{
    if (!File.Exists(path) || File.ReadAllText(path) != content)
    {
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
