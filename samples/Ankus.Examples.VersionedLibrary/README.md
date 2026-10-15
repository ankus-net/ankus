# Versioned library example

This ports pgrx's `versioned_so` example. Its control file omits
`module_pathname`, which puts cargo-pgrx in versioned shared-object mode: the
library is installed as `<name>-<version>` and the installation SQL names that
file directly. Each version's SQL keeps naming its own library, so several
versions can be installed side by side.

Ankus generates the control file, so the mode is an MSBuild property:

```xml
<AnkusVersionedLibrary>true</AnkusVersionedLibrary>
```

Version 0.1.0 publishes `Ankus.Examples.VersionedLibrary-0.1.0.so` (`.dll` on
Windows, `.dylib` on macOS with PostgreSQL 16 and later). The control file has
no `module_pathname`, and the installation script refers to the library without
its suffix, which PostgreSQL appends while searching `dynamic_library_path`:

```sql
CREATE FUNCTION "hello_versioned_so"() RETURNS text
AS 'Ankus.Examples.VersionedLibrary-0.1.0', 'ankus_fn_..._hello_versioned_so' ...
```

```sql
CREATE EXTENSION ankus_versioned_so;

SELECT hello_versioned_so();                       -- Hello, versioned_so
SELECT probin FROM pg_proc
WHERE oid = 'hello_versioned_so()'::regprocedure;  -- Ankus.Examples.VersionedLibrary-0.1.0
```

## Upgrading

Publishing a new version produces a new library, such as
`Ankus.Examples.VersionedLibrary-0.2.0.so`. `ankus install` and `ankus package`
add it beside the libraries and scripts of earlier versions. Databases on 0.1.0
keep using the 0.1.0 library until they update.

An update script, such as `sql/ankus_versioned_so--0.1.0--0.2.0.sql`, replaces
the functions whose native code changed. Copy their declarations from the new
version's generated installation SQL:

```sql
CREATE OR REPLACE FUNCTION "hello_versioned_so"() RETURNS text
AS 'MODULE_PATHNAME', 'ankus_fn_..._hello_versioned_so' LANGUAGE c ...;
```

Without a primary `module_pathname`, PostgreSQL would leave `MODULE_PATHNAME`
unresolved. Ankus resolves it when publishing, as PostgreSQL would for the
version a script updates to: this script references
`Ankus.Examples.VersionedLibrary-0.2.0`, and a `0.2.0--0.1.0` script references
the 0.1.0 library. Explicit library names also work. Then:

```sql
ALTER EXTENSION ankus_versioned_so UPDATE TO '0.2.0';
```

Export names include a hash of the assembly identity, which changes with the
project `Version`. Use the generated SQL of the version a script updates to.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `versioned_so.control` without `module_pathname` | `<AnkusVersionedLibrary>true</AnkusVersionedLibrary>` |
| Installed library `versioned_so-0.0.0.so` | Published library `Ankus.Examples.VersionedLibrary-0.1.0.so` |
| SQL `AS 'versioned_so-0.0.0', 'hello_versioned_so_wrapper'` | SQL `AS 'Ankus.Examples.VersionedLibrary-0.1.0', 'ankus_fn_..._hello_versioned_so'` |
| `default_version = '@CARGO_VERSION@'` | `AnkusExtensionVersion`, which defaults to the project `Version` |
| `#[pg_extern] fn hello_versioned_so()` | `[PgFunction] HelloVersionedSo()` |

## Deliberate differences

- cargo-pgrx selects versioned mode when the control file has no
  `module_pathname`. Ankus owns that setting, so `AnkusVersionedLibrary` selects
  the mode, and an authored `module_pathname` for the current version is rejected.
- cargo-pgrx installs the library as `<extension>-<version>`, while its SQL uses
  the crate's library name; they match in pgrx's examples. Ankus uses one base
  name for both, `AnkusLibraryName` or else the target name, so the published
  file, its manifest and its SQL always agree.
- cargo-pgrx copies update scripts without resolving `MODULE_PATHNAME`, so pgrx
  scripts name the versioned library explicitly. Ankus resolves it to the target
  version's library unless that version's control file sets its own
  `module_pathname`.
- Ankus replaces every `MODULE_PATHNAME` in the installation script, as
  PostgreSQL does, including in custom SQL. pgrx substitutes only the paths it
  generates.
- pgrx's `#[pg_test]` checks the greeting. The Ankus integration tests also check
  the published filenames, the control file, the installation SQL, the embedded
  schema and its item selection, `probin` and the loaded file. A tool test
  publishes two versions, installs and packages them side by side, and moves a
  database between them in both directions.

See [build settings](https://ankus.net/reference/build-settings/#versioned-libraries),
[upgrading an extension](https://ankus.net/getting-started/publishing/#upgrade-an-existing-extension)
and the [versioned custom library name sample](../Ankus.Examples.VersionedCustomLibraryName/).
