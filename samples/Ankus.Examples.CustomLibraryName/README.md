# Custom library name example

This ports pgrx's `custom_libname` example. Its Cargo package is `custom_libname`,
but its `[lib] name` is `other_name`, so the shared library is `other_name` and the
control file sets `module_pathname = 'other_name'`.

In Ankus, `AnkusLibraryName` names the native library without changing the
assembly or the SQL extension:

```xml
<AnkusLibraryName>other_name</AnkusLibraryName>
<AnkusExtensionName>ankus_custom_libname</AnkusExtensionName>
```

Publishing produces `other_name.so` (`other_name.dll` on Windows,
`other_name.dylib` on macOS with PostgreSQL 16 and later) and a control file
whose `module_pathname` is that filename:

```sql
CREATE EXTENSION ankus_custom_libname;

SELECT hello_custom_libname();                       -- Hello, custom_libname
SELECT probin FROM pg_proc
WHERE oid = 'hello_custom_libname()'::regprocedure;  -- other_name.so

-- PostgreSQL 18 and later
SELECT module_name, file_name FROM pg_get_loaded_modules();
-- Ankus.Examples.CustomLibraryName | other_name.so
```

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| Cargo `[package] name = "custom_libname"` | The project, `Ankus.Examples.CustomLibraryName.csproj`, and its assembly name |
| Cargo `[lib] name = "other_name"` | `<AnkusLibraryName>other_name</AnkusLibraryName>` |
| `other_name.control` with `module_pathname = 'other_name'` | Generated `ankus_custom_libname.control` with `module_pathname = 'other_name.so'` |
| `pg_module_magic!(name, version)`, which reports the package name | The generated module identity, which reports the assembly name and version |
| `#[pg_extern] fn hello_custom_libname()` | `[PgFunction] HelloCustomLibname()` |

Without `AnkusLibraryName`, the library is named after `TargetName`, which
defaults to `AssemblyName`. Changing `AssemblyName` also renames the library, but
it changes the assembly's identity too: its module name, default extension name
and generated export names. `AnkusLibraryName` changes only the file, and names
such as `other_name` do not conflict with .NET assembly naming analyzers.

## Deliberate differences

- cargo-pgrx names the installed library after the control file (`other_name`),
  so pgrx's library and extension names are the same. Ankus names the extension
  with `AnkusExtensionName` and the library separately; this sample uses
  `ankus_custom_libname` to show that they are independent.
- pgrx writes `module_pathname` without a suffix. Ankus generates the exact
  filename, including the suffix PostgreSQL's loader expects on that platform.
- pgrx's `#[pg_test]` checks the greeting. The Ankus integration test also checks
  the published filenames, the control file, the embedded schema, the function's
  `probin` and, on PostgreSQL 18 and later, the loaded module.

See [build settings](https://ankus.net/reference/build-settings/#native-library-names)
and the [versioned custom library name sample](../Ankus.Examples.VersionedCustomLibraryName/).
