---
title: Build settings
description: Configure extension names, versions, PostgreSQL headers, and installation paths.
---

Set extension properties in your project file:

```xml
<PropertyGroup>
  <AnkusExtensionName>hello</AnkusExtensionName>
  <AnkusExtensionVersion>0.1.0</AnkusExtensionVersion>
</PropertyGroup>
```

| Property | Default | Purpose |
| --- | --- | --- |
| `AnkusExtensionName` | Lowercase assembly name, with periods replaced by underscores | Names the control and SQL files |
| `AnkusExtensionVersion` | Project `Version` | Selects the versioned SQL filename and control-file version |
| `AnkusExtensionControlFile` | None | Adds author settings from a PostgreSQL control file; see [control settings](#extension-control-settings) |
| `AnkusLibraryName` | `TargetName`, which defaults to `AssemblyName` | Names the native library file without its platform suffix; see [native library names](#native-library-names) |
| `AnkusVersionedLibrary` | `false` | Appends the extension version to the library name so versions install side by side; see [versioned libraries](#versioned-libraries) |
| `EnableDefaultAnkusUpgradeScripts` | Enabled | Includes `sql/<extension>--<old>--<new>.sql` upgrade files; set to `false` for explicit items only |
| `EnableDefaultAnkusVersionControlFiles` | Enabled | Includes `sql/<extension>--<version>.control` files; set to `false` for explicit items only |
| `AnkusPostgresMajor` | The major `AnkusPgConfigPath` or `AnkusTargetInfo` reports, otherwise `18` | Selects the server headers used to compile the native wrapper |
| `AnkusPgConfigPath` | Registered or discovered installation | Selects an exact `pg_config`; the tool sets this automatically |
| `AnkusTargetInfo` | None | Builds from another machine's exported information instead of a `pg_config`: an archive, its extracted directory or its `ankus-target.json`, relative to the project directory. `RuntimeIdentifier` must match the archive, an explicit `AnkusPostgresMajor` must match its major, and `AnkusPgConfigPath` cannot also be set; see [building for another architecture](/getting-started/publishing/#build-for-another-architecture) |
| `AnkusTargetInfoOutput` | `ankus-target.pg<major>.<rid>.zip` in the project directory | Names the archive the `AnkusExportTargetInfo` target writes when run directly with a `RuntimeIdentifier`; `ankus target export` passes its own `--output` |
| `AnkusIncludeTests` | Disabled | Includes `[PgTest]` native exports and installation SQL and defines `ANKUS_TESTS`; the testing fixture enables it for declared backend tests |
| `AnkusReuseSchema` | `false` | Reuses the last successful installation SQL and matching metadata for this target and publication mode while recompiling native code; missing or incompatible saved declarations fail explicitly |
| `AnkusClangPath` | `clang` on Linux/macOS; `clang-cl.exe` on Windows | Selects LLVM Clang 20 or later for native declaration discovery |
| `AnkusLibClangPath` | Matching library from the selected Clang installation | Selects `libclang` when it is installed separately |
| `AnkusBindingCacheDirectory` | Ankus's directory in the current user's local application data | Selects shared generated sources, compiled companions, native objects and extracted target information |

Relative `AnkusPgConfigPath` file paths use each project's directory, including
when the property comes from an imported file. A bare executable name, such as
`pg_config`, uses `PATH`. Ankus commands and the testing fixture use the
executable's reported major when `AnkusPostgresMajor` is omitted. A major set in
the project file, `Directory.Build.props` or `Directory.Build.targets` is an
explicit selection, so the executable's major is not substituted for it. A direct
`dotnet publish` also takes the major from `AnkusPgConfigPath` when no major is
set, so the path alone selects another major; without either property it uses 18. A test project without its own PostgreSQL
selection inherits the extension project's evaluated installation.

For a shared installation beside `Directory.Build.props`, anchor the path to that
file so projects in different directories select the same installation:

```xml
<PropertyGroup>
  <AnkusPgConfigPath>$(MSBuildThisFileDirectory)postgres/bin/pg_config</AnkusPgConfigPath>
</PropertyGroup>
```

Use `pg_config.exe` on Windows. An absolute installation path also works across
extension and test projects.

## Native module identity

On PostgreSQL 18 and later, Ankus includes the project's `AssemblyName` and
`Version` in its native module compatibility block. This preserves prerelease
version text. After the library is loaded, inspect it with:

```sql
SELECT module_name, version, file_name FROM pg_get_loaded_modules();
```

Use an assembly attribute to supply a different name or version:

```csharp
using Ankus;

[assembly: PgModule(Name = "Acme.Search", Version = "0.1.0-preview.1")]
```

Each omitted or `null` value retains its project default. Values preserve exact
UTF-8 text. Empty strings are preserved when explicitly declared.
`[assembly: PgModule]` also permits a loadable module with no SQL objects.

### Module identity diagnostics

Embedded zero characters cannot be represented in PostgreSQL's terminated
module identity strings. Malformed Unicode cannot be encoded as exact UTF-8.
Ankus rejects both without truncating or replacing the supplied text:

| Diagnostic | Invalid value | Correction |
| --- | --- | --- |
| `ANKUS350` | `Name` contains a zero character | Remove the embedded zero from the name |
| `ANKUS351` | `Name` contains an unpaired UTF-16 surrogate | Supply a well-formed Unicode name |
| `ANKUS352` | `Version` contains a zero character | Remove the embedded zero from the version |
| `ANKUS353` | `Version` contains an unpaired UTF-16 surrogate | Supply a well-formed Unicode version |

Attribute errors point to the invalid `Name` or `Version` expression, including
constant references. Invalid project defaults require correcting the project's
`AssemblyName` or `Version`. If both fields are invalid, both errors are reported.

This metadata describes the native library. SQL installation names and versions
still come from `AnkusExtensionName` and `AnkusExtensionVersion`. `AnkusLibraryName`
and `AnkusVersionedLibrary` change the library's filename, not this identity.
PostgreSQL 13–17 use their ordinary compatibility block and do not expose this
metadata. Every publication uses the ABI from its selected PostgreSQL headers.

## Native library names

The published library is named after `TargetName`, which defaults to
`AssemblyName`, plus PostgreSQL's platform suffix: `.so` on Linux, `.dll` on
Windows, and `.dylib` on macOS with PostgreSQL 16 and later (`.so` before).
Set `AnkusLibraryName` to choose another name, as Cargo's `[lib] name` does:

```xml
<PropertyGroup>
  <AnkusLibraryName>other_name</AnkusLibraryName>
</PropertyGroup>
```

This publishes `other_name.so`, and the generated control file's
`module_pathname` names that file. Only the native file changes: the assembly,
its [module identity](#native-module-identity), generated export names and the
default extension name still follow `AssemblyName` and `TargetName`. Changing
`AssemblyName` also renames the library, but changes those identities too.
Library names use ASCII letters, digits, `.`, `_`, `+` and `-`, without a
directory or suffix, and cannot start with `.`. See the
[custom library name sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.CustomLibraryName).

`AnkusOutputPluginAlias` items publish extra libraries that load the extension's
output plugin under other names, as pglogical's `pglogical_output` does. See
[loading the plugin under another name](/logical-decoding/#load-the-plugin-under-another-name).

### Versioned libraries

Set `AnkusVersionedLibrary` to install several versions of an extension side by
side, as cargo-pgrx's versioned shared-object mode does:

```xml
<PropertyGroup>
  <AnkusVersionedLibrary>true</AnkusVersionedLibrary>
</PropertyGroup>
```

The library name gains `-<version>`, using `AnkusExtensionVersion`: version
`0.2.0` of `Acme.Search` publishes `Acme.Search-0.2.0.so`, or `other_name-0.2.0.so`
with `AnkusLibraryName`. The generated control file has no `module_pathname`.
Instead, Ankus performs PostgreSQL's `MODULE_PATHNAME` substitution when it
publishes, replacing every occurrence in the installation script with
`Acme.Search-0.2.0`. PostgreSQL appends the platform suffix while searching
`dynamic_library_path`. The library's embedded schema and `ankus schema` contain
the same resolved script.

Installing a new version adds its library and SQL beside the earlier versions'.
Databases keep using their version's library until they update. In an upgrade
script, `sql/<extension>--<old>--<new>.sql`, `MODULE_PATHNAME` refers to the
library of `<new>`: the library PostgreSQL would substitute if that version's
control file named it. Copy each replaced function's declaration, including its
export name, from the generated installation SQL of `<new>`; export names change
with the assembly version. A [version-specific control file](#version-specific-control-files)
for `<new>` that sets `module_pathname` keeps PostgreSQL's own substitution for
scripts updating to that version, such as one published before this setting.

The current version cannot set `module_pathname` in an authored control file.
The version must be valid in a filename. `ankus get module_pathname` prints
nothing for a versioned publication. See the
[versioned library sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.VersionedLibrary)
and [upgrading an extension](/getting-started/publishing/#upgrade-an-existing-extension).

## Extension control settings

Select a control file relative to your project, or use an absolute path:

```xml
<PropertyGroup>
  <AnkusExtensionControlFile>extension.control</AnkusExtensionControlFile>
</PropertyGroup>
```

```ini
comment = 'Search helpers'
schema = 'search_helpers'
requires = 'pg_trgm'
superuser = true
trusted = false
```

Publishing merges these settings into the generated control file. Installation
and packaging preserve the result. `requires` names extensions that PostgreSQL
must install first; `CREATE EXTENSION ... CASCADE` can install available
dependencies. Use double quotes inside the value for mixed-case dependency names,
for example `requires = '"My Dependency", pg_trgm'`.

`schema` fixes the installation schema and makes the extension non-relocatable.
Without it, Ankus derives relocatability from generated SQL. You can explicitly
set `relocatable = false`, but cannot set it to `true` when generated SQL requires
a fixed schema.

`superuser` and `trusted` retain PostgreSQL's defaults when omitted: `true` and
`false`, respectively. `trusted = true` with `superuser = true` lets database
users with `CREATE` privilege install the extension; PostgreSQL executes its
script as the bootstrap superuser. With `superuser = false`, the caller must
have the privileges required by each SQL statement, including C-language
function creation. Extension authors are responsible for deciding whether their
extension is suitable for trusted installation.

PostgreSQL 16 and later also accept `no_relocate`, a list of dependencies whose
schemas must stay fixed. See PostgreSQL's [extension control parameters](https://www.postgresql.org/docs/18/extend-extensions.html#EXTEND-EXTENSIONS-FILES)
for the server's dependency and privilege semantics.

Control files use ASCII. Quotes, backslashes and comments follow PostgreSQL's
configuration syntax; repeated assignments use the last value. Use SQL
`COMMENT ON EXTENSION` for non-ASCII comments. Include directives are unsupported.

Ankus owns `default_version`, `module_pathname` and `encoding`; if present, those
values must match the generated publication. Set the version through
`AnkusExtensionVersion`, the library name through [`AnkusLibraryName`](#native-library-names)
or `AssemblyName`, and retain `UTF8` for generated SQL. A
[versioned library](#versioned-libraries) has no `module_pathname`, so an
authored value is rejected.

Use [`ankus get`](/reference/cli/#query-extension-properties) to inspect the
effective primary settings for a project or an existing publication.

For custom build integrations, the `AnkusGenerateControlFile` target writes the
same primary control after managed compilation. `AnkusControlOutput` selects
the output file; its default is
`$(IntermediateOutputPath)ankus-control/<extension>.control`.

### SQL directories

Set `directory` in the primary control file to install SQL scripts and
version-specific control files outside the usual `extension/` directory:

```ini
directory = 'hello/scripts'
```

Relative paths start at `pg_config --sharedir`. With PostgreSQL 18 or later,
a custom `extension_control_path` uses the selected search base instead. The
example puts scripts in `hello/scripts/` beneath that base; the primary control
file remains in `extension/`. An empty value, `directory = ''`, selects the
base itself. Omitting the setting uses `extension/`.

On Unix, `child/../scripts` also needs the intermediate `child` directory.
Ankus creates it, and parent traversal through a symbolic link follows the link's
target. Preserve empty directories when deploying a staged package.

An absolute value selects that exact directory on the server. On Windows, use
a fully qualified path such as `C:/PostgreSQL/hello/scripts`; drive-relative
paths and paths rooted without a drive are rejected.

Publishing keeps the control and SQL payload together under the output's
`extension/` directory. `ankus install` places installation SQL, upgrade scripts
and secondary controls in the declared destination. Change the author control
and republish to change the layout; editing only the published control is
rejected because it no longer matches the publication metadata.

`install --destdir` and `package` stage absolute destinations beneath their
output root, removing the filesystem root or Windows drive prefix. They do not
write to the authored absolute directory. Deploy those files to the original
absolute destination on the server.

Windows packages resolve relative values from their `share/` directory.
Parent-relative paths must stay inside the package root; use `install --destdir`
for a full filesystem layout when they do not.

The testing fixture remaps custom directories into its own temporary storage.
It leaves both the authored control and its destination untouched.

### Version-specific control files

Put overrides in `sql/<extension>--<version>.control`, for example
`sql/hello--0.2.0.control`:

```ini
requires = 'pg_trgm'
trusted = false
relocatable = false
```

PostgreSQL applies these assignments over the primary control file when
installing or updating to that version. Omitted assignments inherit the primary
settings. Use `requires = ''` to clear inherited dependencies. Each version
starts from the primary settings; it does not inherit the previous version's
overrides. The server updates dependency ownership transactionally with the SQL
upgrade. A `schema` override selects the schema for initial installation;
PostgreSQL does not move objects during an update.

Secondary files cannot set `default_version` or `directory`. The current
version's `module_pathname` must match its generated native library, and is
rejected for a versioned library. Other versions can name their own libraries,
which must be distributed separately.
All published scripts use UTF-8, so an explicit `encoding` must remain `UTF8`.
The current version's relocation flag also appears in its embedded native schema
metadata and cannot contradict generated SQL. Adding a fixed `schema` without
an explicit relocation flag writes `relocatable = false` into the secondary file.

Use ordinary MSBuild items to customize discovery:

```xml
<ItemGroup>
  <AnkusVersionControlFile Remove="sql/hello--0.2.0.control" />
  <AnkusVersionControlFile Include="controls/hello--0.2.0.control" />
</ItemGroup>
```

Set `EnableDefaultAnkusVersionControlFiles` to `false` for explicit items only.
Publishing validates and snapshots the selected files before native compilation.
Installing and packaging include exactly those snapshots. A successful republish
removes obsolete control files owned by the previous publication and preserves
unlisted files. Control assignments use literal values; SQL upgrade token
expansion does not apply to control files.

## PostgreSQL compilation symbols

The SDK also defines one C# compilation symbol for the selected major:
`ANKUS_PG13` through `ANKUS_PG19`. It uses the final `AnkusPostgresMajor`
setting, including project or command-line overrides, and preserves existing
framework and consumer symbols. Use these symbols when a native declaration
changes between server versions:

```csharp
#if ANKUS_PG13 || ANKUS_PG14 || ANKUS_PG15
// Use the declaration supplied by PostgreSQL 13–15 headers.
#else
// Use the declaration supplied by PostgreSQL 16 and later headers.
#endif
```

Build a separate native library against each target major's headers. A runtime
version check cannot make references to absent fields or different native
signatures compile; select that source at compile time.

Test publications also define `ANKUS_TESTS`, and benchmark publications define
`ANKUS_BENCHMARKS`. Code inside `#if ANKUS_TESTS`, such as a helper function that
only backend tests call, is compiled into test publications and left out of
ordinary ones, as pgrx's `pg_test` feature does.

Binding reuse checks file contents and current compiler inputs. Identical SDK
generator files can share generated sources across package directories. Native
layout and declaration checks still run against the selected installation.
Compiled companions also reuse identical runtime references at different
locations. Each project receives its own companion files, so cleaning one project
does not remove another project's outputs. The companion restore follows the
consuming project's NuGet feeds, configuration, source mappings and package directory.
On Windows, choose a short cache path so MSVC can open its native objects.

When a selected-header function is supplied by your own native code, include its
object file or library through the ordinary `NativeLibrary` item:

```xml
<ItemGroup>
  <NativeLibrary Include="native/libprovider.a" />
</ItemGroup>
```

Use an object or archive compiled for the extension's target, such as a `.lib`
archive on Windows. PostgreSQL and the native linker resolve the function's
actual definition; its generated `NativeMethods` entry retains the native error
guard.

`NativeMethods` includes only declarations present in the selected headers.
For a function owned by your native library whose declaration is absent from
those headers, use an explicit .NET `LibraryImport` with its matching
`DirectPInvoke` item and `NativeLibrary` input. A direct import does not add an
Ankus error guard: any PostgreSQL operation that can raise `ERROR` must be
guarded entirely in native code before control returns to managed code.

## Project SDK

An extension uses `Ankus.Sdk` as its project SDK. The SDK sets `PublishAot` and
`IsAotCompatible` to `true`, `NativeLib` to `Shared`, and enables unsafe code for
generated native entry points. The output type is `Library`.

When the project enables `ImplicitUsings`, the SDK adds `Ankus` to its implicit
usings, as the ASP.NET Core SDK adds its own namespaces, so source files need no
`using Ankus;`. Remove it with `<Using Remove="Ankus" />` in an item group.

Use `AnkusUpgradeScript` items to select SQL upgrades. See
[upgrading an extension](/getting-started/publishing/#upgrade-an-existing-extension)
for file naming, tokens, and native library versioning.

Pin the SDK version in the project (`Ankus.Sdk/0.1.1`) or centrally in `global.json`:

```json
{
  "msbuild-sdks": {
    "Ankus.Sdk": "0.1.1"
  }
}
```

With a central SDK version, use `<Project Sdk="Ankus.Sdk">`. Runtime and generator
package versions follow the SDK, including in projects using
`Directory.Packages.props`.

The .NET SDK, project target framework and embedded Native AOT runtime are
separate selections. See [.NET support](/reference/dotnet-support/) before
retargeting or updating a deployed extension's runtime.
Use `TargetFramework=net10.0` and let Ankus select the matching compiler, patched
runtime and framework libraries. A conflicting `RuntimeFrameworkVersion`
override is rejected.

## Embedded installation metadata

Published libraries also contain their original installation SQL and publication
identity. Use [`ankus schema`](/getting-started/publishing/#inspect-installation-sql)
to extract the complete script. With the matching `Ankus.PgConfig` package, read this metadata without
loading the library or needing its adjacent JSON, control, or SQL files:

```csharp
using Ankus.PgConfig;

ExtensionSchema schema = ExtensionSchema.Read("publish/MyExtension.so");
Console.WriteLine(schema.Name);
Console.WriteLine(schema.Sql);
```

Use `.dll` on Windows, or `.dylib` on macOS with PostgreSQL 16 and later.
macOS PostgreSQL 13–15 uses `.so`. The reader supports Linux x64,
Windows x64, and macOS x64/ARM64 libraries. For a universal macOS library,
pass `"osx-x64"` or `"osx-arm64"` as the second argument to select its slice.
For a thin library, that optional argument checks the native target instead.
`Artifacts` reports the original native filename, PostgreSQL major, runtime
identifier, and installation filenames even if the library was renamed.

`Sql` preserves the complete installation script, including PostgreSQL's
`MODULE_PATHNAME` substitution marker. It is intended for extension installation;
direct SQL replay must resolve that marker to the library's location first. A
[versioned library's](#versioned-libraries) script already names that library.
Missing, malformed, incompatible, or oversized metadata raises `FormatException`.
The native schema section is limited to 64 MiB. This metadata does not replace
the control and SQL files PostgreSQL needs for `CREATE EXTENSION`.

## Installation directories

| Artifact | Standard location |
| --- | --- |
| Native library | `pg_config --pkglibdir` |
| Primary control file | `extension/` under `pg_config --sharedir` |
| Installation SQL, upgrade SQL and secondary controls | `extension/` under `pg_config --sharedir`, or the declared [SQL directory](#sql-directories) |

The control file resolves the library through `dynamic_library_path`, normally
`$libdir`. PostgreSQL 18's `extension_control_path` also allows a separate
directory for extension files.
