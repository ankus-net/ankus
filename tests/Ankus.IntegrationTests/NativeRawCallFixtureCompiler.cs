using System.Globalization;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Compiles actual generated call bodies into a native fixture used by published managed callbacks.
/// </summary>
internal static class NativeRawCallFixtureCompiler
{
    /// <summary>
    /// Gets the exact filename shared by native compilation and SQL installation.
    /// </summary>
    private static string ModuleFileName { get; } = AllocatorFixtureCompiler.GetModuleFileName("Ankus.RawCallFixture");

    /// <summary>
    /// Gets the SQL entry points for native body addresses, backend state and deliberate errors.
    /// </summary>
    internal static string InstallationSql { get; } = $$"""
        CREATE FUNCTION tests.raw_call_address(integer) RETURNS bigint
        AS '{{ModuleFileName}}', 'ankus_test_raw_call_address' LANGUAGE c STRICT;
        CREATE FUNCTION tests.raw_call_holdoffs() RETURNS bigint
        AS '{{ModuleFileName}}', 'ankus_test_raw_call_holdoffs' LANGUAGE c STRICT;
        CREATE FUNCTION tests.raw_call_error(integer) RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_raw_call_error' LANGUAGE c STRICT;
        CREATE FUNCTION tests.raw_call_lock_held() RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_raw_call_lock_held' LANGUAGE c STRICT;
        CREATE FUNCTION tests.raw_call_control(integer) RETURNS bigint
        AS '{{ModuleFileName}}', 'ankus_test_raw_call_control' LANGUAGE c STRICT;
        CREATE FUNCTION tests.log_arm(text, integer) RETURNS void
        AS '{{ModuleFileName}}', 'ankus_test_log_arm' LANGUAGE c STRICT;
        CREATE FUNCTION tests.log_holdoff() RETURNS bigint
        AS '{{ModuleFileName}}', 'ankus_test_log_holdoff' LANGUAGE c STRICT;
        CREATE FUNCTION tests.log_prefix_arm(text) RETURNS void
        AS '{{ModuleFileName}}', 'ankus_test_log_prefix_arm' LANGUAGE c STRICT;
        CREATE FUNCTION tests.log_prefix_calls() RETURNS bigint
        AS '{{ModuleFileName}}', 'ankus_test_log_prefix_calls' LANGUAGE c STRICT;
        CREATE FUNCTION tests.log_prefix_active() RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_log_prefix_active' LANGUAGE c STRICT;
        CREATE FUNCTION tests.log_prefix_restore() RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_log_prefix_restore' LANGUAGE c STRICT;
        CREATE FUNCTION tests.pending_error_cleanup(text) RETURNS void
        AS '{{ModuleFileName}}', 'ankus_test_pending_error_cleanup' LANGUAGE c STRICT;
        """;

    /// <summary>
    /// Generates selected-header bodies with the production command and compiles their complete native implementation.
    /// </summary>
    /// <param name="installation">The same installation used by the test backend.</param>
    /// <param name="cancellationToken">Cancels collection and compilation.</param>
    internal static async Task CompileAsync(PostgresInstallation installation, CancellationToken cancellationToken)
    {
        string output = IntegrationEnvironment.NativeOutputDirectory;
        string temporary = Path.Join(output, "raw-call-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string symbols = Path.Join(temporary, "symbols.txt");
            await File.WriteAllTextAsync(symbols, "FullTransactionIdFromU64\npg_strtoint32\nOidFunctionCall1Coll\nProcessInterrupts\npfree\n", cancellationToken);
            string helper = Path.Join(IntegrationEnvironment.RepositoryRoot, "src", "Ankus.Build", "bin", "Release", "net10.0", "Ankus.Build.dll");
            await ProcessRunner.RunCheckedAsync("dotnet",
                [helper, "binding-call-sources", symbols, installation.Version.Major.ToString(CultureInfo.InvariantCulture), installation.PgConfigPath, temporary],
                new Dictionary<string, string?>(), cancellationToken);
            string fixture = await File.ReadAllTextAsync(Path.Join(IntegrationEnvironment.RepositoryRoot,
                "tests", "Ankus.IntegrationTests", "Native", "raw_call_fixture.c"), cancellationToken);
            string generated = await File.ReadAllTextAsync(Path.Join(temporary, "native-calls.c"), cancellationToken);
            string source = Path.Join(output, "raw_call_fixture.c");
            await File.WriteAllTextAsync(source, generated + "\n" + fixture, cancellationToken);
            await AllocatorFixtureCompiler.CompileModuleAsync(installation, source,
                Path.Join(output, ModuleFileName), true, cancellationToken);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
