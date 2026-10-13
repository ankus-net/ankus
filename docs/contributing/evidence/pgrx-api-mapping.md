# pgrx public API mapping

This maps the public API of the `pgrx` crate at 0.19.3 (commit
`fc91c63ebad11784647b50ee7e265c1fd9c9924f`) to Ankus. It is the API-by-API
inventory the [parity requirements](parity-requirements.md) call for; the
declaration options of `pgrx-macros` and the CLI have their own audits, and the
[unit-test](pgrx-test-mapping.md), [tooling](pgrx-tooling-test-mapping.md) and
[inline-test](pgrx-inline-test-mapping.md) mappings cover the test corpora.

The audit ran on 2026-10-11 against Ankus **0b0d82d**. Three independent passes
enumerated every callable item in the datum, SPI and call, and backend runtime
modules, grouped trivially related items, and searched the Ankus source before
reporting a gap. Each gap was then implemented or recorded below.

Statuses: **Mapped** (an equivalent existed), **Added** (implemented by this
audit), **Different** (a deliberate, documented difference) and **Rust-specific**
(no .NET meaning). No item is open. APIs with no pgrx counterpart, such as the
logical replication toolkit, are listed in the migration guide's
[Beyond pgrx](../../src/content/docs/getting-started/from-pgrx.md#beyond-pgrx) section.

## Datum conversion and values

| pgrx | Ankus | Status |
| --- | --- | --- |
| `FromDatum`, `from_polymorphic_datum`, `*_in_memory_context` | `PgDatum.Read<T>()`, `CopyTo(context).Read<T>()`, generated argument conversion | Mapped |
| `IntoDatum::into_datum`, `DatumWithOid::new`, `rust_str_to_text_p`, `rust_byte_slice_to_bytea` | `PgDatum.Create(value, context)`, `PgDatum.Create(SpiParameter, context)` | Added |
| Built-in Rust types | `bool`, `sbyte`, `short`, `int`, `long`, `uint`, `float`, `double`, `string`, `Rune`, `byte[]`, `PgCString`, `void`, `T?`, `PgTransactionId` | Mapped |
| `AnyNumeric`, `Numeric<P,S>` and their operators and methods | `PgNumeric`, `[PgNumericPrecision]`, `Rescale`; `Log(b)` takes the base as its argument, where pgrx's `log` reverses PostgreSQL's operands ([numeric](../../src/content/docs/numeric.md)) | Mapped; `Log` Different |
| `Date`, `Time`, `TimeWithTimeZone`, `Timestamp`, `TimestampWithTimeZone`, `Interval`, clocks and `DateTimeParts` | `PgDate`, `PgTime`, `PgTimeTz`, `PgTimestamp`, `PgTimestampTz`, `PgInterval`, `PgDateTimePart` and their members | Mapped |
| `From<Interval> for Time`, `From<Time> for Interval` | `PgInterval.ToTime()`, `PgTime.ToInterval()` | Added |
| `POSTGRES_EPOCH_JDATE`, `UNIX_EPOCH_JDATE`, `USECS_PER_SEC`, `USECS_PER_DAY` | `PgDate.PostgresEpochJulianDay`, `UnixEpochJulianDay`, `PgTime.MicrosecondsPerSecond`, `MicrosecondsPerDay` | Added |
| `DateTimeParts` as `&str` | `PgDateTimePart.ToPostgresName()` | Added |
| `Interval::abs`, `signum`, `is_negative` | `Abs`, `Sign` use PostgreSQL's 30-day-month comparison rather than per-component signs ([date and time](../../src/content/docs/date-and-time.md)) | Different |
| `TimeWithTimeZone::to_iso_string_with_timezone` | `PgTimeTz.ToIsoString(zone)` converts into the zone; pgrx keeps the stored offset | Different |
| `Json`, `JsonB`, `Uuid`, `Inet`, geometry, `Range<T>`, `Bytea` | `PgJson`, `PgJsonb`, `Guid`, `PgInet`/`PgCidr`, `PgPoint`…`PgPolygon`, `PgRange<T>`, `PgByteaView` | Mapped |
| `Inet` text | `PgInet.ToString()` uses .NET address formatting, which round-trips but can differ from `inet_out` for embedded IPv4; `PgDatum.Create(value, context).ToPostgresString()` gives the server's text | Different |
| `Array<T>`, `RawArray`, `FlatArray`, `Vec<T>` | `PgArrayView<T>`, `PgArrayView`, `PgFlatArray<T>`, `PgArray<T>`, `T[]` | Mapped |
| `RawArray::data`, `nulls` as mutable buffers | Views are read-only; build a new array with `CreateFlatArray` or `PgArray<T>` | Different |
| `VariadicArray<T>` | `params T[]` binds one-dimensional variadic arrays with a lower bound of 1; declare a non-variadic `PgArray<T>` parameter for other shapes | Different |
| `AnyArray`, `AnyElement`, `Internal` | `PgAnyArray`, `PgAnyElement`, `PgInternal` | Mapped |
| `PgVarlena<T>` | `PgVarlena<T>`; it is a mutable, disposable handle, so it keeps reference equality and callers compare `Value` | Mapped; equality Different |
| `varsize*`, `vardata_*`, `varatt_is_*`, `varlena_to_byte_slice` | Ankus bridges detoast before reading, and `PgTextView`, `PgByteaView` and `PgVarlena<T>` borrow payloads; arbitrary varlena types are read through a `PgDatumType` reader | Different |
| `lookup_enum_by_oid`, `lookup_enum_by_label` | `PgEnums.Lookup`, `PgEnums.GetValueOid(typeOid, label)` | Mapped; label lookup Added |
| `PostgresType`, `InOutFuncs`, `PgVarlenaInOutFuncs` | `[PgType]`, `PgTypeTextCodec<T>`, `NativeLayout` | Mapped |
| `UnboxDatum`, `BorrowDatum`, tuple datums, sealed traits | | Rust-specific |

## SPI, tuples, calls and triggers

| pgrx | Ankus | Status |
| --- | --- | --- |
| `Spi::run`, `get_one/two/three`, `connect`, `select`, `update`, `explain`, quoting | `Spi.Execute`, `ExecuteScalar(s)`, `Spi.Connect`, `SpiSession.Select`/`Query`, `Spi.Explain`, `Spi.Quote*` | Mapped |
| `get_one` row limit | Ankus scalar helpers run the whole command and copy the first row ([SPI](../../src/content/docs/spi.md)) | Different |
| Prepared statements, cursors, `find_cursor`, `detach_into_name` | `SpiPreparedStatement`, `SpiCursor`, `Spi.FindCursor`, `Detach()` | Mapped |
| `SpiTupleTable`, `SpiHeapTupleData::get`, `get_datum_by_*` | `SpiResult`, `SpiRow.Get<T>`, `SpiRawRow`; `SpiRow.Get<PgDatum>` and mapped `PgDatumType` reads | Mapped; raw and mapped reads Added |
| `SpiTupleTable::column_ordinal` | `SpiResult.GetOrdinal`, `SpiRawResult.GetOrdinal`, `SpiRawRow.GetOrdinal` | Added |
| `SpiOkCodes`, `SpiErrorCodes`, `check_status` | Ankus never exposes raw SPI return codes; a failed SPI call raises `PgException` | Different |
| Empty, utility and zero-column scalar results | Nullable and reference types receive null; non-nullable value types throw ([SPI](../../src/content/docs/spi.md)) | Different |
| `PgHeapTuple` construction, `get_by_*`, `set_by_*`, `from_composite_datum` | `PgTupleDescriptor.Load(...).CreateTuple()`, `Get<T>`, `Set<T>`, `PgDatum.Read<PgHeapTuple>()`; fields without a managed conversion are kept as exact copies | Mapped; exact copies Added |
| `from_heap_tuple`, `PgTupleDesc::from_pg_copy`, `into_pg`, `into_composite_datum` | `PgHeapTuple.DangerousCopy`, `PgTupleDescriptor.DangerousCopy`, `DangerousToNative`, `PgDatum.Create(tuple, context)` | Added |
| `PgTupleDesc::get`, attribute metadata | `PgTupleAttributeInfo` exposes name, type, base type, modifier, collation, dropped, not-null and generated availability; storage details come from the catalog | Different |
| `fcinfo` helpers, `pg_getarg*`, `pg_func_extra`, collation | Generated bindings, `PgFunctionContext` (`Arguments`, `CollationOid`, `GetOrCreateState<T>`) | Mapped |
| Raw `fcinfo`, `ReturnSetInfo`, `srf_*` | Generated set-returning protocol; raw call frames stay inside the native boundary ([native boundary](../native-boundary.md)) | Different |
| `direct_function_call`, `fn_call`, `Arg` | `PgFunctions.DangerousCall`, `Call`, `CallRaw`, `PgFunctionArgument`; `internal` arguments and results are rejected by name and by OID | Mapped; by-name check Added |
| `fn_call` OUT arguments, non-constant defaults, name resolution | Ankus accepts OUT arguments, evaluates defaults and resolves names with implicit coercion, as SQL calls do | Different |
| `SetOfIterator`, `TableIterator` | `IEnumerable<T>`, `IEnumerable<(...)>`, `PgSetMode` | Mapped |
| `PgTrigger` metadata, rows, `extra_args`, transition tables | `PgTriggerContext` | Mapped |
| `PgTrigger::trigger()` raw `UPDATE OF` columns, updated columns, constraint flags | `UpdateOfColumns`, `UpdatedColumns`, `ConstraintOid`, `IsDeferrable`, `IsInitiallyDeferred` | Added |
| `PgList`, `StringInfo` | `PgList<T>`, `PgStringInfoStream` | Mapped |
| `register_xact_callback`, `register_subxact_callback`, `xid_to_64bit` | `PgTransaction.RegisterCallback`, `RegisterSubtransactionCallback`, `PgTransactionId.ToFullTransactionId()` | Mapped |

## Backend runtime

| pgrx | Ankus | Status |
| --- | --- | --- |
| `PgMemoryContexts` variants, `new`, `switch_to`, `parent`, palloc helpers | `PgMemoryContext.Get`, `Current`, `Create`, `Run`, `Parent`, `Allocate*` | Mapped |
| `PgMemoryContexts::For`, `of`, `value` | `PgMemoryContext.DangerousBorrow`, `DangerousGetOwner`, `DangerousGetPointer` | Added |
| `set_as_current`, resetting the active context | Contexts switch only inside `Run`, which restores the caller's context; the active callback context cannot be reset or deleted | Different |
| `PgBox`, `PBox`, `MemCx` | `PgMemoryContext.CreateBox`, `CreateContextValue`, `PgContextValue<T>` | Mapped |
| `PgRelation`, `ItemPointer`, `PgProc`, `regtypein`, `PgQualifiedNameBuilder` | `PgRelation`, `PgItemPointer`, `PgFunctions.GetInfo`, `PgTypes.GetOid`, `PgQualifiedNameBuilder` | Mapped |
| `GucRegistry::define_*`, flags, units, hooks, `GucSetting::get` | `[PgGuc*]` properties, `PgGucRegistry.Define*`, `PgGucOptions`, `PgGucUnit`, `PgGucHooks<T>` | Mapped |
| `GucSetting::as_ptr` | Ankus owns the generated backing variable and exposes its value, not its address | Different |
| GUC enum ordinals, string encoding, preload metadata, hooked runtime definitions | C# enum values as ordinals; UTF-8 strings; ASCII preload metadata; up to 64 hooked run-time definitions ([configuration](../../src/content/docs/configuration.md)) | Different |
| `BackgroundWorkerBuilder`, `BackgroundWorker` methods, handles | `PgBackgroundWorkerOptions`, `PgBackgroundWorker`, `PgBackgroundWorkerHandle` | Mapped |
| `From<&BackgroundWorkerBuilder> for pg_sys::BackgroundWorker`, handle lifetime, SIGTERM during a transaction | Registration stays inside Ankus; handles belong to their callback; SIGTERM rolls back and exits ([background workers](../../src/content/docs/background-workers.md)) | Different |
| `pg_shmem_init!`, `PgLwLock`, `PgAtomic`, `PgSpinLock` | `PgSharedMemory.Initialize`, `PgLwLock<T>`, `PgAtomic<T>`, `PgSpinLock<T>`; `PgLwLock<THeader, TElement>` adds an array sized at startup | Mapped |
| `PgAtomic::get` ordering, `fetch_max`, `fetch_min` | Sequentially consistent operations; `Max`, `Min`; other updates use `CompareExchange` | Mapped; `Max`/`Min` Added |
| Custom `PgSharedMemoryInitialization` types | Raw shared-memory hooks through `[PgNativeCallback]` | Different |
| Logging macros, `ereport!`, `PgLogLevel`, `PgSqlErrorCode` | `PgLog.*`, `PgLog.Write(level, PgDiagnostic)`, `PgLogLevel`, `PgSqlStates` | Mapped |
| Lazy message formatting | `PgLog.Write(level, $"...")` formats only enabled levels; the level helpers take finished strings | Different |
| `#[track_caller]` file, line and function | Reports carry the native bridge location unless `PgDiagnostic.File`, `Line` and `Routine` are set | Different |
| `CaughtError` origins, `PgTryBuilder` | `PgException`, `PgTerminalException`, C# `try`/`catch`/`finally`; recovery needs a subtransaction for raw native calls | Different |
| `check_for_interrupts!`, `interrupt_pending` | `PgInterrupts.Check()`, `PgInterrupts.IsPending` | Mapped; `IsPending` Added |
| `pgrx_seahash` | `PgHash.Compute` returns the 32-bit value pgrx's derives use | Different |
| `Send`/`Sync`, lifetimes, typestate markers, panic plumbing | | Rust-specific |
