using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Compiles test-only native allocator constructors against the cluster's selected PostgreSQL headers.
/// </summary>
internal static class AllocatorFixtureCompiler
{
    /// <summary>
    /// Gets the exact filename shared by native compilation and SQL installation.
    /// </summary>
    private static string ModuleFileName { get; } = GetModuleFileName("Ankus.AllocatorFixture");

    /// <summary>
    /// Gets SQL that installs the native fixture functions in an existing tests schema.
    /// </summary>
    internal static string InstallationSql { get; } = $$"""
        CREATE FUNCTION tests.cstring_argument(regprocedure, boolean) RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_cstring_argument' LANGUAGE c STRICT;
        CREATE FUNCTION tests.buffer_argument(regprocedure, anyelement) RETURNS text[]
        AS '{{ModuleFileName}}', 'ankus_test_buffer_argument' LANGUAGE c STRICT;
        CREATE FUNCTION tests.buffer_invalid_text(regprocedure, integer) RETURNS text[]
        AS '{{ModuleFileName}}', 'ankus_test_buffer_invalid_text' LANGUAGE c STRICT;
        CREATE FUNCTION tests.array_bits(bigint) RETURNS bigint[]
        AS '{{ModuleFileName}}', 'ankus_test_array_bits' LANGUAGE c STRICT;
        CREATE FUNCTION tests.array_storage(regprocedure, anyarray, integer) RETURNS text[]
        AS '{{ModuleFileName}}', 'ankus_test_array_storage' LANGUAGE c STRICT;
        CREATE FUNCTION tests.array_argument(regprocedure, anyarray) RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_array_argument' LANGUAGE c STRICT;
        CREATE FUNCTION tests.array_slice_argument(regprocedure, anyarray) RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_array_slice_argument' LANGUAGE c STRICT;
        CREATE FUNCTION tests.array_slice_bitmap(regprocedure, anyarray) RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_array_slice_bitmap' LANGUAGE c STRICT;
        CREATE FUNCTION tests.array_owner(bigint) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_array_owner' LANGUAGE c STRICT;
        CREATE TABLE tests.relation_locks_1 (x integer);
        CREATE TABLE tests.relation_locks_8 (x integer);
        CREATE FUNCTION tests.relation_commit_fault(regprocedure, oid) RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_relation_commit_fault' LANGUAGE c STRICT;
        CREATE FUNCTION tests.relation_transfer_fault(regprocedure, oid, integer) RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_relation_transfer_fault' LANGUAGE c STRICT;
        CREATE FUNCTION tests.relation_owner(regprocedure, oid, boolean) RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_relation_owner' LANGUAGE c STRICT;
        CREATE FUNCTION tests.relation_borrow(regprocedure, oid, integer) RETURNS boolean
        AS '{{ModuleFileName}}', 'ankus_test_relation_borrow' LANGUAGE c STRICT;
        CREATE FUNCTION tests.relation_stats(bigint) RETURNS bigint[]
        AS '{{ModuleFileName}}', 'ankus_test_relation_stats' LANGUAGE c STRICT;
        CREATE FUNCTION tests.default_values(bigint) RETURNS text[]
        AS '{{ModuleFileName}}', 'ankus_test_default_values' LANGUAGE c STRICT;
        CREATE FUNCTION tests.item_pointer_describe(bigint) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_item_pointer_describe' LANGUAGE c STRICT;
        CREATE FUNCTION tests.item_pointer_borrow(regprocedure, integer) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_item_pointer_borrow' LANGUAGE c STRICT;
        CREATE FUNCTION tests.function_address(regprocedure) RETURNS bigint
        AS '{{ModuleFileName}}', 'ankus_test_function_address' LANGUAGE c STRICT;
        CREATE FUNCTION tests.native_nullable_sum(integer, integer) RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_nullable_sum' LANGUAGE c;
        CREATE FUNCTION tests.internal_invoke(regprocedure, integer) RETURNS bigint
        AS '{{ModuleFileName}}', 'ankus_test_internal_invoke' LANGUAGE c STRICT;
        CREATE FUNCTION tests.internal_set_invoke(regprocedure, regprocedure, boolean, boolean) RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_internal_set_invoke' LANGUAGE c STRICT;
        CREATE FUNCTION tests.allocator_create(integer, regprocedure, text) RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_allocator_create' LANGUAGE c STRICT;
        CREATE FUNCTION tests.allocator_delete() RETURNS void
        AS '{{ModuleFileName}}', 'ankus_test_allocator_delete' LANGUAGE c;
        CREATE FUNCTION tests.allocator_flags() RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_allocator_flags' LANGUAGE c;
        CREATE FUNCTION tests.stringinfo_cursor(bigint, integer) RETURNS integer
        AS '{{ModuleFileName}}', 'ankus_test_stringinfo_cursor' LANGUAGE c STRICT;
        CREATE FUNCTION tests.stringinfo_borrow(regprocedure, integer) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_stringinfo_borrow' LANGUAGE c STRICT;
        CREATE FUNCTION tests.list_describe(bigint) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_list_describe' LANGUAGE c STRICT;
        CREATE FUNCTION tests.list_borrow(regprocedure, integer) RETURNS text
        AS '{{ModuleFileName}}', 'ankus_test_list_borrow' LANGUAGE c STRICT;
        """;

