# Engineering apps

Ankus repository automation uses .NET 10 file-based apps. Run an app from the
repository root with `dotnet run --file`.

| App | Purpose |
| --- | --- |
| `Ankus.Ci.cs` | Validate, build, test, pack, and publish Ankus and its pinned Native AOT runtime. |
| `Ankus.SqlStates.cs` | Regenerate or check the named SQLSTATE catalog from pinned PostgreSQL source tags. |
| `Ankus.Oids.cs` | Regenerate or check the version-aware built-in OID catalog from pinned pgrx sources. |
| `Ankus.Bindings.cs` | Regenerate or check native declarations, node cast graphs and attribution from pinned pgrx bindings; check mode also verifies the source-derived header bytes. Header mode derives a major's type catalog and function, global and constant inventory from installed headers and compares them with the pinned ones. |
| `Ankus.Headers.cs` | Regenerate or check one header manifest directly from an installed PostgreSQL 13–19 server-header tree. |
| `Ankus.Templates.cs` | Stage the shared extension, worker and optional test-framework assets for the version-matched template package. |
| `Ankus.BuildTimings.cs` | Read individual binding-task durations and cache outcomes from retained MSBuild binary logs without exposing command arguments. |
| `Ankus.CodeQl.cs` | Fail the CodeQL workflow for every finding in its SARIF results, which code scanning would otherwise only list, and dismiss the alerts of results that a `codeql[rule]` comment marks. |

`Ankus.Ci.cs` provides these commands:

| Command | Purpose |
| --- | --- |
| `metadata` | Validate and export a runtime line's pinned identity: `net10.0` by default, or the target framework given. |
| `runtime-info` | Validate and print a runtime line's repository, commit, matching package/compiler versions and staging directory locally. |
| `select-sdk` | Select another .NET SDK in this checkout's `global.json`, such as a .NET 11 preview for an acceptance run. |
| `release-metadata` | Validate a release tag and export release metadata. |
| `quality` | Install PostgreSQL headers, build Ankus and every automation app, and validate generated API and site documentation. |
| `runtime-build` | Build and stage a runtime line's runtime and compiler host for the runner's platform; a trailing target framework selects the line. |
| `runtime-pack` | Pack a staged runtime line's runtime and compiler host for CI. |
| `runtime-test` | Use a staged runtime to run the complete unit and PostgreSQL integration test suites. |
| `runtime-test-build` | Prepare the staged runtime's PostgreSQL toolchain and build all test projects before saving CI caches. With `AnkusExtensionTargetFramework` set, it also checks that line's staged runtime. |
| `windows-toolchain` | Select the latest supported Visual Studio installation with C++ x64 tools and export its developer environment to later CI steps. |
| `runtime-test-run` | Run every already-built test module using the PostgreSQL and compiler environment prepared by `runtime-test-build`. The integration and Hello sample tests run from the extensions' target framework output. |
| `native-analysis-run` | Run the integration fixture setup and one worker test, which compile the generated native bridge and every composed native fixture, so CodeQL can trace them. |
| `header-frontend-setup` | Select LLVM 20 or later and install LLVM 20 on Linux when no supported compiler is present. |
| `target-export` | Export a runner's PostgreSQL target information for the Hello sample's toolchain, as `ankus target export` does. |
| `target-build` | Build the Hello sample for another runtime from exported target information, with Clang and libclang unavailable, and check the library's architecture. |
| `target-verify` | Load a cross-built Hello publication into a temporary PostgreSQL 18 or later server on the target machine and call it. |
| `header-frontend-check` | Check an explicit Clang executable for the declaration-only frontend required by header collection. |
| `postgresql-check` | Check an explicit PostgreSQL 13–19 `pg_config` for its selected major and server headers without installing packages. |
| `unit-test` | Build and run the five unit test modules. |
| `prepare-reports` | Copy test results and server logs with private runner identifiers removed before artifact upload. |
| `runner-cleanup` | On a self-hosted runner, stop build servers, remove generated checkout files after artifact upload, and delete abandoned package-test directories. |
| `release-managed` | Pack the managed NuGet packages. |
| `release-runtime` | Build and pack one runtime line's runtime and compiler host for one platform. |
| `release-verify` | Check the complete NuGet package set, including every runtime line's packages, and each manifest's identity, license and source commit. |
| `publish` | Verify and publish the complete NuGet package set. |

