---
title: Publish and install
description: Publish an extension library and load it into PostgreSQL.
---

Publish your configured extension project on the machine that matches the
target server's operating system and architecture, or
[build for another architecture](#build-for-another-architecture) from information
captured on the target.

## Prerequisites

- .NET 10 SDK. See [.NET support](/reference/dotnet-support/) for supported
  targets and runtime servicing.
- The [.NET Native AOT toolchain](https://learn.microsoft.com/dotnet/core/deploying/native-aot/).
- LLVM Clang 20 or later and its matching `libclang` library. Ankus uses it to
  read native declarations from your PostgreSQL headers. It uses the first
  Clang 20 or later it finds: `clang` (`clang-cl.exe` on Windows) on `PATH`,
  then the standard LLVM locations. On macOS, Xcode's Apple clang is too old;
  install LLVM with `brew install llvm` or MacPorts' `port install clang-20`,
  and Ankus finds it even though neither puts it on `PATH`. On Linux it also
  finds `clang-N` and `/usr/lib/llvm-N`.
  Windows also uses LLVM's `llvm-objcopy.exe` for native binding verification,
  from `PATH` or LLVM's standard installation; the LLVM installer supplies it.
- PostgreSQL 13 through 18 or the 19 beta or release candidate (this guide uses 18), with `pg_config`, server
  executables, and development headers.
  Windows also needs the PostgreSQL server import library. A client-only package,
  such as Arch Linux's `postgresql-libs`, installs `pg_config` without the server
  headers; Ankus does not select such an installation.

Register the installation with the Ankus tool:

```console
dotnet tool install --global Ankus.Tool --version 0.2.2
ankus init --pg18 /path/to/postgresql/bin/pg_config
```

## Publish

From your extension project or generated solution directory:

```console
ankus publish --output publish
```

Ankus uses .NET Native AOT to build for the current platform. It passes the
registered PostgreSQL installation to the build so the wrapper uses its headers.

Add repeatable `--property Name=Value` options to select project features or other
MSBuild settings. They apply to project evaluation and compilation together:

```console
ankus publish --property Configuration=Shipping --property ExtensionFlavor=preview --output publish
```

See [MSBuild property forwarding](/reference/cli/#pass-msbuild-properties) for
literal values, selection conflicts and commands that reuse existing publications.

You can also use `dotnet publish` directly. For a generated `Hello` solution on Linux x64:

```console
dotnet publish src/Hello/Hello.csproj -c Release -r linux-x64 -o publish -p:AnkusPostgresMajor=18 -p:AnkusPgConfigPath=/path/to/pg_config
```

The SDK supplies Native AOT settings and native build integration. No Ankus source
checkout is needed, and the server does not need an installed .NET runtime.

### Custom build configurations

Ankus defaults to `Release`. Use `--configuration` (or `-c`) to select `Debug`
or a custom MSBuild configuration:

```xml
<PropertyGroup Condition="'$(Configuration)' == 'Shipping'">
  <Optimize>true</Optimize>
  <AnkusExtensionVersion>0.2.0</AnkusExtensionVersion>
</PropertyGroup>
```

```console
ankus publish --configuration Shipping
ankus package --configuration Shipping --output dist
```

Custom configurations use your project's settings; they do not automatically
inherit `Release` settings. The default publication directory includes the
configuration name, keeping configurations separate. Use the same name with
`schema --no-build` or `run --no-build` to reuse that publication.

`build`, `publish`, `install`, `package`, `schema`, `run`, `connect`, `get`,
`regress` and `bench` share this option; `test` accepts it too, with a `Debug` default. Names may include spaces and Unicode; quote names containing spaces.
Each name must fit one directory component, without path separators, invalid
filename characters, or a trailing dot or space.

### Published files

For a project named `Hello`, with extension name `hello` and version `0.1.0`, the
output includes:

```text
Hello.so
Hello.sql
ankus.extension.json
extension/
  hello.control
  hello--0.1.0.sql
```

The library suffix is `.dll` on Windows. On macOS it is `.so` for PostgreSQL
13–15 and `.dylib` for PostgreSQL 16 and later, matching PostgreSQL's loader.

Native AOT writes the library's debug symbols beside it: `Hello.so.dbg` on
Linux, `Hello.pdb` on Windows and a `.dSYM` bundle on macOS. They carry source
lines for your code and for the Ankus framework, whose packages embed their
symbols, so a native debugger or crash dump can show both. Installation and
packages copy only the library; keep the symbols with the build that produced it.

An authored control `directory` setting changes the installed SQL location.
The publication still keeps its SQL and control files together under
`extension/`; installation and packaging apply the declared layout. See
[SQL directories](/reference/build-settings/#sql-directories).

## Build for another architecture

Native AOT can build for another architecture of the same operating system.
Ankus provides runtimes for `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64` and
`osx-x64`. Building for the other architecture is tested between the two macOS
architectures, such as building Intel macOS extensions on Apple silicon. Ankus
also needs the target's PostgreSQL
headers and the native layouts measured from them. It measures layouts by
compiling and running small C programs, which must run on the target, so the
target machine captures both once. pgrx's `cargo pgrx cross pgrx-target` captures
only its generated bindings and takes headers from a sysroot; an Ankus archive
also carries the headers it was measured against.

On the target machine, from your extension project:

```console
ankus target export --pg 18
```

This writes `ankus-target.pg18.osx-x64.zip`, named for the major and the target's
runtime identifier. It holds the `pg_config` values a build reads, the server
headers, headers from other libraries found through the search paths in
`pg_config --cppflags`, such as Homebrew's ICU, and the measured bindings. On
Windows it also holds the whole include directory and `postgres.lib`. Headers in
system directories, such as `/usr/include`, come from the build machine. The
target needs the same prerequisites as an ordinary build, but the export does
not build your extension.

On the build machine:

```console
ankus package --target-info ankus-target.pg18.osx-x64.zip --output dist
```

`ankus build` and `ankus publish` accept `--target-info` too. It selects the
archive's runtime identifier and PostgreSQL major, and a package mirrors the
target's library and share directories. The build machine does not discover
declarations or measure layouts, so it needs neither LLVM Clang 20 nor `libclang`
nor a PostgreSQL installation. It still compiles the native wrapper against the
archived headers with Native AOT's own compiler and linker, which must build for
the target architecture; macOS SDKs cover both. With `dotnet publish`, select the
runtime and the archive:

```console
dotnet publish -c Release -r osx-x64 -p:AnkusTargetInfo=ankus-target.pg18.osx-x64.zip
```

An archive is extracted once, into a directory named by its hash, and every file
is checked against the hash the export recorded before each build uses it. Builds
reject information for another runtime or major, or from another Ankus version:
export again after upgrading Ankus. Run tests on the target machine; `ankus test`
and `ankus run` need a local server and do not accept target information.

Building from target information is tested on Linux x64 and Windows x64 with the
same architecture on both sides, and for Intel macOS from Apple silicon, where
the result loads into PostgreSQL on an Intel Mac.

## Inspect installation SQL

Build your extension and write its installation SQL to a file:

```console
ankus schema --output schema.sql
```

Without `--output`, SQL goes to stdout and build diagnostics go to stderr.
Use the same `--project`, `--pg`, `--pg-config`, and `--configuration` options
as `ankus publish`. `ankus schema --no-build` reads the project's existing
default publication under `bin/ankus/`, selected by PostgreSQL major, host
runtime, and configuration. It does not require a registered PostgreSQL
installation; use `--pg` to select the existing publication's major.

To read a library published to another directory, use its path directly:

```console
ankus schema --from publish/Hello.so --output schema.sql
```

This reads the library's embedded metadata without loading its native code,
building the project, or requiring adjacent SQL, control, or JSON files. Use
`.dll` on Windows, or `.dylib` on macOS with PostgreSQL 16 and later. macOS
PostgreSQL 13–15 uses `.so`. You can inspect another supported
platform's library; `--runtime osx-arm64` or `--runtime osx-x64` selects a slice
from a universal macOS library. For a thin library, `--runtime` checks its target.
Do not combine `--from` with project, configuration, PostgreSQL, or build options.

The output is the complete installation script, including custom SQL and
declarations in dependency order. It retains PostgreSQL's `MODULE_PATHNAME`
marker, which `CREATE EXTENSION` resolves through the control file. Extracting
SQL does not install the extension or replace those installation files.

Generated scripts include a header and comments identifying each declaration's
physical source file and line, managed name and resolved prerequisites. Source
paths are relative to the project; files outside the project show only their
filename. SQL file declarations also identify the authored SQL file. The
connected-object markers separate installation steps without changing their
deterministic dependency order.

These comments stay in the library's embedded schema and in selected scripts.
They help trace SQL changes back to C# or authored SQL. Change those sources and
regenerate the script; source line comments reflect the build that produced the
library. Older libraries remain readable with the metadata they originally
contained.

### Select declarations

Pass one or more SQL names, managed declaration names, signatures, or explicit
`Id` values to emit just those declarations and their prerequisites:

```console
ankus schema --from publish/Hello.so add --output selected.sql
ankus schema --from publish/Hello.so Hello.Functions.Add --no-alter-extension
```

Names are case-sensitive. Ambiguous names require a qualified name, exact SQL
signature, or unique `Id`. Selecting a function also includes its declared
operators and casts. Shared prerequisites appear once, in installation order.

Selected scripts replace `MODULE_PATHNAME` with the published library under
PostgreSQL's `$libdir`. Install that library before running the script. By default,
the script wraps creation and `ALTER EXTENSION ... ADD` statements in one
transaction. The extension must already exist; use this to add new objects to it.
Prerequisites are emitted too, so check the selected script before applying it
to a database containing those objects.

`--no-alter-extension` emits creation SQL without a transaction or extension
attachments. A fixed control schema qualifies generated object names and owned
type references even when the session uses another search path. Authored SQL
retains its own identifiers and qualification.

For an extension installed into a schema chosen at installation time, pass that
schema when selecting declarations:

```console
ankus schema --from publish/Hello.so read_answer --schema reporting --output selected.sql
```

`--schema` qualifies generated object names and resolves reserved `@extschema@`
tokens, including paths declared with `PgSearchPath.ExtensionSchema`. It must
match the installed extension's schema and any fixed control schema. Without this
option, a fixed control schema supplies the target. Selection fails if its SQL
needs `@extschema@` and no target is known. Full installation scripts retain the
token for PostgreSQL to resolve; `--schema` requires named selection.

Code using `ExtensionSchema` can call `schema.Select(["read_answer"], "reporting")`
for the same behavior. The existing overload still uses fixed schema metadata.
Both overloads accept `alterExtension: false` to omit extension attachments.

Custom SQL types declared with `PgSqlTypeProvider` and functions declared with
`PgSqlFunctionProvider` receive attachments. A custom block without a declared
object inventory produces a warning; attach its objects yourself. Replacement
SQL must create the declared object identities to use automatic attachment.
See [custom SQL](/custom-sql/#declare-supplied-functions).

### Export dependencies

Use `--dot` to write the complete dependency graph, including when selecting SQL:

```console
ankus schema --from publish/Hello.so add --dot dependencies.dot --output selected.sql
```

The Graphviz DOT file labels declarations and draws edges from prerequisites to
the objects that depend on them. SQL and graph destinations must differ from
each other and from the input library. Invalid selections preserve existing
output files.

Older libraries containing only flat SQL still support full extraction. Rebuild
them with a current Ankus SDK before requesting item selection or a graph.

## Load into PostgreSQL

Install the published files:

```console
ankus install --from publish
```

Then connect to your database:

```sql
CREATE EXTENSION hello;
SELECT public.add(40, 2);       -- 42
SELECT public.greet('world');   -- Hello, world!
```

See [build settings](/reference/build-settings/) to change the extension name or
version.

## Upgrade an existing extension

Changing `AnkusExtensionVersion` creates a new installation script. Existing
databases also need an upgrade script that changes their current SQL objects.
Place it in your extension project's `sql/` directory:

```text
sql/
  hello--0.1.0--0.2.0.sql
```

For example, an extension that already owns a `hello_settings` table could add
a column with:

```sql
ALTER TABLE hello_settings ADD COLUMN enabled boolean NOT NULL DEFAULT true;
```

Set `AnkusExtensionVersion` to `0.2.0`, publish and install the new files, then
update each database:

```console
ankus install --pg 18
```

```sql
ALTER EXTENSION hello UPDATE TO '0.2.0';
```

`dotnet publish`, `ankus publish`, `ankus install`, and `ankus package` include
the selected upgrade scripts. PostgreSQL chooses the upgrade path, including
multiple intermediate scripts. Version names are literal PostgreSQL versions;
they need not follow semantic versioning. PostgreSQL rolls back SQL changes
when an upgrade fails.

Write scripts as UTF-8. Ankus replaces `@EXTENSION_VERSION@` with the configured
extension version and `@GIT_HASH@` with the project's current commit. Git is
required only when a script uses that token. PostgreSQL resolves
`MODULE_PATHNAME` through the new control file when it executes the script.

A function's native export name depends on the assembly name, the method's
declaring type and its signature. The version plays no part, so functions you leave
unchanged keep working when a new library replaces the old one in place.

When native functions change, give the new version its own library and replace
their SQL declarations in the upgrade script with
`CREATE OR REPLACE FUNCTION ... AS 'MODULE_PATHNAME', 'native_export'`.
Use the matching generated installation SQL for the actual declarations and
export names. Already connected backends can then load the new library while
retaining references to the old one.

Set [`AnkusVersionedLibrary`](/reference/build-settings/#versioned-libraries) to
name each version's library automatically, such as `Hello-0.2.0.so`. Its
installation SQL names that library directly, so earlier versions remain
installable beside it. In an upgrade script, `MODULE_PATHNAME` refers to the
library of the version the script updates to, which also lets a downgrade script
return to the earlier library. Alternatively, choose a distinct `AnkusLibraryName`
or `AssemblyName`, such as `Hello.0.2.0`, for each version. Ankus does not
generate migration SQL.

Default discovery includes matching files directly under `sql/`. To remove a
default item or include a script from elsewhere:

```xml
<ItemGroup>
  <AnkusUpgradeScript Remove="sql/hello--0.1.0--0.2.0.sql" />
  <AnkusUpgradeScript Include="migrations/hello--0.1.0--0.2.0.sql" />
</ItemGroup>
```

Set `EnableDefaultAnkusUpgradeScripts` to `false` to select every script
explicitly. Duplicate output names and invalid upgrade filenames fail the
publish. A successful republish removes obsolete SQL files owned by the previous
publication and preserves unrelated files. Installation preserves previously
installed versions so databases can still use their existing libraries.

When an upgrade changes dependencies or installation permissions, add
`sql/hello--0.2.0.control`. PostgreSQL uses that version's overrides for both
installation and updates. Publishing, installing and packaging include these
files. See [version-specific control files](/reference/build-settings/#version-specific-control-files)
for inheritance, item selection and native-library settings.
