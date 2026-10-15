# .NET compatibility and runtime servicing

The [public .NET support policy](../src/content/docs/reference/dotnet-support.md)
describes the extension-author contract. This document records the implementation
plan and evidence required to make that contract supportable. SDK 11 RC1 has
Linux evidence for the .NET 10 target; full .NET 11 acceptance remains open.

## Basis for the policy

Microsoft's [library targeting guidance](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/cross-platform-targeting)
distinguishes broad managed-library compatibility from libraries using modern
APIs and Native AOT. Ankus belongs to the latter category. Do not add old TFMs
or .NET Standard solely to increase the apparent support range.

[EF Core](https://learn.microsoft.com/en-us/ef/core/what-is-new/) aligns servicing
with .NET lifecycles while choosing its target framework separately: EF Core 9
targets .NET 8, whereas EF Core 10 targets .NET 10.
[Npgsql 10](https://www.npgsql.org/doc/release-notes/10.0.html) drops the already
unsupported .NET 6 target to reduce compatibility code. These examples support
explicit target and lifecycle decisions, not a rule that every package must
target every .NET release. Ankus additionally owns a native runtime fork, so
managed assembly compatibility cannot establish its support matrix.

Keep ordinary managed packages on the lowest supported target their APIs need.
Add multi-targeting where framework-specific APIs, dependencies, analyzers or
validation require it. Independently map every supported extension TFM to its
own validated Native AOT compiler and runtime identity. A `net10.0` assembly being
referenceable from `net11.0` does not validate that mapping.

## Current state and immediate requirements

The repository and generated projects select SDK 10.0.400 or a later stable
10.0 feature band/patch through `latestFeature`; previews are excluded. CI
installs the latest stable 10.0 SDK. This selection must remain separate from the
runtime included in published extensions.

`eng/Ankus.Ci.cs` pins upstream 10.0.13 as shipped (dotnet/dotnet
`4099f83977f4d29604a9aeb971fb0a7bc1d5f699`, whose `src/runtime` is dotnet/runtime
`6aa72daa24f66c01c3d7b6a32519e9d21396deec` plus the release's security changes),
fork commit `f97c4dea9e1cab1f4afcbd8d9c67eb5f7d88a492`, runtime and compiler-host packages
`10.0.13-ankus.1`, and ILCompiler targets/framework packs `10.0.13`. The fork's
last commit only restores two build tasks with the patched
`System.Security.Cryptography.Pkcs` 10.0.13, since NuGet's audit now rejects the
9.0.6 that `NuGet.Packaging` brings; the runtime and compiler it builds are
unchanged. The SDK
target and per-RID package properties carry matching target runtime and host
compiler identities. Framework runtime packs also use 10.0.13 instead of
following the installed SDK's default patch. Check
`PROGRESS.md` for completed platform evidence and recheck upstream servicing
before publishing `0.1.0`.

The runtime repository restores from dnceng staging feeds, which carry builds of
official package versions before or beside nuget.org's signed copies.
`runtime-build` gives the runtime build and its probes their own package folder,
and every CI command that restores for a consumer first removes any staging-feed
package from the shared cache, so consumer builds always use nuget.org's packages.

The compiler executable embeds Native AOT runtime code too. Building ILCompiler
from the fork does not automatically link the fork's runtime: its publish command
must explicitly select the freshly built `aotsdk` through `IlcSdkPath`.
`runtime-build` does this, builds the cross-target JIT libraries, and stages the
compiler alongside the target runtime. The SDK restores the compiler package for
the build host RID and the runtime package for the extension target RID.

The SDK checks compiler/package/RID agreement, rejects targets other than
`net10.0` before framework resolution, and rejects a conflicting
`RuntimeFrameworkVersion`, `IlcSdkPath` or `IlcToolsPath` override.
Packed-consumer tests inspect the actual compiler bytes, restored compiler and
framework-pack versions, and verify incompatible settings
fail before publication. Extend this explicit selection to a supported-TFM
table when introducing a second runtime line; an unknown target must never
fall back to the .NET 10 payload.

## Adding .NET 11

An isolated SDK **11.0.100-rc.1.26425.128** experiment targeting `net10.0`
found two binding-build defects. The helper forced MSBuild to use its own .NET 10
runtime, and the cache rejected symbolic links shipped inside the SDK. Binding
compilation now follows MSBuild's runtime configuration and selects the same SDK
installation for compiler apphosts. Its cache inventories that installation's
host and shared runtimes, and verifies linked dependency contents while requiring
regular owned output artifacts.

The real compiler-process and Native AOT accessor tests pin the SDK that built
the tests, including an isolated installation. Generated consumers retain their
own SDK selection. Their test environment clears the runner's inherited
`MSBuildSDKsPath` and `MSBuildExtensionsPath`; otherwise a .NET 10 consumer can
load .NET 11 build or NuGet tasks. The compiler/cache scope passes **17 tests**
under RC1, including offline restore, source-mapping rejection and recovery,
concurrency, content invalidation and linked-file cases. The five compiler/AOT
cases also pass on both SDKs with the shared SDK-selection helper.

Source analyzer corrections keep the generator on C# 14 and strengthen callback
fixtures without changing analyzer standards. All 16 generated-consumer
cases pass under RC1 after the environment correction, including the ordinary
and background-worker templates, initialization-failure cleanup and explicit
names. Final complete PostgreSQL 18.6/Linux x64 suites pass on SDK 10.0.400 and
RC1: **8,791 passes, zero failures and six Windows-only skips, 8,797 total**
on each, in **11m29.830s** and **12m04.798s** respectively. Both include all
**3,785 integration cases**, and both Release builds pass without warnings or
errors. Earlier interrupted preview runs are not completed evidence. These
results establish the Linux RC1 experiment; cross-platform and GA acceptance
and the separate `net11.0` runtime target remain required.

SDK **11.0.100-rc.2.26504.105**, released on 2026-10-13 and still targeting
`net10.0`, changed three things Ankus relied on. It publishes Native AOT without
the full build (`UseOptimizedPublish`), which leaves the output directory empty
but still copies the documentation file from it; a plain `PublishAot` console
app that generates documentation fails the same way. `Ankus.Sdk` therefore
defaults `UseOptimizedPublish` to `false`, so extensions publish as they do on
SDK 10. `dotnet sln add` now rejects a project that another project's reference
adds a second time, which one test's setup did, and its analyzers report
`JsonDocument.Parse(...).RootElement` as CA2026, which one test also did.
Generated consumers keep SDK 10 through their `global.json`; the test fixture
now publishes with the SDK the extension's directory selects instead of
inheriting the MSBuild paths of the SDK that ran the tests, and an offline
restore test offers the .NET 10 reference packs that SDK 11 downloads. With SDK
10.0.402 installed beside RC2, the complete PostgreSQL 18.6/Linux x64 suite
passes **14,956 of 15,010 with 52 platform skips**. The two failures are one
design-time build test's data rows, which see RC2's preview notice (NETSDK1057);
a release SDK does not print it. That test now ignores the notice, which the SDK
prints for every project on a preview.

The `net11.0` target is implemented. `Ankus.NativeAot.Lines.props` maps each
target framework to its runtime package, compiler host and upstream compiler
versions, and the runtime and compiler package projects pack one line per
`AnkusRuntimeLine`. `ankus new --framework net11.0` and the templates' `--framework`
option set the projects' framework and pin the matching SDK. .NET 11's Native AOT
adds the native library to the publish list under `TargetName`, so `Ankus.Sdk`
restores a custom library name there and publishes its symbols beside it. The
`.NET versions` workflow builds both lines and runs the complete suite with
`AnkusExtensionTargetFramework=net11.0`; its feed carries both lines because
most generated consumers still target `net10.0` while the framework-template
cases target `net11.0`. Releases build and verify every line's packages.

1. Use an isolated writable runtime checkout based on the selected .NET 11
   release tag/commit. Keep reference clones read-only. Review each existing
   patch against upstream changes; remove a local change only when upstream
   provides the required behavior and its tests pass.
2. Build matching runtime payloads for Linux x64, Windows x64, macOS ARM64 and
   macOS x64. Record the upstream base, fork commit, patch revision, ILCompiler
   version, target framework and RID. Never combine a .NET 10 payload with an
   .NET 11 compiler or let an unknown target fall back to the .NET 10 payload.
3. Extend the SDK's existing selection and identity checks for `net10.0` and
   `net11.0`. Audit framework references, runtime packs, compiler packages,
   generated binding companions and cache identities together. A changed runtime
   or compiler must invalidate affected native outputs.
4. Validate SDK 11 building `net10.0` separately from SDK 11 building `net11.0`.
   Exercise packed consumers, the installed tool, source generators, templates,
   native MTP tests and analyzer enforcement. Keep stable .NET 10 SDK builds
   working. Do not infer that a newer managed host is a replacement for the
   runtime required by the command-line tool or test host.
5. Run the complete unsharded suite against real PostgreSQL on every declared
   platform, including release-only macOS x64, and complete the required
   PostgreSQL major matrix. Pay particular attention to Unix preload/fork,
   Windows worker startup, GC/thread repair, signal handling, native error
   unwinding, cleanup and same-session recovery. Reusing the patch cleanly or
   passing a smoke test is insufficient.
6. Publish compatibility results as each platform passes. `ankus new` and the
   templates keep `net10.0`, the LTS release, as their default; `--framework
   net11.0` opts in and pins the matching SDK in `global.json`. Rebase the fork
   onto the GA release when it ships and repeat this gate on it. .NET 11 support
   is required work, not an optional experiment.

Start with explicit compatibility runs using existing automation. Add a small
scheduled/manual preview lane if useful; it must record failures without
weakening stable checks. Stable runtime/compiler changes require the affected
full platform suites. A newly supported .NET major becomes part of the required
CI/release matrix. Reuse caches, run independent jobs in parallel, retain the
60-minute primary-job limit and the approved 360-minute Intel macOS limit, and
measure durations; do not shard or substitute smoke
tests for platform evidence.

### Compiler memory-safety acceptance

The [C# memory-safety proposal](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/unsafe-evolution)
changes pointer syntax and, when enabled, the caller contract of `unsafe`
members. It also specifies compatibility behavior for assemblies compiled with
older rules. Recheck the shipping compiler; preview specifications are not
completed compatibility evidence.

The raw-binding review remains open: data pointers currently use `nint`, while
native callback validation rejects pointer-shaped signatures. Changing only
`NativeMethods` would leave globals, callback construction and transport
inconsistent. Preserve the native error guard and exact ABI while addressing
these surfaces together.

Before declaring the new compiler supported, verify:

- Raw calls, global addresses and callbacks expose their actual caller obligations,
  including pointer-free native signatures that still depend on backend state.
- Checked managed APIs remain usable without making their callers responsible
  for internal pointer operations.
- Old and new compiler consumers, generated binding assemblies, imports and
  explicit layouts compile under the applicable legacy and updated rules.
- Ordinary safe calls pass, invalid raw usage fails compilation, and valid native
  calls retain ownership, cancellation and same-session error recovery.

Do not manufacture compiler-owned safety attributes or weaken diagnostics to
make these checks pass. SDK compatibility and this compiler contract need
separate evidence from the embedded runtime patch.

## Servicing a supported runtime line

Review upstream servicing and security releases monthly and when an out-of-band
fix is announced. Track the latest upstream patch, the embedded patch and any
gap for each supported major. Prioritize security fixes; do not claim that a
custom payload receives Microsoft's automatic runtime updates.

Base each update on the runtime source Microsoft shipped. .NET 10 is built from
dotnet/dotnet: its `src/source-manifest.json` at the release commit names the
dotnet/runtime commit it took, and its `src/runtime` adds the release's security
fixes, which reach dotnet/runtime later. Commit that `src/runtime` tree on top of
the named dotnet/runtime commit and merge it into the fork. A dotnet/runtime
release tag alone can lack those fixes: shipped 10.0.12 changed 13 files beyond
its tag (HttpListener requests, WebSocket compression and EventPipe's diagnostic
socket), and `10.0.12-ankus.5`, built from the tag, did not have them.

A pinned runtime can also stop building after its release: the runtime build
treats NuGet's audit warnings as errors, so a new advisory against a package its
build tools restore fails a cold build. Reference the patched package from those
tools in the fork, as `f97c4de` did for `System.Security.Cryptography.Pkcs`,
rather than turning the audit off; the payloads they build do not change.

For each update, rebase the minimal patch, rebuild the native payloads and update
the pinned compiler/runtime identities together. Use immutable package versions
and new cache identities. Review the upstream changes and record exact platform,
PostgreSQL and package validation. Release notes must identify the embedded
runtime and tell extension authors to rebuild and redeploy. No fixed turnaround
SLA is promised; an unresolved security or compatibility blocker must be visible.

Retain each declared .NET major through its upstream end of support in at least
one supported Ankus release line. Prefer a shared codebase and create a servicing
branch only when necessary. Do not maintain every historical Ankus minor: users
must take the latest servicing update on their supported line. .NET support
removal is announced in release notes and occurs in a breaking release (a minor
release while Ankus is pre-1.0, a major release afterward), with an upgrade path.
If the newest line raises its minimum before an older .NET major reaches end of
support, its previous Ankus line still needs servicing through that date.

Keep reducing the fork where upstream improvements permit it. Removing the fork
requires the same PostgreSQL behavior evidence as introducing it; it is not a
prerequisite for supporting .NET 11.