Each supported extension target framework has its own runtime line: a fork
commit, a patched runtime and compiler host package version, and the upstream
compiler it pairs with. `net10.0` stages into `artifacts/nativeaot`; other lines
stage into `artifacts/nativeaot-<framework>`. The `.NET versions` workflow builds
the `net11.0` line and runs the complete suite with every repository extension
retargeted through `AnkusExtensionTargetFramework=net11.0`. The framework-template
consumer cases also target `net11.0` there, so the feed carries both lines. Its
`postgresql` input runs that suite against another PostgreSQL major, and its
`platform` input adds Intel macOS, which `all` leaves out because its suite takes
hours.

Runtime and compiler packages are built on their own operating system and
architecture, including Intel macOS. Linux/macOS builds also execute the fork's bounded host-shutdown
probes before staging a payload. They require normal process exit and managed
thread cleanup, including mutex abandonment, GC and finalizer drain.
Signal checks also verify host masks, ordinary and alternate stacks, and
termination deferred until forked children install their own handlers. They
exercise both the runtime's native object and an initialized Native AOT library.
A host-signal check sends PostgreSQL's signals to a process whose host thread
blocks them: runtime threads must leave each one pending for that thread.

Use `--` before command arguments:

```text
dotnet run --file ./eng/Ankus.Ci.cs -- metadata
```

`runtime-test` selects an installed LLVM 20 or later on Linux, installing LLVM 20
when necessary. It installs LLVM 20 on hosted macOS, and uses dedicated macOS
runners' Clang on PATH or the Windows runner's LLVM installation.
It prints the compiler version and verifies
`-skip-function-bodies` support before building or running tests. The platform
default Clang can be too old even when Native AOT compilation works. Check a local
compiler without installing or changing anything:

```text
dotnet run --file ./eng/Ankus.Ci.cs -- header-frontend-check /path/to/clang
```

Platform CI separates `runtime-test-build` and `runtime-test-run` so it can save
the native binding cache before the complete suite starts. The build command
exports the selected PostgreSQL installation and compiler path to subsequent
GitHub Actions steps. `AnkusBindingCacheDirectory` selects the restored cache;
normal header preprocessing, native ABI verification and content checks still
run before reuse. The full suite remains in one platform job. Primary and
compatibility jobs use the measured 120-minute limit, while Intel macOS retains
360 minutes.
`runtime-test` retains the combined local command.
Test commands write each module's TRX results and durations to
`artifacts/test-results`; platform CI uploads available reports on every outcome.

Owner-triggered main-branch pushes and manual runs use the dedicated runners
labelled `ankus-linux-x64`, `ankus-macos-arm64` and `ankus-windows-x64`. Both the original actor and
the actor requesting a rerun must be the repository owner. Pull requests and
other actors use GitHub-hosted runners; all external contributors require
workflow approval. The same actor checks apply to release runtime builds.
These workflow conditions route normal jobs; review workflow edits before
approving an external run because approval also permits its changed workflow.
Keep the runner services online before dispatching dedicated jobs.
CI selects the latest stable .NET 10 SDK allowed by `global.json`.

Publishing a GitHub release runs the `Release` workflow, which packs the managed
packages and every runtime and compiler package, then publishes them to NuGet.
Dispatching it with a `tag` input such as `v0.1.0` rehearses the same build and
packing from the dispatched commit and skips publishing.

The `Additional platforms` workflow runs the complete suite on the dedicated
Intel macOS runner, covering the release-only macOS x64 architecture. The weekly
run tests PostgreSQL 13–17, 18 and 19 beta in turn; manual dispatch takes a
`postgresql` input of one major (18 by default) or `all`. MacPorts supplies 13–18,
with 18 on `PATH`, and 19 beta is an `ankus init` build; repository variables
`ANKUS_MACOS_X64_PG<major>_PG_CONFIG` name every major except 18. Runtime, NuGet
and binding caches are saved before tests so later runs reuse successful
preparation. Each major retains its test results and timings and allows 360
minutes. Successful execution is required before counting Intel macOS as
validated.

The same workflow checks cross-architecture builds. The Intel runner exports its
PostgreSQL 18 target information for the Hello sample, the Apple silicon runner
builds `osx-x64` from that archive with Clang and libclang unavailable, reusing
the primary workflow's cached `osx-arm64` compiler, and the Intel runner loads
the result into a temporary server and calls it. A `jobs` dispatch input of
`tests` or `target` runs only the suites or only this check. The first run took
94 seconds to export with a cold binding cache, 67 seconds to build and 32
seconds to load; each job allows 10 or 15 minutes.

