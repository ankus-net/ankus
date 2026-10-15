using System.Runtime.InteropServices;
using Ankus.PgConfig;

namespace Ankus.IntegrationTests;

/// <summary>
/// Compiles the emitted memory bridge with bounded, test-only native allocation failures.
/// </summary>
internal static class AllocatorFaultFixtureCompiler
{
    /// <summary>
    /// Gets the exact filename shared by native compilation and SQL installation.
    /// </summary>
    private static string ModuleFileName { get; } = AllocatorFixtureCompiler.GetModuleFileName("Ankus.AllocatorFaultFixture");

    /// <summary>
    /// Gets the isolated-cluster declaration for completion reporter allocation failures.
    /// </summary>
    internal static string CompletionReportingSql { get; } = $$"""
        CREATE FUNCTION tests.completion_reporting_allocation_fault(integer) RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_completion_reporting_allocation_fault' LANGUAGE c STRICT;
        CREATE FUNCTION tests.completion_reporting_allocation_remaining() RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_completion_reporting_allocation_remaining' LANGUAGE c STRICT;
        CREATE FUNCTION tests.completion_terminal_fault() RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_completion_terminal_fault' LANGUAGE c STRICT;
        """;

    /// <summary>
    /// Gets the declaration for the native registry failure probe.
    /// </summary>
    internal static string InstallationSql { get; } = $$"""
        CREATE FUNCTION tests.function_defaults_fault(integer) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_function_defaults_fault' LANGUAGE c STRICT;
        CREATE FUNCTION tests.item_pointer_fault(integer) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_item_pointer_fault' LANGUAGE c STRICT;
        CREATE FUNCTION tests.allocator_registry_fault(integer) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_allocator_registry_fault' LANGUAGE c STRICT;
        CREATE FUNCTION tests.stringinfo_fault(integer) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_stringinfo_fault' LANGUAGE c STRICT;
        CREATE FUNCTION tests.list_fault(integer) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_list_fault' LANGUAGE c STRICT;
        CREATE FUNCTION tests.worker_allocation_fault(integer, text) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_worker_allocation_fault' LANGUAGE c STRICT;
        {{CompletionReportingSql}}
        """;

    /// <summary>
    /// Extracts exact emitted bridge sections and compiles them with native test controls.
    /// </summary>
    /// <param name="installation">The installation whose backend loads the test module.</param>
    /// <param name="cancellationToken">Cancels source reads and compilation.</param>
    internal static async Task CompileAsync(PostgresInstallation installation, CancellationToken cancellationToken)
    {
        string root = IntegrationEnvironment.RepositoryRoot;
        string suffix = Path.Join(RuntimeInformation.RuntimeIdentifier, "native", "ankus", "bridge.c");
        string emittedPath = Directory.EnumerateFiles(Path.Join(root, "tests", "Ankus.TestExtension", "obj", "Release",
            IntegrationEnvironment.ExtensionTargetFramework),
            "bridge.c", SearchOption.AllDirectories).Single(path => path.EndsWith(suffix, StringComparison.Ordinal));
        string emitted = (await File.ReadAllTextAsync(emittedPath, cancellationToken)).ReplaceLineEndings("\n");
        int preambleEnd = FindBoundary(emitted, "static inline void\nankus_read_buffer(");
        int diagnosticsStart = FindBoundary(emitted, "#include \"utils/memutils.h\"\n#include \"miscadmin.h\"\n#include \"tcop/dest.h\"");
        int terminalStart = FindBoundary(emitted, "static int\nankus_recovery_terminal(");
        int memoryStart = FindBoundary(emitted, "#include <stdint.h>\n#include <stdlib.h>\n#include <string.h>\n#include \"miscadmin.h\"\n" +
            "#include \"access/xact.h\"\n#include \"storage/lock.h\"\n#include \"storage/proc.h\"\n#include \"utils/memutils.h\"");
        int memoryEnd = FindBoundary(emitted, "#include \"access/xact.h\"\n#include \"commands/trigger.h\"\n#include \"storage/lock.h\"\n" +
            "#include \"storage/proc.h\"\n#include \"utils/snapmgr.h\"\n\n" +
            "typedef int (*AnkusTransactionManaged)");
        if (preambleEnd >= diagnosticsStart || diagnosticsStart >= terminalStart || terminalStart >= memoryStart || memoryStart >= memoryEnd)
        {
            throw new InvalidOperationException("The emitted native bridge section order changed.");
        }

        string fixtures = Path.Join(root, "tests", "Ankus.IntegrationTests", "Native");
        string prefix = await File.ReadAllTextAsync(Path.Join(fixtures, "allocator_fault_prefix.c"), cancellationToken);
        string probe = await File.ReadAllTextAsync(Path.Join(fixtures, "allocator_fault_probe.c"), cancellationToken);
        string workerPrefix = await File.ReadAllTextAsync(Path.Join(fixtures, "worker_fault_prefix.c"), cancellationToken);
        string workerProbe = await File.ReadAllTextAsync(Path.Join(fixtures, "worker_fault_probe.c"), cancellationToken);
        string completionPrefix = await File.ReadAllTextAsync(Path.Join(fixtures, "completion_fault_prefix.c"), cancellationToken);
        string completionProbe = await File.ReadAllTextAsync(Path.Join(fixtures, "completion_fault_probe.c"), cancellationToken);
        // Memory callbacks retain terminal intent through their own memory capability, so its recovery entry point is required.
        // Workers and SQL must load the same compiled filename, including its explicit platform suffix.
        string source = emitted[..preambleEnd] + emitted[diagnosticsStart..memoryStart] + prefix + workerPrefix + completionPrefix +
            emitted[memoryStart..memoryEnd] + probe + $"\n#define ANKUS_WORKER_FAULT_LIBRARY \"{ModuleFileName}\"\n" + workerProbe + completionProbe;
        string output = IntegrationEnvironment.NativeOutputDirectory;
        string sourcePath = Path.Join(output, "allocator_fault_fixture.c");
        await File.WriteAllTextAsync(sourcePath, source, cancellationToken);
        await AllocatorFixtureCompiler.CompileModuleAsync(installation, sourcePath,
            Path.Join(output, ModuleFileName), cancellationToken);
    }

    /// <summary>
    /// Requires an unambiguous emitted declaration boundary instead of accepting a partial bridge.
    /// </summary>
    /// <param name="source">The emitted production native source.</param>
    /// <param name="boundary">The exact declaration beginning a section.</param>
    /// <returns>The unique boundary's position.</returns>
    private static int FindBoundary(string source, string boundary)
    {
        int position = source.IndexOf(boundary, StringComparison.Ordinal);
        if (position < 0 || source.IndexOf(boundary, position + boundary.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException($"Expected one emitted native bridge boundary: {boundary}");
        }

        return position;
    }
}
