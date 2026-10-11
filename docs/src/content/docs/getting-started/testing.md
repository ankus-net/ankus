---
title: Test an extension
description: Run managed and PostgreSQL tests with ordinary dotnet test.
---

From a solution created by `ankus new`:

```console
dotnet test
```

`ankus new Hello --test-framework xunit` and `--test-framework nunit` generate
xUnit.net v3 and NUnit test projects. The installed `dotnet new ankus` and
`ankus-worker` templates accept the same options. MSTest remains the default.
All three frameworks discover individual backend cases, honor `IgnoreReason`
and use an asynchronous fixture to dispose the cluster and published extension.
Reports retain each backend test's managed identity; xUnit also shows its theory
arguments after that name.

The MSTest project contains two kinds of tests. Managed tests call ordinary C#
methods directly. Backend tests publish the extension with Native AOT, start an
isolated PostgreSQL cluster, and exercise its generated SQL functions.

Backend testing requires PostgreSQL with server headers and the
[Native AOT toolchain](https://learn.microsoft.com/dotnet/core/deploying/native-aot/).
Extension builds also require LLVM Clang 20 or later with matching `libclang`;
see [publishing prerequisites](/getting-started/publishing/#prerequisites).
The fixture uses the extension project's evaluated `AnkusPostgresMajor` and
`AnkusPgConfigPath`, including imported and configuration-specific properties.
Without a version setting, it discovers PostgreSQL 18. Register a nonstandard
installation with `ankus init --pg18 /path/to/pg_config`. Missing prerequisites
fail initialization; tests are never silently skipped.

Plain `dotnet test -p:AnkusPostgresMajor=17` selects matching headers and a
PostgreSQL 17 server. Use `-p:AnkusPgConfigPath=/path/to/pg_config` for a specific
installation; its reported major is used when `AnkusPostgresMajor` is absent.
The testing package carries these build settings into the test host; `--no-build`
retains the settings from its previous build. An explicit
fixture `Installation` or `ANKUS_TEST_PG_CONFIG` takes
precedence. A selected path that reports a different requested major is rejected.

Use `ankus test --pg 17` to build and test against another registered version,
or `ankus test --all` to run against every registered major. Pass normal runner
arguments after `--`, for example:

```console
ankus test --pg 18 -- --filter "FullyQualifiedName~BackendTests" --report-trx
```

The command selects matching headers and servers, keeps reports per major, and
cleans up fixtures after an aborted host. See [test command options](/reference/cli/#run-extension-tests).

For SQL files with expected text output, use `ankus regress --pg 18`.
The [SQL regression guide](/reference/cli/#run-sql-regression-suites) covers
PostgreSQL's native comparator, setup scripts, and reviewing changed expectations.
On supported Unix systems, `ankus regress --pg 18 --valgrind` runs the server
under Memcheck. See [native memory diagnostics](/reference/cli/#inspect-native-memory-with-valgrind)
for prerequisites and reading the server log.

## Declare tests inside the extension

Use `[PgTest]` on a synchronous static `void` method in a partial class.
The method and its containing classes must be accessible from generated code
in the extension assembly: `public`, `internal`, or `protected internal` for
members and nested classes.

```csharp
using Ankus;

public static partial class BackendChecks
{
    [PgTest]
    public static void AdditionInsidePostgres()
        => PgAssert.AreEqual(42, Spi.ExecuteScalar<int>("SELECT 19 + 23"));

    [PgTest(ExpectedError = "expected failure")]
    public static void ExpectedFailure()
        => throw new InvalidOperationException("expected failure");
}
```

The method body executes inside the PostgreSQL backend. You can use SPI,
memory contexts and other backend APIs there. Test frameworks do not load inside
an extension, so check results with `PgAssert`, as pgrx tests use `assert_eq!`.
A failed check names the expected and actual values. `PgAssert.ThrowsSqlState`
runs its work in a subtransaction and returns the PostgreSQL error, so the test
can inspect it and keep going in the same transaction:

```csharp
[PgTest]
public static void DivisionByZeroIsReported()
{
    PgException error = PgAssert.ThrowsSqlState(PgSqlStates.DivisionByZero,
        () => Spi.ExecuteScalar<int>("SELECT 1 / 0"));
    PgAssert.AreEqual("division by zero", error.Message);
}
```
 Methods accept no SQL arguments;
injected `PgFunctionContext` and `PgMemoryContext` parameters remain available.
Methods and containing classes must be accessible to generated code and cannot
be generic. Every containing class must be partial. Invalid declarations produce
an error at the declaration or option that needs correction; valid sibling tests
and production exports remain available.

`BackendChecks.PostgresTests.Cases` contains immutable `PgTestCase` values.
Reading this catalog does not run the enclosing class's static constructor or
its test methods. Reserve the nested name `PostgresTests` for generated code.

The scaffold connects these cases to MSTest's ordinary data-driven tests:

```csharp
public static IEnumerable<TestDataRow<PgTestCase>> NativeCases
    => BackendChecks.PostgresTests.Cases.Select(test => new TestDataRow<PgTestCase>(test)
    {
        DisplayName = test.Name,
        IgnoreMessage = test.IgnoreReason,
    });

[TestMethod]
[DynamicData(nameof(NativeCases))]
public Task DeclaredTestsExecuteInPostgres(PgTestCase test)
    => extension.RunTestAsync(test, context.CancellationToken);
```

Start `extension` once during test-class initialization with test publication
enabled:

```csharp
extension = await PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions
{
    ProjectPath = projectPath,
    IncludeTests = true,
    PostgreSqlConfiguration = ["my_extension.mode = 'test'"],
}, context.CancellationToken);
```

Dispose it during test-class cleanup. The options also select `Installation`,
`Configuration` (default `Release` for direct fixtures, or the configuration
selected by `ankus test`), `BuildProperties`, `SharedPreload` and `Port`.
`BuildProperties` participate in project selection and the native publication.
When invoked through `ankus test`, forwarded MSBuild properties become the
default so the managed test build and nested fixture publish use the same values.
The fixture uses the selected installation's headers and server together.

For ordinary `dotnet test`, set `DataDirectoryBase` to put cluster data beneath
a chosen directory. The fixture creates and removes its own unique child;
your parent directory and other contents remain. With `ankus test`, use
`--pgdata ./test-data` to choose the base for every fixture in the command.
Server logs remain in the extension project's `bin/ankus-test-logs/` directory.

Set `ReuseSchema = true` to reuse the last successfully published installation
SQL and its embedded metadata while recompiling function bodies. With
`ankus test --no-schema`, this is the default; an explicit fixture option takes
precedence. Run normally first to generate the schema for the selected
configuration, PostgreSQL major and test-publication mode. Changes to native
declarations require another normal run. SQL-only changes take effect when
schema generation is enabled again.

Each case runs in its own transaction, which rolls back after success, failure
or cancellation. `ExpectedError` matches the server's primary message exactly;
a different error or unexpected success fails the test. Failure diagnostics
include the original managed name, database exception and backend session log.
`[PgTest(IgnoreReason = "reason")]` supplies an explicit framework skip reason.
Directly invoking an ignored case fails; the host must report the skip.

Set `PgTest.SearchPath` to control name resolution inside a backend test. For
example, `[PgTest(SearchPath = ["pg_catalog", PgSearchPath.ExtensionSchema, "pg_temp"])]`
uses the extension's installation schema. PostgreSQL restores the caller's path
when the function returns or raises an error. An empty array clears the path;
null preserves the caller's setting. The reserved extension-schema token makes
test publications non-relocatable, just as it does for
[ordinary functions](/function-declarations/#execution-options). Normal publications
exclude backend test functions and their search paths.

Names in reports retain their C# identity. Generated SQL names fit PostgreSQL's
identifier limit, even for long C# method names. An explicit `[PgSchema]` selects
the function schema; otherwise the fixture uses the installed extension schema,
including a schema supplied by its control file.

Normal `dotnet publish` excludes test functions. The fixture explicitly sets
`AnkusIncludeTests=true` only when `IncludeTests` is enabled, which also defines
the `ANKUS_TESTS` compilation symbol. Put SQL functions that only backend tests
call inside `#if ANKUS_TESTS`, as pgrx code uses `#[cfg(feature = "pg_test")]`. Other test hosts
can consume the same framework-neutral catalog and fixture. Use
[`[PgBenchmark]` and `ankus bench`](/benchmarks/) for measured backend work.

## Property tests

`PgPropertyRunner` checks a condition against many generated inputs inside the
backend, as pgrx's `PgTestRunner` does with proptest:

```csharp
[PgTest]
public static void DatesRoundTrip()
    => new PgPropertyRunner().Run(
        PgGenerators.Number<int>().Select(PgDate.FromRawSaturating),
        date => PgAssert.AreEqual(date, Spi.ExecuteScalar<PgDate>("SELECT $1", SpiParameter.Create(date))));
```

The test throws to fail. Each input runs in its own subtransaction, so a
PostgreSQL error fails that input and rolls back its work while the run
continues; passing inputs keep their changes until the test's transaction rolls
back. Query cancellation stops the run.

When an input fails, the runner shrinks it before reporting: numbers move toward
zero, text and arrays lose elements, and choices move toward the first
alternative. The `PgPropertyException` names the smallest failing input, its
error and the seed. Set `PgPropertyOptions.Seed` to replay the same inputs.
`Cases` defaults to 256 as in proptest.

`PgGenerators` provides integers of any width with `Number<T>()`, floating-point
values including NaN and the infinities, text, arrays, booleans, SQL NULL and
fixed choices. Compose them with `Select`, `Where` and LINQ query syntax; the
results still shrink.

## Declaration diagnostics

Backend-test errors identify the requirement and its authored source.
Correct the highlighted method, containing class, conflicting attribute or
metadata value. Each valid test keeps its discovery catalog and normal native
publication behavior even when another declaration is invalid.

| Diagnostic | Requirement |
| --- | --- |
| `ANKUS291` | PostgreSQL backend test must be static. |
| `ANKUS292` | PostgreSQL backend test must be synchronous. |
| `ANKUS293` | PostgreSQL backend test cannot be generic. |
| `ANKUS294` | PostgreSQL backend test must have an implementation. |
| `ANKUS295` | PostgreSQL backend test must return void. |
| `ANKUS296` | PostgreSQL backend test must be accessible. |
| `ANKUS297` | PostgreSQL backend test cannot have SQL arguments. |
| `ANKUS298` | PostgreSQL backend test context must be passed by value. |
| `ANKUS299` | PostgreSQL backend test has a conflicting role. |
| `ANKUS300` | PostgreSQL backend test requires a class. |
| `ANKUS301` | PostgreSQL backend test cannot have a generic container. |
| `ANKUS302` | PostgreSQL backend test cannot be file-local. |
| `ANKUS303` | PostgreSQL backend test container must be accessible. |
| `ANKUS304` | PostgreSQL backend test container must be partial. |
| `ANKUS305` | PostgreSQL backend test owner has a reserved name. |
| `ANKUS306` | PostgreSQL backend test catalog name is already declared. |
| `ANKUS307` | Invalid PostgreSQL backend test expected error. |
| `ANKUS308` | Invalid PostgreSQL backend test ignore text. |
| `ANKUS309` | PostgreSQL backend test requires a nonempty ignore reason. |

Editors offer **Make static** for `ANKUS291`, **Make internal** for `ANKUS296`,
`ANKUS302` and `ANKUS303`, **Make partial** for `ANKUS304`, and **Pass by value**
for an `in` or `ref readonly` context (`ANKUS298`).

`ExpectedError = null` means successful execution is expected; an empty string
remains an exact expected message. `IgnoreReason = null` runs the test, while a
non-null reason must explain its omission. Both options preserve exact Unicode
and embedded quotes, and reject zero characters or unpaired surrogates.

## Use the fixture

[`PostgresExtensionTest`](/api/ankus.testing.postgresextensiontest/) is included
in the `Ankus.Testing` package:

```csharp
await using PostgresExtensionTest extension = await PostgresExtensionTest.StartAsync(
    projectPath, cancellationToken: context.CancellationToken);

await extension.Cluster.RunInTransactionAsync("addition", async (connection, transaction, token) =>
{
    await using var command = new NpgsqlCommand("SELECT public.add(19, 23)", connection, transaction);
    Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
}, context.CancellationToken);
```

The fixture passes the same PostgreSQL installation to native compilation and
cluster startup, then executes `CREATE EXTENSION ... CASCADE`, so extensions
that your control file `requires` are created first when the installation
provides them. It leaves the selected installation untouched. Supply an explicit `PostgresInstallation` to test a
particular version.

If your control file declares a custom SQL `directory`, the fixture remaps it
inside its temporary storage. It stages the declared installation SQL, upgrades
and secondary controls without changing your author file or writing to its
original destination, including absolute paths.

For workers and shared-memory extensions, enable shared preload in the fixture:

```csharp
await using PostgresExtensionTest extension = await PostgresExtensionTest.StartAsync(
    projectPath, sharedPreload: true, cancellationToken: context.CancellationToken);
```

The fixture loads the published library before starting backends, then creates
the extension's SQL objects. `ankus new MyWorker --background-worker` generates
this setup and a test that observes a worker running in a separate process.

`RunInTransactionAsync` opens a connection and rolls back when the callback
finishes, including after assertion failures. Parallel tests use independent
connections. The generated fixture shares one cluster for its test class and
disposes it after all tests finish.

## Choose a test port

Fixtures choose an available port automatically so independent test runs can
start in parallel. For a predictable port while debugging, save a test base
with `ankus init --pg18 /path/to/pg_config --base-testing-port 33000`, then
request the resolved port explicitly:

```csharp
var registry = new PostgresRegistry();
PostgresInstallation installation = await registry.GetAsync(18, context.CancellationToken);
await using PostgresExtensionTest extension = await PostgresExtensionTest.StartAsync(
    projectPath, sharedPreload: false, port: registry.GetTestPort(18),
    installation: installation, cancellationToken: context.CancellationToken);
```

This starts the fixture on `33018`. You can also pass a literal port from `1`
through `65535`, or set `PostgresTestClusterOptions.Port` when creating a cluster
directly. An occupied explicit port fails startup; it never switches to another
port. Use different ports for concurrent fixtures, or leave port selection automatic.

## Failures and cleanup

Unhandled managed exceptions become PostgreSQL errors. The generated recovery
test uses a savepoint to recover from an expected error and verifies the next
query on the same connection.

Build logs and PostgreSQL logs remain under the extension project's
`bin/ankus-test-logs/`. A failed test's exception carries its session's log
lines and, when its backend died, the server's report about that process. The
cluster and temporary published library are removed on disposal. A failed build or extension load fails initialization and cleans up
the resources it created.

While a test runs, messages it raises that contain `TMSG: ` go to the test's
output as they arrive, as in pgrx. Use `PgLog.Info("TMSG: ...")` to trace a
backend test; other messages stay in the server log.

On Windows, the fixture also collects its server's Windows Event Log messages.
Older PostgreSQL versions use that destination when the test host runs as a
service. The test account needs read access to the Application event log;
each cluster's diagnostics remain separate and are retained in its server log.
Call `ReadServerLog()` to refresh that file while a Windows cluster is running.
Disposing the cluster also collects its shutdown messages.

Test clusters run with `fsync = off`, as PostgreSQL's own test clusters do.
PostgreSQL crash recovery still works, because the operating system keeps the
written WAL; only durability across an operating system crash is given up. Add
`fsync = on` to `PostgreSqlConfiguration` to test behavior that depends on it.

Set `UseValgrind` on the fixture or cluster options, or run `ankus test
--valgrind`, to run the server under Valgrind's Memcheck on Linux or macOS.

Like pgrx's, test clusters log messages at `info` and statements that take over a
second, and use a C collation with UTF-8 encoding. Unlike pgrx, they do not log
every statement; add `log_statement = 'all'` to `PostgreSqlConfiguration` when a
failure report should list each statement. Settings you supply follow the
defaults, so they win.

`StartupTimeout` bounds initialization, server start and creation of the test
database together. Its 180-second default matches PostgreSQL's own TAP test
timeout, so a busy shared disk does not fail an otherwise healthy startup.

If another process takes an automatically selected TCP port during startup, the fixture
retries with a new isolated cluster and port. It allows up to three attempts
within the original startup timeout, retaining each failed attempt's log and
removing its data and sockets. Configuration errors and other startup failures
are reported immediately; cancellation stops further attempts.

PostgreSQL 18 uses its per-cluster extension search path. For earlier versions,
the fixture makes and removes an isolated copy of the installation.