The `PostgreSQL versions` workflow runs complete Linux x64 suites for PostgreSQL
13–17 and 19 weekly. Manual dispatch selects one major, including 18, or all six
additional majors. A separate runner labelled `ankus-linux-versions-x64` keeps
these checks off the primary CI queue. Both actors must be the repository owner
and the selected ref must be `main`; there is no pull-request trigger.
Each major runs the complete suite in its own 120-minute job. The repository
setting controls how many dedicated Linux runner services execute concurrently.
Failures do not cancel the other majors or supersede existing runs.
Install each selected server and matching development headers before dispatch;
PostgreSQL 19 uses its current prerelease until a stable release is available.
Check an installation before enabling the workflow:

```text
dotnet run --file ./eng/Ankus.Ci.cs -- postgresql-check 19 /path/to/pg19/bin/pg_config
```

The preparation command verifies that `pg_config` matches the selected major,
including beta and release-candidate identifiers. Completed native test reports,
not the matrix definition or this prerequisite check, establish version coverage.

### Additional platform and version prerequisites

The `PostgreSQL platform versions` workflow runs weekly or on manual dispatch:

| Platform | PostgreSQL majors | Purpose |
| --- | --- | --- |
| macOS ARM64 | 13 through 17 and 19 | Exercise every major that primary PG18 CI does not, including both sides of the `.so`/`.dylib` extension suffix change at 15 and 16. |
| Windows x64 | 13 through 16, 18 and 19 | Exercise every major that primary PG17 CI does not. |

Install the Homebrew `postgresql@15` and `postgresql@16` formulae on the macOS
runner. Build the other macOS majors with `ankus init --home <directory> --pg13
download --pg14 download --pg17 download --pg19 download` in a runner-local
directory, and set repository variables `ANKUS_MACOS_PG13_PG_CONFIG`,
`ANKUS_MACOS_PG14_PG_CONFIG`, `ANKUS_MACOS_PG17_PG_CONFIG` and
`ANKUS_MACOS_PG19_PG_CONFIG` to their `pg_config` paths. On Windows, install each major with server headers and
import libraries; PostgreSQL 16 must be 16.15 or later and 18 must be 18.6 or
later. EDB publishes no Windows beta, so build 19 beta from source with meson
and MSVC (with winflexbison and Strawberry Perl on `PATH`) into the same layout.
Build it with `-Dicu=enabled`, as the EDB builds are, because the suite tests ICU
collations: point `ICU_ROOT` at ICU's official Win64 MSVC binaries so meson finds
them through CMake, and copy the `icuuc`, `icuin` and `icudt` DLLs into the
installation's `bin` directory.
Configure repository secrets `ANKUS_WINDOWS_PG13_ROOT` through
`ANKUS_WINDOWS_PG16_ROOT`, `ANKUS_WINDOWS_PG18_ROOT` and `ANKUS_WINDOWS_PG19_ROOT`
with each installation's root, containing its `bin`, `include` and `lib`
directories.
The workflow selects the matching root for the build step. It does not change
the runner's default `PGROOT` or store device paths in tracked files.

Check every installation with `postgresql-check` before dispatch. Manual dispatch
can select one `platform` and one `postgresql` major; the weekly run tests every
cell. Each matrix cell runs the entire suite against its selected real server, with a 120-minute
timeout and separate result artifacts. Runner capacity queues macOS ARM64 suites.
Windows test commands use two machine-wide slots across the available runner
services so independent cells overlap without saturating one physical host and
queued jobs are never replaced. Independent machines can run simultaneously. Runtime artifacts reuse
the primary workflow's verified runtime cache. Failures do not cancel other
cells, and there is no cancellation of earlier runs. Both actors must be the
repository owner on `main`, and the workflow has no pull-request trigger.

Successful reports are still required for coverage. This workflow does not by
itself establish acceptance of these or other supported platform/version pairs.

### Linux version test concurrency

`ANKUS_LINUX_VERSIONS_PACKAGE_TEST_CONCURRENCY` sets the compatibility runner's
default package-consumer slots independently of primary CI. Configure the runner
privacy secret before uploading reports.

Timed-out runs also upload available redacted failure logs. Test builds print
an MSBuild performance summary and retain a unique binary log in
`artifacts/test-logs` for local investigation. Binary logs can contain machine
paths and environment values; they remain local and are not uploaded.
Hosted test runs also print each completed case and its duration. These records
remain in the job log if a timeout prevents the final integration TRX from being
written. Captured test output is printed for failures.

