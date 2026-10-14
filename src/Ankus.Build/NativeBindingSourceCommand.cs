using System.Globalization;
using System.Security;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Emits managed declarations only after compiling and validating the selected native header contract.
/// </summary>
internal static class NativeBindingSourceCommand
{
    private const string Project = """
        <Project>
          <PropertyGroup>
            <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
            <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
          </PropertyGroup>
          <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
          <PropertyGroup>
            <TargetFramework>$(AnkusBindingTargetFramework)</TargetFramework>
            <AssemblyName>$(AnkusBindingAssemblyName)</AssemblyName>
            <RootNamespace>Ankus.Postgres</RootNamespace>
            <OutputType>Library</OutputType>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
            <Nullable>enable</Nullable>
            <IsAotCompatible>true</IsAotCompatible>
            <EnableNETAnalyzers>true</EnableNETAnalyzers>
            <AnalysisLevel>latest-recommended</AnalysisLevel>
            <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
            <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            <MSTestAnalysisMode>All</MSTestAnalysisMode>
            <GenerateDocumentationFile>true</GenerateDocumentationFile>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <Deterministic>true</Deterministic>
            <Version>1.0.0</Version>
            <AssemblyVersion>1.0.0.0</AssemblyVersion>
            <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
            <PathMap>__ANKUS_PATH_MAP__</PathMap>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="native-binding.g.cs" />
            <Reference Include="Ankus.Runtime">
              <HintPath>$(AnkusRuntimeAssembly)</HintPath>
              <Private>false</Private>
            </Reference>
          </ItemGroup>
          <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
        </Project>
        """;

    /// <summary>
    /// Measures the selected headers and writes the managed companion source and assembly identity.
    /// </summary>
    /// <param name="arguments">The layout command's arguments, followed by optional Clang, libclang and shared cache paths.</param>
    /// <param name="cancellationToken">Cancels probing and source emission.</param>
    internal static async Task RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is < 3 or > 10)
        {
            throw new ArgumentException("Expected binding-sources <major> <pg_config> <output-directory> [compiler] [windows-library-directories] [runtime-identifier] [target-triple] [clang] [libclang] [cache-directory].", nameof(arguments));
        }

        cancellationToken.ThrowIfCancellationRequested();
        // Clang is already required for semantic collection. Use it for default native
        // verification too; explicitly selected native compilers keep their own checks.
        if (arguments.Length == 3)
        {
            arguments = [.. arguments, OperatingSystem.IsWindows() ? "cl.exe" : "clang"];
        }
        else if (arguments[3].Length == 0)
        {
            arguments = [.. arguments];
            arguments[3] = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
        }

        int major = int.Parse(arguments[0], NumberStyles.None, CultureInfo.InvariantCulture);
        string cache = arguments.Length == 10 && arguments[9].Length != 0 ? Path.GetFullPath(arguments[9])
            : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ankus", "bindings");
        await NativeBindingSourceCache.CopyAsync(major,
            arguments.Length == 10 ? arguments[..9] : arguments, cache, cancellationToken);
        string output = Path.GetFullPath(arguments[2]);
        await WriteIfChangedAsync(Path.Join(output, "Ankus.NativeBindings.csproj"), CreateProject(output), cancellationToken);
        string assembly = (await File.ReadAllTextAsync(Path.Join(output, "native-binding.assembly-name"), cancellationToken)).Trim();
        Console.WriteLine($"Managed bindings: {assembly}");
    }

    /// <summary>
    /// Creates the isolated deterministic project used by local and shared compiled companions.
    /// </summary>
    internal static string CreateProject(string directory)
    {
        string output = Path.GetFullPath(directory);
        string pathMap = string.Join(',', new[] { output, NativeBuildDirectory.PhysicalPath(new DirectoryInfo(output)) }
            .Distinct(StringComparer.Ordinal).Select(static path => path.Replace(",", ",,", StringComparison.Ordinal)
                .Replace("=", "==", StringComparison.Ordinal) + "=/_/Ankus.Postgres"));
        string project = Project.Replace("__ANKUS_PATH_MAP__", SecurityElement.Escape(EscapeProperty(pathMap)), StringComparison.Ordinal);
        return project.ReplaceLineEndings("\n") + "\n";
    }

    /// <summary>
    /// Preserves literal paths and values through MSBuild's property expression parser.
    /// </summary>
    internal static string EscapeProperty(string value)
    {
        var escaped = new StringBuilder();
        foreach (char character in value)
        {
            if (character is '%' or '$' or '@' or ';' or '\'' or '(' or ')' or '*' or '?')
            {
                escaped.Append('%').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                escaped.Append(character);
            }
        }

        return escaped.ToString();
    }

    private static async Task WriteIfChangedAsync(string path, string value, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || await File.ReadAllTextAsync(path, cancellationToken) != value)
        {
            await File.WriteAllTextAsync(path, value, cancellationToken);
        }
    }
}
