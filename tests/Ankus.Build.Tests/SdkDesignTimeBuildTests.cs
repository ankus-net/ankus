using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Build.Tests;

/// <summary>
/// Exercises repository dependency preparation through actual editor build entry points.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class SdkDesignTimeBuildTests(TestContext context)
{
    /// <summary>
    /// Editors list a design-time build's high-importance messages as problems, so every SDK command that runs the build
    /// tool logs its progress at low importance there and at high importance in command-line builds.
    /// </summary>
    /// <param name="designTime">Whether the build is an editor's design-time build.</param>
    /// <param name="expected">The expected importance of the build tool's output.</param>
    [TestMethod]
    [DataRow(true, "Low")]
    [DataRow(false, "High")]
    public async Task BuildToolOutputIsQuietInEditors(bool designTime, string expected)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-importance-").FullName;
        try
        {
            string project = Path.Join(directory, "Probe.proj");
            new XDocument(new XElement("Project",
                new XElement("Import", new XAttribute("Project", Path.Join(AppContext.BaseDirectory, "Sdk", "Ankus.props"))))).Save(project);
            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", project, "-nologo", "-nodeReuse:false", "-property:DesignTimeBuild=" + (designTime ? "true" : "false"),
                    "-getProperty:_AnkusToolOutputImportance"], directory, context.CancellationToken);
            Assert.AreEqual(expected, output.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        string[] commands = [.. Directory.EnumerateFiles(Path.Join(AppContext.BaseDirectory, "AnkusSdk", "targets"), "*.targets")
            .SelectMany(static file => XDocument.Load(file).Descendants("Exec"))
            .Where(static exec => !((string?)exec.Attribute("Command") ?? "").Contains("--version", StringComparison.Ordinal))
            .Select(static exec => (string?)exec.Attribute("StandardOutputImportance") ?? "")];
        Assert.IsNotEmpty(commands);
        Assert.IsTrue(commands.All(static importance => importance == "$(_AnkusToolOutputImportance)"),
            "Every SDK command that runs the build tool must use the editor-aware output importance.");
    }

    /// <summary>
    /// A fresh editor load builds its tools and preserves the consumer's nullable settings. Later loads build the tools
    /// again only when a source has changed or an output is missing, so an unchanged repository loads without build
    /// messages, which editors list as problems.
    /// </summary>
    /// <param name="configuration">The selected output configuration.</param>
    /// <param name="nullable">The consumer's nullable annotation setting.</param>
    [TestMethod]
    [DataRow("Debug", "enable")]
    [DataRow("Release", "annotations")]
    public async Task FreshEditorLoadPreparesDependenciesAndCompilerOptions(string configuration, string nullable)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-editor-").FullName;
        try
        {
            string? sdk = Assert.ContainsSingle(typeof(SdkDesignTimeBuildTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(static metadata => metadata.Key == "AnkusTestSdkVersion")).Value;
            Assert.IsNotNull(sdk);
            await File.WriteAllTextAsync(Path.Join(directory, "global.json"),
                JsonSerializer.Serialize(new { sdk = new { version = sdk, rollForward = "disable", allowPrerelease = false } }), context.CancellationToken);
            string[] dependencies = ["Ankus.Runtime", "Ankus.Build", "Ankus.Generators", "Ankus.CodeFixes"];
            foreach (string name in dependencies)
            {
                string dependency = Directory.CreateDirectory(Path.Join(directory, name)).FullName;
                string framework = name is "Ankus.Generators" or "Ankus.CodeFixes" ? "netstandard2.0" : "net10.0";
                new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                    new XElement("PropertyGroup", new XElement("TargetFramework", framework),
                        new XElement("OutputType", name == "Ankus.Build" ? "Exe" : "Library")),
                    new XElement("Target", new XAttribute("Name", "RejectEditorOnlyCompilation"),
                        new XAttribute("BeforeTargets", "CoreCompile"),
                        new XElement("Error", new XAttribute("Condition", "'$(DesignTimeBuild)' == 'true' or '$(SkipCompilerExecution)' == 'true'"),
                            new XAttribute("Text", "Dependencies must produce real assemblies."))),
                    // MSBuild builds the dependencies in parallel, and on Windows two of them appending to one file can
                    // collide, so each records its builds in a file of its own.
                    new XElement("Target", new XAttribute("Name", "RecordBuild"), new XAttribute("AfterTargets", "Build"),
                        new XElement("WriteLinesToFile", new XAttribute("File", Path.Join(directory, "builds", name + ".log")),
                            new XAttribute("Lines", "$(MSBuildProjectName)"))))).Save(Path.Join(dependency, name + ".csproj"));
                await File.WriteAllTextAsync(Path.Join(dependency, "Proof.cs"),
                    "namespace " + name.Replace('.', '_') + " { public static class Proof { public static int Read() => 42; } }",
                    context.CancellationToken);
                if (name == "Ankus.Build")
                {
                    // Supply a compiled companion through the real SDK command boundary;
                    // native header generation is exercised separately by backend tests.
                    await File.WriteAllTextAsync(Path.Join(dependency, "Program.cs"), """
                        public static class Program
                        {
                            public static void Main(string[] args)
                            {
                                string output = args[0] == "binding-sources" ? args[3] : args[1];
                                System.IO.Directory.CreateDirectory(output);
                                string file = args[0] == "binding-sources" ? "native-binding.assembly-name" : "native-binding.assembly-path";
                                string value = args[0] == "binding-sources" ? "Ankus.Generators" : System.Environment.GetEnvironmentVariable("ANKUS_TEST_BINDING_ASSEMBLY");
                                System.IO.File.WriteAllText(System.IO.Path.Join(output, file), value);
                            }
                        }
                        """, context.CancellationToken);
                }
            }

            string project = Path.Join(directory, "Extension.csproj");
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("Nullable", nullable),
                    new XElement("AnkusPostgresMajor", "18"),
                    new XElement("_AnkusBuildTool", Path.Join(directory, "Ankus.Build", "bin", "$(Configuration)", "net10.0", "Ankus.Build.dll")),
                    new XElement("_AnkusNativeEnvironment", "ANKUS_TEST_BINDING_ASSEMBLY=" +
                        Path.Join(directory, "Ankus.Generators", "bin", "$(Configuration)", "netstandard2.0", "Ankus.Generators.dll"))),
                new XElement("ItemGroup", dependencies.Select(name => new XElement("ProjectReference",
                    new XAttribute("Include", Path.Join(name, name + ".csproj")),
                    new XAttribute("ReferenceOutputAssembly", name == "Ankus.Runtime" ? "true" : "false"),
                    new XAttribute("GlobalPropertiesToRemove", "RuntimeIdentifier;SelfContained;PublishAot")))),
                new XElement("Import", new XAttribute("Project", Path.Join(AppContext.BaseDirectory, "Sdk", "Ankus.Development.targets"))),
                new XElement("Import", new XAttribute("Project", Path.Join(AppContext.BaseDirectory, "Sdk", "Ankus.Bindings.targets"))))).Save(project);
            string source = Path.Join(directory, "Probe.cs");
            await File.WriteAllTextAsync(source,
                "public static class Probe { public static string? Read() => (Ankus_Runtime.Proof.Read() + Ankus_Generators.Proof.Read()).ToString(); }",
                context.CancellationToken);
            // Default globs must not also compile the dependency sources into the consumer.
            new XDocument(new XElement("Project", new XElement("PropertyGroup",
                new XElement("DefaultItemExcludes", "$(DefaultItemExcludes);Ankus.*/**")))).Save(Path.Join(directory, "Directory.Build.props"));
            await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["restore", project, "-nologo", "-verbosity:quiet", "-bl:" + Path.Join(directory, "restore-{}.binlog")],
                directory, context.CancellationToken);
            foreach (string name in dependencies)
            {
                Assert.IsFalse(Directory.Exists(Path.Join(directory, name, "bin")), "Restore must leave the dependencies unbuilt.");
            }

            string builds = Path.Join(directory, "builds");
            for (int load = 0; load < 4; load++)
            {
                if (load == 2)
                {
                    // An edited dependency source makes the next load build again.
                    File.SetLastWriteTimeUtc(Path.Join(directory, "Ankus.Generators", "Proof.cs"), DateTime.UtcNow);
                }
                else if (load == 3)
                {
                    // So does a missing output.
                    File.Delete(Path.Join(directory, "Ankus.Build", "bin", configuration, "net10.0", "Ankus.Build.dll"));
                }

                string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                    ["msbuild", project, "-nologo", "-nodeReuse:false", "-target:GetTargetPathWithTargetPlatformMoniker,Compile", "-property:Configuration=" + configuration,
                        "-property:TargetFramework=net10.0", "-property:DesignTimeBuild=true", "-property:BuildProjectReferences=false",
                        "-property:BuildingProject=false", "-property:ProvideCommandLineArgs=true", "-property:SkipCompilerExecution=true",
                        "-property:ContinueOnError=ErrorAndContinue", "-getItem:CscCommandLineArgs,TargetPathWithTargetPlatformMoniker",
                        "-bl:" + Path.Join(directory, "editor-{}.binlog")], directory, context.CancellationToken);
                using JsonDocument result = JsonDocument.Parse(output);
                string[] arguments = [.. result.RootElement.GetProperty("Items").GetProperty("CscCommandLineArgs").EnumerateArray()
                    .Select(static item => item.GetProperty("Identity").GetString()!)];
                CSharpCommandLineArguments parsed = CSharpCommandLineParser.Default.Parse(arguments, directory, null);
                Assert.AreEqual(nullable == "enable" ? NullableContextOptions.Enable : NullableContextOptions.Annotations,
                    parsed.CompilationOptions.NullableContextOptions);
                Assert.IsEmpty(parsed.Errors);
                JsonElement[] outputs = [.. result.RootElement.GetProperty("Items").GetProperty("TargetPathWithTargetPlatformMoniker").EnumerateArray()];
                Assert.ContainsSingle(outputs.Where(static item => item.GetProperty("Identity").GetString()!.EndsWith("Extension.dll", StringComparison.Ordinal)));
                JsonElement companion = Assert.ContainsSingle(outputs.Where(static item => item.GetProperty("Identity").GetString()!.EndsWith("Ankus.Generators.dll", StringComparison.Ordinal)));
                Assert.AreEqual("true", companion.GetProperty("AnkusBindingReference").GetString());
                foreach (string name in dependencies)
                {
                    string framework = name is "Ankus.Generators" or "Ankus.CodeFixes" ? "netstandard2.0" : "net10.0";
                    Assert.IsTrue(File.Exists(Path.Join(directory, name, "bin", configuration, framework, name + ".dll")),
                        "The editor must receive a real dependency built for its own framework: " + name);
                }

                CSharpCompilation compilation = CSharpCompilation.Create("EditorProof",
                    [CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(source, context.CancellationToken), parsed.ParseOptions, source,
                        cancellationToken: context.CancellationToken)],
                    parsed.MetadataReferences.Select(reference => MetadataReference.CreateFromFile(reference.Reference)), parsed.CompilationOptions);
                Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning));
                Assert.IsFalse(File.Exists(Path.Join(directory, "bin", configuration, "net10.0", "Extension.dll")),
                    "The editor must compile dependencies without building the extension itself.");
                Assert.AreEqual(dependencies.Length * (load == 0 ? 1 : load), await CountBuildsAsync(builds),
                    $"Load {load} built the dependencies an unexpected number of times.");
            }

            // Editors list a design-time build's high-importance messages, warnings and errors as problems. Minimal
            // verbosity shows exactly those, so a load that rebuilds every dependency must print nothing.
            File.SetLastWriteTimeUtc(Path.Join(directory, "Ankus.Runtime", "Proof.cs"), DateTime.UtcNow);
            string visible = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", project, "-nologo", "-nodeReuse:false", "-tl:off", "-verbosity:minimal", "-target:ResolveReferences",
                    "-property:Configuration=" + configuration, "-property:TargetFramework=net10.0", "-property:DesignTimeBuild=true",
                    "-property:BuildProjectReferences=false", "-property:BuildingProject=false"], directory, context.CancellationToken);
            Assert.AreEqual("", visible.Trim());
            Assert.AreEqual(dependencies.Length * 4, await CountBuildsAsync(builds),
                "The silent load must still have rebuilt the dependencies.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Counts the dependency builds recorded so far, one line per build in each dependency's log.
    /// </summary>
    private async Task<int> CountBuildsAsync(string builds)
    {
        int count = 0;
        foreach (string log in Directory.EnumerateFiles(builds, "*.log"))
        {
            count += (await File.ReadAllLinesAsync(log, context.CancellationToken)).Length;
        }

        return count;
    }
}
