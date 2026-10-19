---
title: .NET support
description: Choose a .NET version and keep the runtime embedded in your extension up to date.
---

Ankus targets .NET 10 LTS by default. Extensions can also target .NET 11
(`net11.0`), which Ankus currently builds on the .NET 11 release candidate; see
[.NET 11 status](#net-11-status) for what has been validated.

## Version policy

Ankus plans to support both LTS and STS .NET releases, starting with .NET 10.
Each new major must pass Ankus's native runtime and PostgreSQL validation before
it is listed as supported. A successful managed build alone does not establish
extension compatibility.

Once declared supported, a .NET major will remain serviced in a supported Ankus
release line until that .NET major reaches Microsoft's end of support. Use the
latest servicing release of that Ankus line. Adding .NET 11 will not remove
.NET 10 support or require existing extensions to retarget immediately.

As checked on October 14, 2026, Microsoft's policy provides three years for
LTS releases and two years for STS releases, with current patches required.
.NET 10 support ends November 14, 2028. .NET 11 is currently at RC2, with general
availability expected in November 2026. Microsoft's go-live status for its
release candidates does not establish Ankus support. See the [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
for current dates.

Ankus releases use their own version numbers independently of .NET. There is
no planned .NET Framework, .NET Standard extension target, or backport to .NET
8/9. Ankus's native runtime integration requires an explicitly supported modern
.NET target. This is Ankus's project support policy; Microsoft's support does
not cover Ankus's runtime modifications.

## SDK, target framework, and embedded runtime

These versions serve different purposes:

| Selection | What it controls |
| --- | --- |
| .NET SDK in `global.json` | The build tools and C# compiler used by your project |
| `TargetFramework`, `net10.0` or `net11.0` | The .NET API contract your extension compiles against, and which patched runtime it embeds |
| `Ankus.Sdk` package version | Matching Ankus packages and the selected Native AOT compiler/runtime payload |
| PostgreSQL major and runtime identifier | The server ABI, operating system, and architecture of the native library |

Generated projects select stable .NET 10 SDKs with `rollForward: latestFeature`
and `allowPrerelease: false`. A .NET 11 SDK can coexist for other projects without
changing that selection. Installing a newer SDK does not retarget your extension
or update its embedded runtime. `ankus new --framework net11.0` (or the same
template option) instead targets `net11.0` and pins SDK 11.0.100-rc.2.26504.105
with prerelease SDKs allowed, since .NET 11 has not shipped yet. See
[.NET SDK selection](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json).

SDK 11 RC2 targeting `net10.0` passes the complete test suite against
PostgreSQL 18.6 on Linux x64, using the .NET 10 compiler and embedded runtime.
SDK 11 can publish Native AOT without a full build, but in RC2 that path loses
an extension's documentation file, so Ankus keeps `UseOptimizedPublish` off
unless your project turns it on.

Ankus selects the matching runtime and compiler from your `TargetFramework`;
extension authors do not build a runtime fork or override compiler internals. The command-line tool,
native binding helper, and managed test host also need their declared .NET runtime
on the development machine. The selected SDK's compiler uses that SDK's own
runtime requirements. Installing a newer SDK does not replace the .NET 10 runtime
required by Ankus's managed tools. PostgreSQL servers do not need a separately
installed .NET runtime.

## .NET 11 status

The `net11.0` runtime carries every Ankus runtime change, adapted where .NET 11
reorganized the collector, thread pool and diagnostics. It passes the runtime's
own fork probes on Linux x64, Linux ARM64, macOS ARM64, Windows x64 and Intel
macOS. With every repository extension and the framework-template consumers
targeting `net11.0`, the complete Ankus test suite passes on every supported
PostgreSQL major, 13 through 19, on Linux x64, macOS ARM64, Windows x64 and Intel
macOS, and on Linux ARM64.

Before `net11.0` is listed as supported, it must pass again on the .NET 11
release that replaces the release candidate. Until then, treat
`net11.0` extensions as previews and keep production extensions on `net10.0`.

## Runtime servicing and deployment

Ankus includes a patched Native AOT runtime for PostgreSQL process and preload
behavior. The SDK automatically restores a matching patched Native AOT compiler
for the build machine and runtime for the extension's target platform. These
packages are versioned together, and each target framework has its own: a
`net11.0` extension embeds .NET 11's runtime with the same patch, matching its
.NET 11 compiler.

Ankus also selects the matching framework-library patch. Projects targeting an
unsupported framework or overriding `RuntimeFrameworkVersion` to a different
patch fail before publication. Update Ankus packages to service the embedded
runtime; changing that property does not update Ankus's patched runtime.

Native AOT embeds runtime code into the published library. Updating .NET on a
server does not update an already published extension. See Microsoft's
[Native AOT deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

When Ankus ships a runtime servicing update:

1. Update the pinned `Ankus.Sdk` and any explicitly referenced Ankus packages to
   the matching supported release, and update the build SDK as instructed by its
   release notes.
2. Restore and republish the extension for every deployed PostgreSQL major and
   operating system/architecture. Test the resulting artifacts.
3. Stop the PostgreSQL processes that loaded the old libraries before replacing
   them, then restart those processes. Shared-preload extensions require stopping
   and restarting PostgreSQL. Replacing a library on disk does not replace code
   already loaded into a backend.

A runtime patch update alone does not imply a SQL schema upgrade. Follow your
extension's upgrade instructions if its SQL or stored-data format also changes.