    /// <summary>
    /// Names the compiled artifact explicitly instead of relying on a PostgreSQL version's implied library suffix.
    /// </summary>
    /// <param name="moduleName">The fixture's fixed module basename.</param>
    /// <returns>The exact platform-native library filename.</returns>
    internal static string GetModuleFileName(string moduleName)
        => moduleName + (OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so");

    /// <summary>
    /// Builds the fixture with the host C compiler without adding any production exports.
    /// </summary>
    /// <param name="installation">The installation used to start the backend.</param>
    /// <param name="cancellationToken">Cancels the compiler process.</param>
    internal static async Task BuildAsync(PostgresInstallation installation, CancellationToken cancellationToken)
    {
        string source = Path.Join(IntegrationEnvironment.RepositoryRoot, "tests", "Ankus.IntegrationTests", "Native", "allocator_fixture.c");
        await CompileModuleAsync(installation, source,
            Path.Join(IntegrationEnvironment.NativeOutputDirectory, ModuleFileName), cancellationToken);
    }

    /// <summary>
    /// Compiles one standalone PostgreSQL test module using the selected installation's public headers.
    /// </summary>
    /// <param name="installation">The installation providing native headers and import libraries.</param>
    /// <param name="source">The complete C translation unit.</param>
    /// <param name="outputPath">The resulting shared library path.</param>
    /// <param name="cancellationToken">Cancels compilation.</param>
    internal static async Task CompileModuleAsync(PostgresInstallation installation, string source, string outputPath, CancellationToken cancellationToken)
        => await CompileModuleAsync(installation, source, outputPath, false, cancellationToken);

    /// <summary>
    /// Compiles a native fixture with C11 and the platform extensions required by the selected PostgreSQL headers.
    /// </summary>
    /// <param name="installation">The selected backend installation.</param>
    /// <param name="source">The C translation unit.</param>
    /// <param name="outputPath">The resulting module.</param>
    /// <param name="useC11">Whether to enable the compiler's C11 language mode.</param>
    /// <param name="cancellationToken">Cancels compilation.</param>
    internal static async Task CompileModuleAsync(PostgresInstallation installation, string source, string outputPath, bool useC11, CancellationToken cancellationToken)
        => await CompileModuleAsync(installation, source, outputPath, useC11, null, cancellationToken);

    /// <summary>
    /// Compiles a fixture with an explicit native compiler while preserving every diagnostic and header check.
    /// </summary>
    /// <param name="installation">The selected backend installation.</param>
    /// <param name="source">The C translation unit.</param>
    /// <param name="outputPath">The resulting module.</param>
    /// <param name="useC11">Whether the translation unit requires C11.</param>
    /// <param name="nativeCompiler">The production bridge compiler, or null for the platform fixture default.</param>
    /// <param name="cancellationToken">Cancels compilation.</param>
    internal static async Task CompileModuleAsync(PostgresInstallation installation, string source, string outputPath,
        bool useC11, string? nativeCompiler, CancellationToken cancellationToken)
    {
        string output = Path.GetDirectoryName(outputPath) ?? throw new ArgumentException("The module needs an output directory.", nameof(outputPath));
        Directory.CreateDirectory(output);
        List<string> arguments;
        string compiler;
        if (OperatingSystem.IsWindows())
        {
            compiler = nativeCompiler ?? "cl.exe";
            arguments = ["/nologo", "/std:c11", "/LD", "/O2", "/MD", "/WX",
                "/I" + installation.ServerIncludeDirectory,
                "/I" + installation.IncludeDirectory,
                "/I" + Path.Join(installation.ServerIncludeDirectory, "port", "win32"),
                "/I" + Path.Join(installation.ServerIncludeDirectory, "port", "win32_msvc"),
                "/Fo" + Path.ChangeExtension(outputPath, ".obj"), source, "/link",
                "/OUT:" + outputPath,
                Path.Join(installation.LibraryDirectory, "postgres.lib")];
        }
        else
        {
            compiler = nativeCompiler ?? "cc";
            arguments = [.. await installation.GetPreprocessorArgumentsAsync(cancellationToken),
                "-O2", "-fPIC", "-Wall", "-Wextra", "-Werror",
                "-isystem", installation.ServerIncludeDirectory,
                "-isystem", installation.IncludeDirectory];
            if (OperatingSystem.IsMacOS())
            {
                arguments.AddRange(["-dynamiclib", "-Wl,-undefined,dynamic_lookup"]);
            }
            else
            {
                arguments.Add("-shared");
            }

            arguments.AddRange(["-o", outputPath, source]);
        }

        if (useC11)
        {
            arguments.Insert(0, OperatingSystem.IsWindows() ? "/std:c11" : "-std=gnu11");
        }

        await ProcessRunner.RunCheckedAsync(compiler, arguments, new Dictionary<string, string?>(), cancellationToken,
            workingDirectory: output);
    }
}