Hosted report preparation also replays retained build logs into
`build-timings.log`, containing only target/task names and durations. This covers
fixture publication and package-consumer logs still present after a timeout;
unfinished logs are labelled explicitly. Raw binary logs remain local.
The report also records each completed `binding-sources`, `binding-compile` and
`binding-link` task separately, including its success and cache outcome. These
durations include helper startup and work inside that task; concurrent task
durations must not be added together as elapsed build time. Incomplete tasks
have no proven duration or outcome. The reader accepts one or more local binary
logs and performs no builds or backend calls.
Hosted test builds print these binding timings immediately after the initial
solution build, so the first measurements do not depend on test completion.

The additional-platform workflow accepts `package-test-concurrency` on manual
dispatch, matching the primary workflow. Compare the same commit with complete
test suites when tuning it; a timed-out subset is not validation.

The Linux service runs under its own unprivileged account. Provision PostgreSQL
18 with server headers, Clang 20 or later with matching libclang, and the .NET
runtime build prerequisites, Valgrind and matching libc debug symbols once
(`valgrind` and `libc6-dbg` on Debian/Ubuntu). Keep Clang's `bin` directory on the service
PATH. On macOS, provision Homebrew's `llvm`, `postgresql@18`, `cmake`, `ninja`
and `pkgconf`; run the runner as a user LaunchAgent. Keep the session logged in
and the machine awake for queued work. Dedicated jobs validate the installed
tools and do not run package-manager commands or require sudo. Runtime cache
keys distinguish dedicated Linux/macOS builds from hosted builds so each
toolchain is actually exercised. Runner labels identify platform roles and can
move to replacement machines without changing workflows.

Keep personal names and paths out of runner labels and work directories. Before
assigning a personal machine its runner label, update the repository secret
`ANKUS_RUNNER_PRIVATE_IDENTIFIERS` with one private identifier per line, including
its machine name and any personal user/home paths. CI and release jobs reference
that secret so GitHub masks startup logs. `prepare-reports` separately creates
redacted copies of TRX results and server logs; only those copies are uploaded.
Keep this secret current when replacing a runner. Raw reports remain local.

Windows needs PowerShell 7, Git, current Visual Studio C++
tools, CMake, Ninja, Python, LLVM 20 or later with matching libclang, and
PostgreSQL 17.11 or later in major 17 with server headers and import libraries.
Set `PGROOT` to the dedicated installation root when the machine's default
installation differs. The CI build checks the required maintenance version
before compiling; aligned no-OOM allocation tests require PostgreSQL 16.15,
17.11 or 18.6 for their respective majors.

The service PATH must include Windows PowerShell for the runtime build and
Git's `usr/bin` directory for the cache action's `tar`/`gzip` pair. Keep Git's
Unix tools after the system tool directories. The `windows-toolchain` command
requires Visual Studio 2022 17.9 or later and queries `vswhere` for the C++
component, preventing unrelated products such as SQL Server Management Studio
from winning discovery. Its developer environment puts the selected MSVC
compiler and linker first. It exports `IlcUseEnvironmentalTools=true` so child
Native AOT builds reuse that selection without repeating `vcvarsall`. The Ankus
SDK also carries its `LIB` roots into header verification and native compilation.

