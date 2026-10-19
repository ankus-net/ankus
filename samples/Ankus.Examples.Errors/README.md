# Errors and PostgreSQL reports

This sample ports pgrx's `errors` example to idiomatic C#. It demonstrates
ordinary managed exceptions, errors from guarded PostgreSQL calls, and every
reporting level used by the original example.

Publish and install the extension, then load it:

```sql
CREATE EXTENSION ankus_errors;

SELECT errors.array_with_null_and_panic(ARRAY[1, 2, 3]);
SELECT errors.raise_pg_info('finished');
SELECT errors.raise_pg_warning('check this value');
SELECT errors.throw_managed_exception('managed failure');
SELECT errors.throw_pg_error('PostgreSQL error');
```

Managed exceptions become PostgreSQL `ERROR` only after managed cleanup has
finished and use SQLSTATE `38000`. The equivalent pgrx Rust panics use `XX000`.
`PgLog.Error` follows the same boundary and preserves its diagnostic.
`PgLog.Fatal` ends the current backend. `PgLog.Panic` terminates peer backends
and starts PostgreSQL crash recovery, so use those examples only on a disposable
development cluster.

The nullable array example preserves SQL NULL cells as `int?` values and rejects
them deliberately. The native relation example shows a PostgreSQL lookup error
crossing the guarded boundary as an owned diagnostic, so no native `longjmp`
crosses managed frames.
