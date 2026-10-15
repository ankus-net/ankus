# Contributing

Ankus ports [pgrx](https://github.com/pgcentralfoundation/pgrx) to .NET Native AOT, so most changes touch C#, the
C that crosses into PostgreSQL, and the build that joins them. The first build takes some setup. This page lists the
steps and where each one is explained.

## Set up

1. Install the .NET SDK that `global.json` selects, the platform's
   [Native AOT toolchain](https://learn.microsoft.com/dotnet/core/deploying/native-aot/), PostgreSQL 18 with its server
   development headers, and LLVM Clang 20 or later with `libclang`. Linux also needs Valgrind and `libc6-dbg`.
   [Development and testing](docs/contributing/development.md) has the complete list for each platform.
2. Extensions embed a patched Native AOT runtime. A checkout restores it from nuget.org when its runtime version has
   been released; after the runtime fork moves past the last release, stage it from a CI run as
   [Stage the patched Native AOT runtime](docs/contributing/development.md#stage-the-patched-native-aot-runtime)
   describes.
3. Build and test from the repository root:

   ```console
   dotnet build
   dotnet test
   ```

   The tests publish real extensions and run them in PostgreSQL servers they start, so the full suite takes a while.
   Tests that cannot run on your platform report themselves as skipped.

## Before changing code

Read the [README](README.md), the [guide](docs/src/content/docs) for the area, and its entries in
[PROGRESS.md](PROGRESS.md), which records what is done, how it was verified and what remains. pgrx is the reference
for APIs and tests, and PostgreSQL's source is the reference for backend behavior. Before touching code that calls
into PostgreSQL, read [the native boundary](docs/contributing/native-boundary.md): a PostgreSQL error must never
unwind through a managed frame.

## Code

The build enforces the conventions through `.editorconfig` and the analyzers, and warnings are errors. Fix what it
reports; never suppress a diagnostic with a pragma, an attribute, `NoWarn` or a lower severity. [AGENTS.md](AGENTS.md)
lists the conventions, among them explicit local types, braces on their own lines, and XML documentation on public and
internal declarations.

New NuGet dependencies must come from Microsoft or .NET. Repository automation is .NET file-based apps under `eng/`,
listed in [eng/README.md](eng/README.md), rather than shell or PowerShell scripts.

## Tests

Test the way pgrx does: check values and helpers directly, check generated contracts and diagnostics, and run the
published extension in PostgreSQL to prove what the backend does. Check observable behavior, such as errors,
ownership and recovery in the same session, rather than test counts or generated text alone. Run the affected tests
while you work, then the whole suite.

## Documentation

Update the guides under `docs/src/content/docs` and the README for anything an extension author would notice.
Regenerate the API reference with `dotnet run --project src/Ankus.DocGenerator -c Release` instead of editing it, and
check the site with `pnpm build` and `pnpm check` in `docs/`. See [the documentation site](docs/contributing/site.md).

## Pull requests

Describe the change in a few plain sentences. CI runs the complete suite against real PostgreSQL servers on Linux
x64, macOS ARM64 and Windows x64. CodeQL and the secret scan must report no findings. The few CodeQL results that
describe code which must stay as it is carry a `// codeql[rule-id]` comment saying why; see
[Security checks](docs/contributing/development.md#security-checks).

Report a security problem privately, as [SECURITY.md](SECURITY.md) describes, not in an issue.