The dedicated runner keeps its SDK installation, NuGet packages, temporary
files and work directory under its own storage root. Configure
`DOTNET_INSTALL_DIR`, `DOTNET_ROOT`, `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `TEMP`
and `TMP` for its service account. Native binding entries persist in
`runner.tool_cache/ankus-binding-cache` and retain normal content and ABI
validation. Parallel binding compiler restores share the package cache but keep
their transient NuGet HTTP metadata in their owned compiler workspaces, avoiding
cross-process vulnerability-cache replacement races. Self-hosted jobs reuse these
local caches; hosted jobs continue to restore and save GitHub caches. Before
upload, report preparation redacts every TRX machine identity and all configured
private identifiers. After upload, `runner-cleanup` shuts down build servers,
removes ignored and untracked checkout outputs, and deletes package-test
directories more than 12 hours old that cancelled suites left in the shared
temporary directory. All platform suites remain
complete and unsharded. Workflows do not automatically cancel earlier runs; jobs
queue while their dedicated runner is busy.

`ANKUS_PACKAGE_TEST_CONCURRENCY` controls both Native AOT sample publication and
build-intensive package-consumer child processes. It accepts any positive integer.
Without an override, the fixture uses three slots per four logical processors.
Parallel sample publications and generated consumer builds use one MSBuild node
each and disable node reuse, preventing nested or retained build workers from
multiplying the configured outer limit. CI's manual
`package-test-concurrency` input overrides it for a comparison run. The repository
variables `ANKUS_WINDOWS_PACKAGE_TEST_CONCURRENCY`,
`ANKUS_LINUX_PACKAGE_TEST_CONCURRENCY` and `ANKUS_MACOS_PACKAGE_TEST_CONCURRENCY`
select each dedicated runner's normal
setting independently. Compare complete suites at the same commit, SDK and
PostgreSQL version with equivalent cache conditions before raising that setting;
record elapsed time, test outcomes and whether the run started with cold caches.

Source and managed binding caches have separate **2 GiB** idle-entry budgets.
`ANKUS_BINDING_CACHE_MAX_BYTES` overrides each budget. Eviction uses the normal
cross-process leases and leaves active builds alone. Native linker objects are
retained until maintenance can establish that no dependent build is using them.
Remove obsolete experiment checkouts and completed test artifacts after retaining
the compact evidence needed for the current change; avoid keeping duplicate
runtime source trees and historical binary logs indefinitely.

See the [.NET file-based app documentation](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps)
for SDK behavior.

Regenerate SQLSTATE constants from an existing read-only PostgreSQL checkout:

```text
dotnet run --file ./eng/Ankus.SqlStates.cs -- /path/to/postgres
dotnet run --file ./eng/Ankus.SqlStates.cs -- /path/to/postgres --check
```

The app reads `src/backend/utils/errcodes.txt` at the pinned PostgreSQL 13–18 and
19 beta tags listed in its source. It makes no network requests or changes to
the reference checkout. The generated union retains native aliases and names
removed from newer server versions. Update the tag list when refreshing the
catalog, then regenerate the API documentation from its XML comments.

Regenerate built-in OID values and native names from an existing read-only pgrx checkout:

```text
dotnet run --file ./eng/Ankus.Oids.cs -- /path/to/pgrx
dotnet run --file ./eng/Ankus.Oids.cs -- /path/to/pgrx --check
```

The app reads PostgreSQL 13–19 catalogs at the pgrx commit pinned in its source.
It preserves per-version membership and native renames; one enum member represents
each numeric value. These catalogs follow pgrx's constant-name heuristic and are
not a list of every built-in database object. The app makes no network requests
and does not modify the reference checkout.

Regenerate the native declaration catalogs using the same read-only checkout:

```text
dotnet run --file ./eng/Ankus.Bindings.cs -- /path/to/pgrx
dotnet run --file ./eng/Ankus.Bindings.cs -- /path/to/pgrx --check
```

The catalogs retain fields, typedefs, enums and pgrx's node inheritance/alias
rules for PostgreSQL 13–19. Separate raw catalogs retain foreign function
signatures, global mutability, callback aliases, variadic arguments and original
linkage, including pgrx-specific C shims. The raw catalogs also retain unevaluated
reference constants; these include platform-dependent values and must not be
used as target measurements. Native sizes, offsets and target constants must
be established using the selected server headers.
The app invokes the `Ankus.Build binding-catalogs` command; parsing and catalog
generation stay inside the build tool without exposing its internals.
See the [catalog inventory](../src/Ankus.Build/Bindings/README.md) for the pinned
declaration counts and remaining runtime binding scope.

Header mode derives the type catalog and the foreign function, global and
constant inventory without pgrx's generated output. It compiles the major's manifest
against an installed server with Clang 20 or later, reads the declarations
through the compiler's libclang in a worker process, applies pgrx 0.19.3's
bindgen configuration, and lists every difference from the pinned catalogs,
ignoring formatting. The command exits with 2 when they differ:

```text
dotnet run --file ./eng/Ankus.Bindings.cs -- --headers /path/to/pg_config /tmp/pg18-derived.json
```

Differences are expected where the installation is configured differently from
pgrx's reference build. Assert-enabled servers add `USE_ASSERT_CHECKING` fields
and functions, configure options and version strings change constants, ICU,
NLS and liburing builds add their declarations, distribution patches can add
their own, and the C library contributes its own versions of types such as
`_IO_FILE`.

The same command refreshes the upstream license notice. In check mode it also
verifies that the source-derived manifests still match the pinned pgrx release,
without writing them. Header manifests come directly from each installed
PostgreSQL version rather than copying pgrx's generated output:

```text
dotnet run --file ./eng/Ankus.Headers.cs -- 18 /path/to/pg_config
dotnet run --file ./eng/Ankus.Headers.cs -- 18 /path/to/pg_config --check
```

Run the command once for every supported major from 13 through 19. It applies
pgrx 0.19.3's directory, explicit-file and exclusion rules, then writes the
deterministically sorted `src/Ankus.Build/Bindings/pgXX.h` manifest. Missing
optional headers remain absent for older majors. Linux full-suite CI checks the
selected major before executing its tests. Primary PostgreSQL 18 CI plus the
scheduled 13–17 and 19 matrix therefore check all seven manifests. These inputs
are packaged with the build tool so an installed SDK does not need a pgrx checkout
to measure a server's native layouts.

For layout development, compile and run the selected header probe:

```text
dotnet run --project src/Ankus.Build -c Release -- binding-layouts 18 /path/to/pg_config artifacts/binding-layouts/pg18
```

The command discovers selected-header fields before writing `native-layout.c`,
its executable, raw observations and validated `native-layout.json`.
`native-node-availability.json` lists reference fields absent from those headers.
It measures every node and its present embedded value
dependencies, including unnamed types reached through named fields, arrays and
flexible tails, and checks
all node tags against the pinned major. Pointer and C long widths, plain-char
signedness, byte order, sizes, alignments and field offsets come from the selected
headers and compiler. Named enum widths, signedness and every constant are also
validated. The probe runs on the build host; it does not establish a
cross-compilation ABI. An optional fourth argument selects the C compiler; on
Windows a fifth argument supplies semicolon-separated native library directories.
A sixth argument selects the expected runtime identifier and a seventh passes
the compiler target triple. The actual compiler target must match the requested
runtime identifier.

Promoted anonymous members are checked through their enclosing declarations.
Independent probes compile complete headers, including implementation headers,
and link only definitions reached by the probe. Unused backend functions need
no standalone implementation; missing dependencies in executed checks still fail
linking. Compile-time assertions and executable bitfield checks remain enabled.
The pinned pgrx 0.19.3 inventory, OIDs and PostgreSQL 19 SQLSTATE inputs all use
19 beta 4. Complete platform/version validation remains required.

To emit managed declarations and their companion project, use `binding-sources`
with the same arguments, followed by optional Clang executable and libclang paths.
The native layout compiler remains separate from the Clang declaration frontend.
The command collects the complete node dependency graph, checks the independent
node/enum observations and emits one companion containing all reached native
declarations. Large temporary AST files are removed after workers exit, including
on failure. A failed collection does not replace a previous companion source.
The SDK invokes this command before C# compilation,
builds the generated `Ankus.NativeBindings.csproj`, and references its assembly.
The assembly name includes a hash of the measured declarations so consumers
built against the same contract share native type identity. Generated source
retains its timestamp when unchanged, and normal project clean removes the
companion artifacts. Compiler/header inputs are measured again on each build.

For raw-call development, put one catalog function name per line in a text file
and verify its native signature against an installed server:

```text
dotnet run --project src/Ankus.Build -c Release -- binding-signatures functions.txt 18 /path/to/pg_config artifacts/binding-signatures/pg18
```

The arguments after the function list match `binding-layouts`, including optional
compiler, Windows library directories, runtime identifier and target triple.
The command writes `native-signatures.c`, its executable, raw observations and
validated `native-signatures.json`. C11 generic-selection static assertions check complete prototypes;
the probe measures fixed parameter and result storage without calling backend
functions. Native typedef names remain intact so target headers determine widths.
Nested callback declarations, const-qualified pointers and arrays retain C
declarator precedence. pgrx shim linkage selects the corresponding header function,
without requiring a pgrx-specific export in the PostgreSQL server.

Unknown/duplicate names, incompatible prototypes, unavailable header declarations
and incomplete observations fail explicitly. The probe does not establish export
availability, a managed calling convention, pointer ownership or an error guard.
Some reference declarations still need target-derived type/prototype information,
including anonymous C typedefs, platform types and qualifiers lost in Rust. A PostgreSQL major match
alone does not prove that every installed-header function matches the catalog.
Variadic signatures retain only their fixed arguments; promoted call-site arguments
still require generation. Guarded managed calls and hook registration remain open.

To collect authoritative types from the selected headers, put catalog function
and/or global names in a text file, one per line:

```text
dotnet run --project src/Ankus.Build -c Release -- binding-header-types symbols.txt 18 /path/to/pg_config artifacts/binding-header-types/pg18
```

This developer command requires `clang` on Linux/macOS or `clang-cl.exe` on
Windows. Optional arguments select the Clang executable, Windows library
directories, expected runtime identifier and compiler target triple, in that
order. Windows uses the Visual Studio and SDK include directories. Collection
uses a frontend only, without linking or running target code; matching headers,
compiler includes and target configuration are still required.

Header collection requires Clang 20 or later with the declaration-only
`-skip-function-bodies` frontend option. Select that compiler explicitly or put
it on `PATH`; an older platform-default Clang does not support this command.

The command writes `native-header-types.c`, the compiler's
`native-header-types.ast.json`, reconstructed `native-header-checks.c`, compiler
output in `native-header-checks.txt`, and a normalized `native-header-types.json`.
The AST is a development artifact containing local header paths; the normalized
contract excludes source paths and compiler pointer identities. Its target records
the exact PostgreSQL version, runtime identifier, pointer width, byte order and
Clang major. Its numeric model records plain-char signedness, `wchar_t` size and
signedness, floating radix, and the precision and exponent limits of `float`,
`double` and `long double`. These compiler observations distinguish numeric
representations that have identical object byte sizes. The type graph retains
native typedefs and anonymous records/enums,
qualifiers at each pointer level, fixed/incomplete arrays, both written and
adjusted parameter types, callbacks, variadic/prototype distinctions and no-return
metadata. Functions also retain parameter names and linkage; globals retain
their declared types and thread-local status.
Records and enums retain whether a complete declaration is available; implicit
compiler tags without an exposed declaration keep that state unknown.

Clang inspects declarations and constant expressions without compiling inline
implementation bodies, which can depend on the server's original compiler dialect.
Warnings remain errors. It checks reconstructed declarations against the same headers before
the command writes the final contract. Unsupported type kinds/calling conventions,
invalid observations, target mismatches and compiler errors fail explicitly.
Existing final contracts survive failures before that final write; intermediate
diagnostic files may be replaced. Unlike the reference-prototype probe, collection
can retain minor-release prototype changes, native volatile qualifiers and
anonymous typedefs directly from the selected installation.

Header types do not establish native record layouts, scalar widths, exported
symbol availability, managed calling conventions, pointer ownership or backend
error guards. Integrating these facts with layout measurement, managed call
generation, global access and hook registration remains port work. The consumer
SDK's existing node layout generation is unchanged.

No-return metadata reports an annotation in the selected declaration or type.
It is not inferred from a function's name or implementation. For example,
PostgreSQL 17's MSVC headers omit the `proc_exit` annotation, while PostgreSQL 18
declares it with C11 `_Noreturn`. An absent annotation does not prove that the
function returns.

To measure storage from those authoritative types, run:

```text
dotnet run --project src/Ankus.Build -c Release -- binding-storage symbols.txt 18 /path/to/pg_config artifacts/binding-storage/pg18
```

This command accepts the same optional Clang/toolchain arguments as
`binding-header-types`, collects the semantic contract, then evaluates constants in
`native-storage.c` with the same frontend. It does not execute a target program.
Unevaluated prototype checks and storage expressions do not call PostgreSQL
functions or require backend exports. The observed PostgreSQL version, runtime,
pointer width, byte order, Clang major and numeric model must exactly match the
collected contract.

`native-storage.ast.json` retains the compiler output and `native-storage.txt`
contains the normalized observations. Its version-2 header carries every numeric
identity field; regenerate earlier observations instead of reusing them. The validated
`native-storage.json` retains the header contract and ordered measurements for
fixed parameters, non-void results and globals. Each value records byte size,
alignment, array element stride and integer/enum signedness where applicable.
Alignment can exceed a typedef's size. Incomplete arrays have a null total size
with measured alignment and stride. Opaque records/enums have null size and
alignment; they are not represented as zero-sized objects. A pointer to an opaque
type still has measured pointer storage. A non-void opaque result remains a
result entry with unknown storage, distinct from a void result.
Large selections use batches of 256 symbols with numbered C/AST diagnostic files;
every batch retains the same 512 MiB compiler-output limit and must independently
match the selected target before the final combined contract is written.

Missing, duplicate, extra, contradictory or malformed observations fail before
the final storage contract is written. Intermediate type/diagnostic artifacts may
be replaced. These measurements do not classify a managed aggregate ABI, supply
record fields, establish pointer ownership, promote variadic call-site arguments,
or implement guarded calls and hooks.

To collect the complete transitive record graph for selected symbols, run:

```text
dotnet run --project src/Ankus.Build -c Release -- binding-records symbols.txt 18 /path/to/pg_config artifacts/binding-records/pg18
```

It accepts the same optional compiler, Windows library directories, runtime and
target arguments as `binding-header-types`, followed by an optional absolute
`libclang` library path. Empty strings retain the defaults for earlier optional
arguments. Install the matching library with the compiler: `libclang-20-dev`
beside `clang-20` on Linux, Homebrew `llvm@20` on macOS, or the Windows LLVM
installer. Automatic discovery uses the selected compiler's resource directory,
so compiler shims retain their selected toolchain. A library from an incompatible
compiler build cannot read its serialized AST and fails explicitly.

The command first validates the semantic symbol contract, then saves
`native-header-records.ast` using exactly the same compiler driver, includes,
target and warning settings. A separate process loads that AST and collects
record metadata; it does not reinterpret the headers with different compiler
options. Cancellation terminates the worker and waits for its exit. The AST and
worker request/observation files are diagnostic artifacts that can contain local
paths. The final `native-records.json` couples the existing symbol signatures with
a validated graph whose identities are numeric indices, not native addresses or
source paths. A failed operation preserves the previous final record contract.

The graph retains declared and canonical types, typedef annotations, exact native
size/alignment, array/vector extents, function ABI shapes, struct/union/enum
identity, ordered physical fields, anonymous containers, bit offsets and widths,
flexible arrays and exact signed/unsigned enum values up to 64 bits. Unsupported
enum representations fail instead of truncating values. Anonymous typedefs do
not acquire fabricated C tag names. Padded vectors and over-aligned typedefs
retain their actual storage. Incomplete declarations have unknown storage;
pointers to them still have measured pointer storage. Callback ABI observations
and compiler-printed annotations accompany the richer existing signature
contracts; they do not replace written parameter and declaration metadata.

These facts do not yet classify managed aggregate calls, establish exports or
ownership, transport callbacks across error boundaries, generate promoted
variadic arguments, or implement raw globals and hooks. The consumer SDK's node
generation is unchanged; the complete PostgreSQL-major/platform matrix remains
required port work.

To generate checked native call bodies for a list of fixed-prototype functions:

```text
dotnet run --project src/Ankus.Build -c Release -- binding-call-sources symbols.txt 18 /path/to/pg_config artifacts/binding-calls/pg18
```

This accepts the same arguments and compiler/library prerequisites as
`binding-records`, followed by an optional native body compiler. Clang collects
the declarations; native body compilation defaults to MSVC on Windows, matching
the SDK, and to the selected Clang elsewhere. An explicit MSVC executable must
target the measured architecture and numeric model; generated checks reject a
mismatch, including changed char signedness or long-double precision. Both
compilation stages retain warnings as errors. It writes `native-calls.c` after
compiling the complete generated bodies, including their calls, against the
selected headers. A compiler
failure or cancellation preserves the previous final source. Declaration-only
inspection remains separate from complete-body validation.

Each `ankus_native_call_<symbol>` body accepts native argument addresses and exact
byte lengths, plus a result destination. It checks the complete envelope before
calling the function. Argument addresses must satisfy the measured native
alignment and hold valid C object representations with live referenced storage.
Pointer values remain borrowed, including null pointers where the underlying
native function permits them. The C compiler performs the actual call with its
native scalar/aggregate ABI; there is no managed aggregate calling-convention
guess. Result bytes are copied only after the native function returns. Empty
records retain the selected compiler's size, including zero where applicable.

These bodies are an internal prerequisite for guarded raw bindings. They must
execute beneath the native PostgreSQL error guard on the backend thread, with
owned diagnostic transport and managed unwinding outside that guard. They are
not directly callable managed imports. Callback addresses here refer to native
callbacks; generated managed callback guards and lifetimes remain required.
Variadic and unprototyped calls need explicit call-site type/promotion handling;
globals are not function calls. Those selections and incomplete by-value storage
fail explicitly. Export discovery, guard/consumer integration and the complete
PostgreSQL-major/platform matrix remain required port work.

These declarations provide native fields, enums, embedded values, inline arrays
and explicit flexible-tail access. Checked node ownership/casting/formatting APIs
are available through the runtime; typed pointer/callback fields remain port work.
