using System.Runtime.InteropServices;
using Ankus.PgConfig;

namespace Ankus.IntegrationTests;

/// <summary>
/// Compiles the exact emitted value guard with translation-unit-only catalog and lock faults.
/// </summary>
internal static class NativeValueOwnershipFixtureCompiler
{
    /// <summary>
    /// Gets the exact compiled fixture filename used by PostgreSQL.
    /// </summary>
    private static string ModuleFileName { get; } = AllocatorFixtureCompiler.GetModuleFileName("Ankus.ValueOwnershipFixture");

    /// <summary>
    /// Gets the SQL entry point for independently observed native ownership and recovery.
    /// </summary>
    internal static string InstallationSql { get; } = $$"""
        CREATE FUNCTION tests.value_ownership(integer, boolean, boolean, boolean) RETURNS integer[]
        AS '{{ModuleFileName}}', 'ankus_test_value_ownership' LANGUAGE c STRICT;
        """;

    /// <summary>
    /// Builds the production native bridge prefix with real-resource fault controls and no managed exports.
    /// </summary>
    /// <param name="installation">The cluster's selected server and headers.</param>
    /// <param name="cancellationToken">Cancels reads and compilation.</param>
    internal static async Task CompileAsync(PostgresInstallation installation, CancellationToken cancellationToken)
    {
        string root = IntegrationEnvironment.RepositoryRoot;
        string suffix = Path.Join(RuntimeInformation.RuntimeIdentifier, "native", "ankus", "bridge.c");
        string emittedPath = Directory.EnumerateFiles(Path.Join(root, "samples", "Ankus.Examples.Hello", "obj", "Release",
            IntegrationEnvironment.ExtensionTargetFramework),
            "bridge.c", SearchOption.AllDirectories).Single(path => path.EndsWith(suffix, StringComparison.Ordinal));
        string emitted = await File.ReadAllTextAsync(emittedPath, cancellationToken);
        int boundary = emitted.IndexOf("extern int ankus_managed_", StringComparison.Ordinal);
        int guard = emitted.IndexOf("ankus_spi_execute(", StringComparison.Ordinal);
        if (guard < 0 || boundary <= guard)
        {
            throw new InvalidOperationException("The emitted value guard or managed export boundary is absent.");
        }

        string fixtures = Path.Join(root, "tests", "Ankus.IntegrationTests", "Native");
        string prefix = await File.ReadAllTextAsync(Path.Join(fixtures, "value_ownership_prefix.c"), cancellationToken);
        string probe = await File.ReadAllTextAsync(Path.Join(fixtures, "value_ownership_probe.c"), cancellationToken);
        string output = IntegrationEnvironment.NativeOutputDirectory;
        string sourcePath = Path.Join(output, "value_ownership_fixture.c");
        await File.WriteAllTextAsync(sourcePath, prefix + emitted[..boundary] + probe, cancellationToken);
        // The emitted bridge uses the Native AOT toolchain: Clang on Unix, MSVC on Windows.
        await AllocatorFixtureCompiler.CompileModuleAsync(installation, sourcePath,
            Path.Join(output, ModuleFileName), true, OperatingSystem.IsWindows() ? "cl.exe" : "clang", cancellationToken);
    }
}
