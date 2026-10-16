# Development and testing

The [.NET compatibility and runtime servicing plan](dotnet-support.md) defines
SDK selection, .NET 11 acceptance and maintenance of the patched Native AOT
runtime. Repository builds stay on stable .NET 10 SDKs until a newer major is
explicitly validated.

Install the stable .NET SDK selected by `global.json`, the platform's
[Native AOT toolchain](https://learn.microsoft.com/dotnet/core/deploying/native-aot/),
and PostgreSQL 18 with server development headers. Windows also needs the server
import library. The repository's collation tests require PostgreSQL built with
ICU support.

Linux integration tests also require Valgrind and matching system-library debug
symbols. On Debian/Ubuntu, install `valgrind` and `libc6-dbg`. The tests start real
instrumented PostgreSQL servers, execute a Native AOT extension, and verify
failure cleanup and retained data. A missing tool is a failed prerequisite.

Build-tool tests compile standalone C layout and signature probes, including
deliberately incompatible prototypes which must fail compilation. Header-type
collection also uses Clang's structured AST and checks the packaged command against
the selected PostgreSQL installation. Header collection requires LLVM Clang 20
or later with the `-skip-function-bodies` frontend option; the platform's default
Clang may be older. Make both `cc` and a supported `clang` on Linux/macOS or
`clang-cl.exe` on Windows available on `PATH`. Windows also uses `cl.exe` from
Visual Studio 2022 17.9 or later for the selected PostgreSQL header probe. Its
[`__typeof__` support](https://learn.microsoft.com/cpp/c-language/typeof-c)
lets the probe measure anonymous native values with a type operand to MSVC's
alignment operator. Standalone probes use LLVM's `llvm-objcopy.exe`
after compilation to separate backend header implementations
from probe code. References from probe code to removed definitions or missing
dependencies still fail. Put LLVM's `bin` directory on
`PATH`. A Visual Studio Developer Command Prompt supplies headers and libraries.
Install the Visual Studio Clang tools or LLVM for the independent
standard C fixture and the header-type collector.

Windows CI configures the Visual Studio developer environment once and sets
`IlcUseEnvironmentalTools=true` for child builds. Native AOT uses its selected
`PATH`, `INCLUDE` and `LIB`; Ankus passes the same library roots to its native
header and ABI checks. This avoids repeating Visual Studio discovery for each
consumer project. Ordinary builds retain the SDK's default discovery behavior.
Windows redirected process pipes use synchronous handles. Native-tool calls that
do not require read cancellation drain those pipes on dedicated readers and join
them before disposing the child process. This prevents an idle diagnostic pipe
from occupying the thread-pool capacity needed to drain compiler output when a
build has a small processor budget. Cancellable reads retain stream cancellation;
compiler output retains its byte limit.

Hosted CI selects LLVM 20 on Linux/macOS. Dedicated Linux/macOS CI uses its installed
Clang and matching libclang; Windows uses its installed LLVM frontend. Each
frontend is verified before running the suite. On macOS, Homebrew's `llvm` formula supplies
the required compiler; put its `bin` directory on `PATH` for the test process.
Linux packages are available from [LLVM's package repository](https://apt.llvm.org/).
SDK extension builds and the transitive record collector need the matching `libclang` library;
install `libclang-20-dev` beside `clang-20` on Linux. Homebrew's LLVM formula and
the Windows LLVM installer include it. The collector uses the compiler's reported
resource directory to find its library, including when the compiler is a shim.
It loads a serialized declaration AST from that exact compiler in a separate
worker process; incompatible compiler/library builds fail explicitly.

The weekly PostgreSQL version workflow uses a separate Linux runner for complete
suites against PostgreSQL 13–17 and 19. It can also select PostgreSQL 18 manually.
Each installation needs matching server headers, ICU and the same native
toolchain prerequisites. See [engineering apps](../../eng/README.md) for the
preflight command, runner isolation and retained evidence. A scheduled matrix
does not replace successful full-suite results for each supported target.

The weekly `PostgreSQL platform versions` workflow adds complete macOS ARM64
suites for PostgreSQL 13 through 17 and 19, and Windows x64 suites for
PostgreSQL 13 through 16 and 18. The macOS 15 and 16 cells cover the extension
library suffix change from `.so` to `.dylib`.
These jobs use the dedicated platform runners and the same full suite as primary
CI. Install the selected servers and configure the Windows installation roots
before dispatch; see [engineering prerequisites](../../eng/README.md#additional-platform-and-version-prerequisites).

The `header-frontend-check` engineering command checks a compiler's version and
required option without modifying the machine; see [engineering apps](../../eng/README.md).

On macOS, native compilation uses the active developer tools’ SDK from `xcrun`,
or an explicit absolute `SDKROOT` path. PostgreSQL’s recorded SDK path may belong
to its package build machine; Ankus replaces that path while retaining its
include directories and definitions. The same selection applies to bindings,
layout probes and native extension code.

## PostgreSQL discovery

Ankus checks the Ankus home's `config.json` and `postgres/` directory, then `PATH`
and conventional installation directories. `ANKUS_HOME` selects the home,
defaulting to `~/.ankus`. For a nonstandard path:

```json
{
  "pg18": "/path/to/postgresql/bin/pg_config"
}
```

## Tests

### Stage the patched Native AOT runtime

A checkout needs the patched runtime and compiler before building or running
backend tests. When the runtime version it selects has been released, restore
downloads them from nuget.org and nothing needs staging. After the runtime fork
moves past the last release, stage them as below; without a staged or published
payload, restore fails with `NU1101` for `Ankus.NativeAot.Runtime.<rid>` or
`Ankus.NativeAot.Compiler.<rid>`.

Read the checkout's runtime identity and find a successful CI run for the same
Ankus revision:

```console
dotnet run --file eng/Ankus.Ci.cs -- runtime-info
git rev-parse HEAD
gh run list --workflow ci.yml --commit <ankus-commit> --status success
```

Download that run's matching platform artifact into the repository. For Linux
x64:

```console
gh run download <run-id> --name nativeaot-linux-x64 --dir artifacts/nativeaot/linux-x64
dotnet run --file eng/Ankus.Ci.cs -- runtime-pack linux-x64
```

Use `win-x64`, `osx-arm64` or `osx-x64` for the corresponding platform, replacing
the artifact name, directory and command argument together. Primary CI produces
the first three; the additional-platforms workflow produces `osx-x64`. Artifacts
expire, so an older checkout may need a new workflow run. The staged directory
must contain `aotsdk/System.Private.CoreLib.dll`, the compiler and its native
libraries under `compiler/`, and runtime license files under `source/`.
The repository SDK automatically selects these payloads. `runtime-pack` restores
the compiler's executable permission on Unix and creates matching runtime and
compiler packages in `artifacts/packages` for external package consumers.

To build it locally instead, use a separate writable `runtime/` checkout of the
repository and commit printed by `runtime-info`; reference clones stay read-only.
Then run `dotnet run --file eng/Ankus.Ci.cs -- runtime-build linux x64 linux-x64`
on Linux, `runtime-build osx arm64 osx-arm64` on Apple Silicon,
`runtime-build osx x64 osx-x64` on Intel macOS, or
`runtime-build windows x64 win-x64` on Windows. Use a host with the package's
operating system and architecture. See
[engineering prerequisites](../../eng/README.md) for the native toolchain and
runtime build commands. Keep the runtime payload and compiler versions matched
to the selected Ankus revision.

### Run the suite

From the repository root:

```console
dotnet test
```

The suite uses MSTest with Microsoft.Testing.Platform. The integration fixture
publishes the extensions, starts an isolated cluster, installs the extensions,
and invokes their functions through SQL. Test transactions roll back before their
connections close. The cluster shuts down after the run.

Tests that intentionally crash PostgreSQL reserve one crash-recovery lease before
starting their isolated cluster and release it after the cluster stops. This keeps
restarts from competing for the host's disk and crash-reporting resources while
ordinary integration cases continue in parallel. Crash cases retain their
thirty-second deadline, real fsync, diagnostic and durability checks. Shared-database
tests that change global schema state retain their separate MSTest exclusion.

The optional xUnit and NUnit consumer templates use their selected framework's
own test packages. Those consumer test dependencies and their template
acceptance tests are an approved exception to the Microsoft/.NET package rule.
This exception does not expand the runtime, generator or tooling dependency
policy. Repository tests continue to use MSTest and its enforced analyzers.

The complete suite also publishes independent installed-package consumers in
temporary directories. Allow disk space for concurrent native builds. On Linux,
if `/tmp` is a small RAM-backed filesystem, set `TMPDIR` for the test process to
a directory on a disk-backed filesystem outside the checkout. Test consumers
must not inherit repository build files or Git context. They are created beside
the checkout, or beneath `RUNNER_TEMP` when it is set. For a checkout nested in
another repository, such as a worktree inside the main checkout, set
`RUNNER_TEMP` to a short directory outside both checkouts; PostgreSQL Unix
socket paths beneath it must stay within 107 bytes. Build outputs and NuGet extraction
need that capacity even though the fixture removes its temporary directories
after the run. A full temporary filesystem is a failed prerequisite, not a
reason to skip consumer tests.

The six worker-cancellation cases share one immutable installed-package
publication; the two lightweight-lock interrupt cases share another. Each case
still starts its own PostgreSQL cluster and runs its complete cancellation,
terminal-error and recovery checks. The class fixture
owns the publication until every case finishes; cancellation of one case stops
that case's wait without interrupting another case's build. Tests of publishing,
source edits and cold package restore continue to own separate projects.

Other installed-tool cases share work only when their inputs are identical. The
two extension search-path schemas read one publication and one rejected
relocatable publication. The tool and the installed template generate
byte-identical non-worker solutions, so framework-template and regression
scaffolding cases hash every generated file and run each distinct tree's
`dotnet test` or `ankus build` once in class-owned storage. Worker templates
carry distinct shared-memory names and still run separately. Regression cases
copy the shared publication into their own projects and run with `--no-build`
against their own servers. Cases whose rows differ only at run time, such as
fixture storage, abandoned clusters and command cancellation, reuse one generated
project and rebuild it incrementally; the evidence each run asserts is removed
before reuse. Build, property-forwarding and diagnostic cases keep fresh projects.

On Windows, the .NET 10 test harness drains each redirected child-process pipe
on a dedicated reader thread. Those pipes use synchronous handles; reading them
through the thread pool can delay test continuations while children are running.
Cancellation terminates and joins the process tree before joining its readers.
Compiler slots apply to commands that can build; installation, packaging and
schema extraction from existing publications do not reserve those slots. Worker
polling collects diagnostic logs when an assertion fails, preserving the failure
details without repeatedly collecting Windows events on successful polls.

SDK rejection cases own separate projects and restore outputs, so they can run
alongside read-only package checks. A finished package case returns its PostgreSQL
installation lease after its clusters stop, before removing its consumer build
files. Fixture initialization builds shared managed dependencies first and then
publishes every extension in parallel, without waiting for one extension's Native
AOT compilation before starting the others. Package-consumer checks read resolved
properties and analyzer items from the completed publish invocation, avoiding a
second MSBuild process while retaining their package and style-isolation assertions.

Memory cleanup checks query `ankus_test_memory.contexts`, installed by the test
extension. On PostgreSQL 14 and later this view reads the server's memory-context
catalog. PostgreSQL 13 uses the standalone C allocator fixture to walk the actual
native context tree and collect allocator counters. These observations remain
independent of the Ankus memory APIs under test and are available in dedicated
encoding databases as well as the shared fixture database.

Every repository project inherits `MSTestAnalysisMode=All` and
`TreatWarningsAsErrors=true` from `Directory.Build.props`. Fix analyzer findings
without suppressing diagnostics or reducing the enforced analysis mode.
The root `.editorconfig` also enforces IDE0251 as an error: mark eligible struct
members `readonly`, including helpers exposed by fixing their callees.
IDE0042 requires eligible tuple locals to be deconstructed. Nullable annotations
are enabled for repository projects, including samples; CS8632 is an error.
These settings remain confined to the repository and are not added to consumer templates.

Editor design-time builds prepare the repository's build helper, runtime and
analyzer assemblies before resolving native bindings. This also works before
the first ordinary build. The editor receives the project's nullable settings
and generated binding references without publishing the extension. The packaged
SDK already includes the helper and restores its runtime and analyzers from NuGet.

To run a subset:

```console
dotnet test --project tests/Ankus.IntegrationTests/Ankus.IntegrationTests.csproj --filter 'FullyQualifiedName~SpiSessionTests'
```

Filtering still runs Native AOT publishing and cluster startup. Server logs are
retained in `artifacts/test-logs`, and each integration run first removes files
there that are more than seven days old, as the public fixture does with its own
logs. A failure report includes the failing test's
PostgreSQL session log.
Windows fixtures retain their own Application-event messages in the same log,
including PostgreSQL versions that route diagnostics there under a service
token. The test account needs Application-log read access. Collection selects
the cluster's unique event source; it does not collect unrelated machine events.
The installed-tool Valgrind test also retains its final server log there before
removing its development cluster, including when a native query fails.
The configuration package owns the shared `PostgresServerLog` collector used by
test fixtures and persistent development clusters. Windows collection retains
the original event messages and its cursor in an owned sidecar beside the log;
later CLI processes reuse the cluster identity and serialize cursor updates.
`PostgresDevelopmentCluster.ReadServerLog()` refreshes the retained snapshot.
On Unix, it reads PostgreSQL's existing stderr log directly.

Each fixture extension publish also writes a uniquely named MSBuild binary log
under `artifacts/test-logs/publish`, including when Native AOT compilation fails
before cluster startup. Raw binary logs remain local. CI uploads redacted text
server logs and test reports. On GitHub-hosted runners, report preparation also
exports target/task names and durations from retained binary logs; it does not
upload their properties, environment values or command payloads.
Generated-solution test builds retain their binary logs in
`artifacts/test-logs/generated-solution`, outside the disposable test projects.

The public test fixture gives native publications their own SDK artifacts tree
under the extension's `obj/ankus-test-build`. This keeps ordinary test-host
assemblies, symbols and incremental-clean records separate from native publishing.
Referenced projects receive independent subdirectories through the SDK's standard
artifacts layout. Repeated fixture publications retain their native build cache
and schema snapshots there.

Packaged backend tests share a NuGet directory owned by their test run. Each
consumer still builds outside the repository and publishes its own native
extension for a real PostgreSQL cluster. The two GUC package-contract tests use
separate, initially empty package directories to verify cold restore. Reusing
packages for other backend cases avoids repeated package extraction and restore.
Each case removes its consumer builds and cold package directories when its
processes stop. Class cleanup removes the shared packages and pooled projects.
CI places these directories under the runner's owned workspace storage, allowing
the runner to clean up after a terminated test host as well.
CI saves each module's TRX report, including individual test durations, in the
`test-results-<rid>` artifact on successful and failed runs. A cancelled module
may not finish its report; uploads include only reports that were written.

Binding-source cache readers copy verified artifacts into private temporary
directories before releasing the cache lock. Each consumer then compiles and
runs its own native ABI checks independently. Failed verification preserves the
consumer's existing outputs; concurrent builds retain the same content and
header checks as sequential builds.

Managed companions use one Release build contract regardless of the consuming
project's configuration name. Cache lookup happens before restore and compiler
input discovery, so a hit starts no child MSBuild process. The key covers the
generated source, runtime reference, helper, selected SDK and restore settings.
Misses record the exact references, analyzers, source, editor configuration and
framework-pack resolution metadata selected by MSBuild. Hits verify those
external files by content and check package directory membership for added or
removed inputs. Framework-pack
inventory also participates in the key. Timestamps do not establish identity.

Separate restore policies and package directories still perform their own
restore and compiler-input resolution. After that succeeds, a second cache
shares compilation when the resolved inputs match by content. Its identity maps
the private workspace and package roots to stable logical paths, retaining
package names, versions, reference bytes and analyzer configuration. Concurrent
consumers compile a matching companion once. Restore failures cannot be bypassed
by an existing compiled artifact.

Verified cache hits retain shared reader leases, so independent builds can
validate and copy the same entry concurrently. Replacement and retention require
exclusive ownership. A writer closes admission to new readers before waiting for
existing readers, allowing changed inputs to rebuild without repeated competing
validation. Every reader still checks artifact and dependency contents.

Generated-source, managed-request and shared-compilation stores each retain at most **2 GiB**
of idle entries by default. `ANKUS_BINDING_CACHE_MAX_BYTES` selects a positive
byte budget for each store. Reuse refreshes an entry's recency; lease disposal
evicts the least recently used idle entries and removes abandoned staging.
Active leases can temporarily exceed the budget and are never evicted. Lock
files remain in place to preserve cross-process synchronization. Native object
files named by linker manifests outlive the helper process and remain retained
build inputs; clean those only when dependent builds have finished.

Native binding probes and SDK record verification remove their temporary
directories after the compiler and probe processes exit. Production commands and
their tests share bounded Windows file-release retries; persistent access or
deletion errors still fail the operation, including during cancellation cleanup.

If another process takes the reserved TCP port before PostgreSQL binds it, the
cluster harness retries with fresh data, socket and log paths and a new port.
It allows at most three attempts within one `StartupTimeout`, cleans each failed
attempt's data and sockets, and retains its server log. Other startup failures
are reported immediately; cancellation stops further attempts.

Tests that exercise a fixed configured port first bind a candidate listener.
Candidate selection skips occupied ports and operating-system exclusions,
including Windows exclusions reported as access denied. It stops after 32
unsuccessful candidates and preserves the last socket error. An explicitly
requested PostgreSQL port is still used exactly as requested; startup does not
silently select a different port for it.

Tool and development-cluster tests release their reservation before an `ankus`
command or `PostgresDevelopmentCluster` binds the port, so another process can
take it first. On Windows, WSL's localhost relay binds every Linux listener's
randomly chosen ephemeral port on the host; Windows itself assigns ports from one
sequential counter and does not reissue a released port until it wraps. Those
tests use `[RetryPortCollisionTestMethod]`, which reruns the whole test with new
ports and directories only when every failure reports PostgreSQL's bind
collision, up to three attempts. The final result's output records each
superseded attempt. Tests that cause collisions on purpose keep `[TestMethod]`.

Run-as cases (`ankus test --runas` and `PostgresTestClusterOptions.RunAs`) start
real servers as another Unix account, like pgrx's `--runas` job. They run when
`ANKUS_TEST_RUNAS_ACCOUNT` names an account that `sudo -n -u ACCOUNT` reaches
without a password, and report inconclusive otherwise. Provision an unprivileged
account and a sudoers rule that grants only that account, never root:

```console
sudo useradd --system --user-group --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin ankus-runas
echo "$USER ALL=(ankus-runas) NOPASSWD: ALL" | sudo tee /etc/sudoers.d/ankus-runas
sudo chmod 0440 /etc/sudoers.d/ankus-runas && sudo visudo -cf /etc/sudoers.d/ankus-runas
```

On macOS, create the account with `dscl` instead, using an unused UID below 500:

```console
sudo dscl . -create /Groups/ankus-runas PrimaryGroupID 498
sudo dscl . -create /Users/ankus-runas UniqueID 498
sudo dscl . -create /Users/ankus-runas PrimaryGroupID 498
sudo dscl . -create /Users/ankus-runas UserShell /usr/bin/false
sudo dscl . -create /Users/ankus-runas NFSHomeDirectory /var/empty
sudo dscl . -create /Users/ankus-runas IsHidden 1
```

The account must be able to read the PostgreSQL installation. Distribution and
Homebrew packages are readable; a downloaded installation under a private home
directory is not, because its binaries load libraries through an absolute
`RUNPATH` there. CI test runs use `ankus-runas` when the runner provides it,
create it on GitHub-hosted Linux runners, and print a warning otherwise. Tests
stage PostgreSQL 13 to 17 inside the workspace, so on hosted runners CI also
lets other accounts search the directories above it, including the runner's
home directory. Grant a dedicated runner's account the same rule for
`ankus-runas`, and make sure that account can search its workspace's parents.

For a separate PostgreSQL 18 build, set `ANKUS_TEST_PG_CONFIG` to its `pg_config`
path for the integration test process. The fixture uses that installation for
both native publishing and cluster startup, preserving the user's registered
installation. The allocator tests compile a test-only native module against the
same headers. Run the Bump cases on both assertion-enabled and ordinary server
builds: ordinary Bump allocations have no chunk header.

`tests/Ankus.TestExtension` contains attributed backend probes. `tests/Ankus.IntegrationTests`
invokes them and checks their SQL results. The public `Ankus.Testing` package lives
in `src/Ankus.Testing` and owns cluster startup, transactions, diagnostics, and
shutdown. Repository-specific fixtures stay under `tests/`. FATAL and PANIC tests
use dedicated clusters.

### Large memory allocation tests

The allocation lifecycle tests always run five small cases for ordinary,
no-OOM, over-aligned, aligned no-OOM, and zeroed allocation. To also execute the
five cases above PostgreSQL's ordinary allocation limit, use a dedicated run:

```sh
timeout --signal=TERM --kill-after=20s 6m env \
  ANKUS_TEST_PG_CONFIG=/path/to/release/postgresql/bin/pg_config \
  ANKUS_TEST_HUGE_ALLOCATIONS=1 \
  dotnet test --project tests/Ankus.IntegrationTests -c Release \
  --filter 'FullyQualifiedName~AllocationLifecycleTests'
```

The additional cases allocate 1 GiB + 17 bytes, grow by 1 MiB while still above
the ordinary limit, shrink to 128 bytes, and free the chunk before deleting its
context. They check exact endpoint values, checked views, retained policy and
alignment, and independent native/catalog accounting. No-OOM and over-aligned
resize copy the retained payload; zeroed allocation writes the initial payload.

This resource run currently requires 64-bit Linux with cgroup v2, readable
process/resource accounting, `prlimit`, and `timeout`. Keep an external deadline
for the run: SQL cancellation alone cannot bound a native allocation or copy.
Run it separately from other builds and tests. It admits each large case only
with more than 5 GiB of available host
memory and the same headroom under any finite cgroup memory limits. Each fresh,
warmed backend receives an additional 3 GiB address-space allowance. This bounds
new address-space allocation; it does not reserve physical memory or constrain
use of pages in mappings the backend already holds. Admission failures fail the
requested test instead of counting it as passed or skipped.

Use a matching release server/header installation for the economical run.
PostgreSQL debug allocation randomization and freed-memory clobbering can touch
the entire buffer. The test retains server, memory high-water, and cgroup
observations in `artifacts/test-logs/huge-allocations/`. An ordinary `dotnet test`
run without the opt-in variable supplies small-case evidence only; report the
large resource run separately.

## Publish the sample

On Linux x64:

```console
dotnet publish samples/Ankus.Examples.Hello -c Release -r linux-x64 --self-contained -o artifacts/hello
```

The sample imports `src/Ankus.Sdk/Ankus.Sdk.targets`, which supplies the runtime,
generator, and build-tool references. Shared .NET settings come from
`Directory.Build.props`.

Both `dotnet build` and `dotnet publish` require the selected PostgreSQL server
headers and a C compiler. Before compiling an extension, the SDK measures the
native layouts and builds a generated companion assembly under the extension's
intermediate directory. `AnkusPostgresMajor` selects the major (18 by default),
and `AnkusPgConfigPath` selects an explicit installation. The same properties
must apply to referenced extension projects. A requested runtime identifier
that differs from the compiled probe's target fails explicitly.

## Build the packages

Pack the solution into a local feed:

```console
dotnet pack -c Release -o artifacts/packages
```

This produces `Ankus.Sdk`, `Ankus.Runtime`, `Ankus.Generators`, `Ankus.PgConfig`,
`Ankus.Testing`, `Ankus.Tool`, and `Ankus.Templates`. The SDK includes its native build helper and
matching runtime/generator versions. It uses NuGet's MSBuild SDK resolver.

Add the absolute feed path to a consumer's `NuGet.Config` and use
`<Project Sdk="Ankus.Sdk/<version>">`, where `<version>` is the `VersionPrefix` in
`Directory.Build.props`. Both the SDK resolver and package restore read
that configuration. The consumer needs its own `TargetFramework`, nullable, and
implicit-using settings; it does not import repository build files.

## Build the tool

Pack and install from the local feed:

```console
dotnet pack src/Ankus.Tool -c Release -o artifacts/packages
dotnet tool install Ankus.Tool --tool-path artifacts/tools --add-source artifacts/packages --version <version>
```

Invoke `artifacts/tools/ankus` (`ankus.exe` on Windows), or put that directory on
`PATH`. For development without packing:

```console
dotnet run --project src/Ankus.Tool -- --help
```

`ToolCommandTests` packs all six packages with a unique version, then creates
consumer projects outside the repository with an empty NuGet package directory.
It verifies installed-tool publishing and staging, direct `dotnet publish` with
Central Package Management, and real SQL execution. A separate MSTest consumer
references only the packed `Ankus.Testing`, runs ordinary `dotnet test`, and checks
native error recovery as well as successful calls.

Native AOT sample publication and build-intensive package-consumer child
processes share one concurrency limit. By default it uses one slot per two
logical processors, with a minimum of one. Set
`ANKUS_PACKAGE_TEST_CONCURRENCY` to a positive integer to override both phases
for the machine; malformed values fail initialization. Fast tool commands and
PostgreSQL assertions do not reserve build capacity. The fixture logs the
selected limit, logical processor count and processors per build. Regression
dry runs, `run`/`regress`/`bench` commands using `--no-build`, and manifest
upgrades do not wait for compiler slots. `dotnet test --no-build` still reserves
one because test fixture initialization can publish a Native AOT extension.
Build slots divide the machine's logical processors evenly and distribute any
remainder across the slots. Each child receives its slot's `DOTNET_PROCESSOR_COUNT`,
with a minimum of one. For example, 32 processors with 20 slots allocate twelve
two-processor slots and eight one-processor slots. Native AOT compilation,
managed worker pools and GC consequently share that budget instead of each
assuming ownership of the whole machine. This applies only to the repository's
test processes. Version-selection cases share one reusable project pool with
the same configured limit. MSTest's worker count can impose a
lower limit. PostgreSQL releases before 18 also use a separate staged
installation for each slot, so consumers with the same extension name cannot
overwrite another active test's control or SQL files. PostgreSQL 18 and later
select each consumer's own extension directory.
The fixture releases a slot after its test finishes and removes the staged
installations during class cleanup. Cases that change build settings own independent
projects. Every platform job still runs the complete suite.
Consumer builds disable MSBuild node reuse so worker processes release the
fixture's temporary package assemblies before class cleanup.

Consumer build files use runner-owned temporary storage in CI and are removed
after each case. On Unix, test-controller sockets use a separate short directory
when the build path would exceed the 103-byte portable socket-path limit.
The check includes MTP's monitoring prefix and GUID (46 bytes), and counts UTF-8
bytes rather than characters. The fixture
owns and removes the short directory; `TESTINGPLATFORM_PIPE_DIRECTORY` can select
its parent when the user's home directory is also too long.

`ankus new` bundles source templates under `src/Ankus.Tool/Templates/Extension`.
It creates a version-matched solution with CPM and native MTP discovery. The
package tests run the generated solution's `dotnet test` outside the checkout,
then change an extension function and verify its backend test fails. The same
tests check keyword namespaces, path handling, and preservation of existing files.

## Security checks

The `CodeQL` workflow scans the C#, documentation-site TypeScript and workflow
sources with the `security-and-quality` suite on every change. C# is read from
source, so anything MSBuild adds behind the scenes stays invisible to it; each
test project therefore states MSTest's global `using` in a source file. On
`main`, a
dedicated Linux runner also traces the C that the integration tests compile: the
emitted native bridge and the fixtures composed from it. Code scanning only lists
results, so `eng/Ankus.CodeQl.cs` fails the workflow for any finding; fix the code
rather than suppressing a query. A few findings describe code that must stay as it
is, such as a catch that returns every exception to PostgreSQL at the native
boundary. Mark each of those with a `// codeql[rule-id] reason` comment on the line
before it; the check lists them and does not fail, and on `main` the workflow
dismisses their alerts in code scanning as won't fix, quoting each comment's
reason; an alert whose result loses its comment reopens. Only two C queries are turned
off, in `.github/codeql-config.yml`: every `PG_TRY` links a jump buffer on its
own stack into a global list, which they report as an escaping stack address, and
a comment cannot mark a macro expansion. Any frame the bridge links the same way
must unlink itself on every exit, as `PG_TRY` does. The `Secret scan` workflow runs
[Picket](https://github.com/willibrandon/picket) on every change. Run the same scan
locally before pushing:

```console
picket scan . --redact=100
```

A genuine false positive's fingerprint goes in `.picketignore` with a comment
explaining it; prefer changing the code so it no longer looks like a secret.
Dependabot proposes NuGet, workflow and documentation-site updates weekly;
`.github/dependabot.yml` explains the packages that move only with the SDK or the
embedded runtime. Report vulnerabilities as [SECURITY.md](../../SECURITY.md)
describes.

## C# type style

Ankus follows the `dotnet/runtime` and `dotnet/msbuild` convention: use explicit
local types when the right-hand side does not name the type. Built-in values and
non-apparent results are enforced as IDE0008 errors through `.editorconfig` and
`EnforceCodeStyleInBuild`. A constructor or explicit cast that names its type
permits either `var` or an explicit declaration; IDE0007 does not force `var`.
Roslyn's own repository generally prefers `var`, so its type-style rules are not
the convention used here.

Use primary constructors wherever supported by IDE0290. The repository enforces
`csharp_style_prefer_primary_constructors` and IDE0290 as errors.
Constructor validation, visibility and native layouts must remain
unchanged when applying the conversion.

Remove redundant casts. IDE0004 is enforced as an error throughout the repository.
Use collection expressions where IDE0300 applies; it is also enforced as an error.
Remove unnecessary `unsafe` modifiers; IDE0380 is enforced as an error.

Leave a blank line after a closing block brace before the next statement or
declaration. IDE2003 enforces statement separation during repository builds.
Connected `else`, `catch`, and `finally` clauses and adjacent enclosing closing
braces stay together. Do not insert a blank line immediately after an opening brace.

Fix warnings at their source. Do not add warning pragmas, suppression attributes,
`NoWarn`, or lower an enforced diagnostic's severity.

These conventions apply to Ankus development. Consumer projects created by
`ankus new` choose their own style; the scaffold does not include an `.editorconfig`
or enable code-style enforcement in their builds.
