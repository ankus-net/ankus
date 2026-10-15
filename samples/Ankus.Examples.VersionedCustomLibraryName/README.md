# Versioned custom library name example

This ports pgrx's `versioned_custom_libname_so` example, which combines a custom
`[lib] name` with versioned shared-object mode. Its library is
`versioned_othername-<version>`, and the installation SQL names it directly.

Ankus combines the two properties:

```xml
<AnkusLibraryName>versioned_othername</AnkusLibraryName>
<AnkusVersionedLibrary>true</AnkusVersionedLibrary>
```

Version 0.1.0 publishes `versioned_othername-0.1.0.so` (`.dll` on Windows,
`.dylib` on macOS with PostgreSQL 16 and later), a control file without
`module_pathname`, and SQL that references `versioned_othername-0.1.0`:

```sql
CREATE EXTENSION ankus_versioned_custom_libname_so;

SELECT hello_versioned_custom_libname_so();  -- Hello, versioned_custom_libname_so
SELECT probin FROM pg_proc
WHERE oid = 'hello_versioned_custom_libname_so()'::regprocedure;  -- versioned_othername-0.1.0
```

Later versions install beside it and update as described in the
[versioned library sample](../Ankus.Examples.VersionedLibrary/#upgrading).

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| Cargo `[lib] name = "versioned_othername"` | `<AnkusLibraryName>versioned_othername</AnkusLibraryName>` |
| `versioned_othername.control` without `module_pathname` | `<AnkusVersionedLibrary>true</AnkusVersionedLibrary>` |
| Installed library `versioned_othername-0.0.0.so` | Published library `versioned_othername-0.1.0.so` |
| `#[pg_extern] fn hello_versioned_custom_libname_so()` | `[PgFunction] HelloVersionedCustomLibnameSo()` |

## Deliberate differences

- pgrx's extension is named after its control file, `versioned_othername`. The
  Ankus extension is `ankus_versioned_custom_libname_so`; the library name is
  set separately.
- The other differences are those of the
  [versioned library sample](../Ankus.Examples.VersionedLibrary/#deliberate-differences).

See [build settings](https://ankus.net/reference/build-settings/#native-library-names).
