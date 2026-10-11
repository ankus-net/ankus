# pgrx unit-test source mapping

This maps every test case in `pgrx-unit-tests/src/tests/` at pgrx 0.19.3 to the
Ankus test that verifies the same behavior, or records why it does not apply.
It satisfies the inventory's source-case mapping requirement for that corpus;
the [parity requirements](parity-requirements.md) list the remaining corpora.

The audit covers 539 test cases in 63 files, read against Ankus at revision
**a3e39c8** and updated with the cases added on 2026-10-09. Only the same values
and edge cases count as covered; a test of the same type with different
boundaries is partial.

Paths are relative to the repository: **IT** is `tests/Ankus.IntegrationTests/`,
**RT** is `tests/Ankus.Runtime.Tests/`, **GT** is `tests/Ankus.Generators.Tests/`,
**BT** is `tests/Ankus.Build.Tests/` and **TE** is `tests/Ankus.TestExtension/`.
Integration tests execute their TE functions inside PostgreSQL.

Statuses: **Covered**, **Added** (covered by a test added in this audit),
**Partial** (what is missing is named), **Gap** (no equivalent), **Different**
(a deliberate, documented behavior difference) and **Rust-specific** (no .NET
analogue; the .NET guarantee is named).

## Summary

Most cases are covered, often with more boundary values than pgrx uses. Cases
added on 2026-10-09 close every gap and partial: SPI errors inside set-returning
functions, exceptions from callbacks PostgreSQL invokes, configuration checks,
lock release during unwinding, range reconstruction, `Rune` as `varchar`, message
domains, lazy logging, composite defaults, variadics and casts, TOAST input to
set functions, worker SPI after termination, seeded temporal and serialized
round trips, the installed signature matrix and flat arrays built in place.
Deliberate differences are documented where users meet them.

## Datum types

### datetime_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| test_to_pg_epoch_days, test_to_posix_time, test_to_julian_days | Covered | RT PgTemporalFieldsTests DateFieldsAndEpochsPreserveFullRange; IT TemporalParityTests DateFieldsMatchPostgresAcrossFullRange |
| test_time_with_timezone_serialization, test_timetz_from_time_and_zone | Covered | IT TemporalParityTests NamedZoneTimeConstructionPreservesWallClock, RawTemporalFactoriesMatchNativeBinaryValues; IT ScalarJsonTests ScalarJsonPreservesFullRangeAndScale |
| test_date_serialization, test_time_serialization, test_timestamp_serialization, test_timestamp_with_timezone_serialization | Covered | IT ScalarJsonTests ScalarJsonPreservesFullRangeAndScale |
| test_accept_date_* (now, yesterday, tomorrow, random, round trips, ±infinity, large dates) | Covered | IT TemporalDatumTests TemporalStorageSurvivesEveryPath (eight ownership paths) |
| test_accept_time_* | Covered | Same |
| test_convert_time_with_time_zone_now, test_is_timestamp_with_time_zone_utc | Covered | IT TemporalParityTests UtcConveniencesMatchNativeValues |
| test_accept_timestamp, test_accept_timestamp_with_time_zone*, round trips | Covered | IT TemporalDatumTests TemporalStorageSurvivesEveryPath, DotnetTemporalTypesWorkInsideNativeAot |
| test_return_3pm_mountain_time | Covered | IT TemporalConvenienceTests TemporalFactoriesMatchSql |
| test_is_timestamp_utc, test_timestamptz | Covered | RT PgTemporalFieldsTests TimestampFieldsUseFloorDivision; IT TemporalDatumTests NativeInputUsesPostgresEpochAndIndependentFields |
| test_timestamp_with_timezone_infinity, test_timestamp_infinity | Covered | IT TemporalDatumTests TemporalStorageSurvivesEveryPath; RT PgTemporalFieldsTests RawTimestampSaturationPreservesBoundaries |
| test_from_str | Covered | IT TemporalOperationTests ParsingAndFormattingHonorServerSettings; IT TemporalConvenienceTests TemporalFactoriesMatchSql |
| test_accept_interval_* | Covered | IT TemporalDatumTests TemporalStorageSurvivesEveryPath |
| test_interval_serialization | Different | Interval JSON is ISO 8601 in every IntervalStyle (IT ScalarJsonTests IntervalJsonIgnoresStyleAndRetainsComponents); pgrx writes `interval_out` text. Documented in the date and time guide. |
| test_duration_to_interval_err, test_negative_interval_to_duration_conversion | Rust-specific | `TimeSpan` is signed and `PgInterval.FromTimeSpan` stores microseconds only (RT PgTemporalTests TimeSpanConversionIsExactAndNeverNormalizesDays) |
| test_timezone_offset_cest, test_timezone_offset_us_eastern, test_timezone_offset_unknown | Covered | IT TemporalParityTests NamedZoneOffsetsUseTransactionStart, NamedZoneOffsetsUseSpecifiedInstant, TemporalParityErrorsPreserveState |
| test_interval_to_duration_conversion, test_duration_to_interval_conversion | Different | `ToTimeSpan` rejects month and day components and `FromTimeSpan` never invents them (RT PgTemporalTests IntervalKeepsCalendarComponentsAndExplicitInfinity); the SPI read is covered by IT TemporalDatumTests TemporalDomainsRemainOwnedAfterSpiCleanup |
| test_interval_from_seconds | Covered | IT TemporalConvenienceTests TemporalFactoriesMatchSql |
| test_interval_from_mismatched_signs | Different | `PgInterval.Create` accepts mixed signs as `make_interval` does (IT IntervalFactoryTests IntervalFactoryPreservesBoundaryComponents) |
| test_add_date_time, test_add_time_interval, test_add_intervals | Covered | IT TemporalConvenienceTests TemporalOperatorsMatchSql |
| test_old_date | Covered | IT ArrayJsonTests ArrayJsonPreservesExactUpstreamValues; IT ArrayDatumTests ArraysPreserveBinaryValuesAcrossOwners |

### numeric_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| select_a_numeric | Covered | IT NumericTests NumericStorageSurvivesEveryPath |
| test_return_an_i32_numeric, test_return_a_u64_numeric | Covered | IT NumericContractTests GenericIntegersExecuteInNativeAot |
| test_return_a_f64_numeric | Covered | IT NumericTests NumericDomainsConversionsAndCleanupRemainOwned |
| test_deserialize_numeric | Covered | IT ScalarJsonTests ScalarJsonNumbersAndNullsUseExactContracts, ScalarJsonFailuresPreservePathsAndBackend |
| test_limits | Added | Integer and float4 limits: RT PgNumericTests, IT NumericContractTests; double ±Infinity and NaN: IT NumericContractTests SpecialAndOverflowingConversionsMatchSql (identical to a SQL cast, including PostgreSQL 13's rejection of infinity) |
| test_bad_conversions | Added | Narrowing overflow: RT PgNumericTests GenericIntegerConversionsAreExactAndChecked; numeric beyond single precision: IT NumericContractTests SpecialAndOverflowingConversionsMatchSql |
| test_nan_ordering, test_ordering | Covered | RT PgNumericTests EqualityAndHashesIgnoreDisplayScale, OrderingMatchesNumericMagnitudeAndSpecialValues |
| test_anynumeric_sum, test_option_anynumeric_sum | Covered | IT NumericContractTests GenericArithmeticPreservesScale; IT ArrayDatumTests ArraysPreserveBinaryValuesAcrossOwners |

### uuid, bytea, inet and json tests

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| uuid: test_uuid_scalar_and_element_impls | Rust-specific | Element and array OIDs: IT BorrowedArraySliceTests BorrowedArraySlicesPreserveNativeValues |
| uuid: array slice, display, accept, return, parse | Covered | IT BorrowedArraySliceTests; IT ExtendedDatumTests UuidUsesNetworkByteOrder, ExtendedTypesSurviveEverySpiPath |
| bytea: test_return_bytes, test_return_vec_bytes, test_return_vec_subvec, test_bytea_arg_length, test_bytea_roundtrip | Covered | IT DatumConversionTests ByteaPreservesAllBytes; IT BorrowedBufferStorageTests BorrowedBuffersMatchNativeStorageAndCleanup; IT BorrowedBufferTests BorrowedBufferScalarReturnsPreserveExactValues |
| bytea: test_return_bytes_slice | Rust-specific | A borrowed sub-slice return depends on Rust lifetimes; .NET copies |
| bytea: test_bytea_is_empty | Added | IT BorrowedBufferTests BorrowedByteaLengthDistinguishesEmptyValues |
| inet: all three | Covered | IT NetworkDatumTests NetworkJsonUsesAotMetadata, NetworkOwnershipPathsPreserveValues |
| json: test_json, test_jsonb, test_json_arg, test_jsonb_arg | Covered | IT ExtendedDatumTests SourceGeneratedJsonContractsRunInsideNativeAot, ExtendedTypesSurviveEverySpiPath |

### geo_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| point, box, circle, line, lseg, path and polygon datums | Covered | IT GeometryDatumTests GeometryOwnershipPathsPreserveBinaryValues, GeometryParsingMatchesPostgres, GeometryBoundsMatchPostgres; RT PgGeometryTests |
| test_fn_call_path_datum, test_fn_call_polygon_datum | Added | IT FunctionCallTests GeometryArgumentsAndResultsCrossDirectCalls (`popen` and `polygon` with path arguments and results) |

## Ranges and function calls

### range_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| int4, int8, numeric, date, timestamp and timestamptz pass-through | Covered | IT RangeDatumTests RangeOwnershipPathsPreserveBinaryValues |
| test_accept_range_date_array | Covered | Same, `dates` rows; RT PgRangeTests RangeArrayTransportPreservesStates |
| test_empty_anynumeric_range | Added | Same test, `[10.5,10.5)` numrange row |
| test_range_date_rt_values_empty, _neg_inf, _inf, _neg_inf_inf, _neg_inf_val, _val_inf, _full | Added | Same test, single daterange rows for empty, open-bracket infinity, missing bounds and `(,)` |
| test_range_*_rt_bounds and test_range_date_rt_bounds_* (rebuild from bounds) | Added | IT RangeDatumTests RangesRebuiltFromBoundsAreIdentical (int4, int8, numeric, timestamp and every daterange edge) |

### fn_call_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| test_int4eq_eq, test_int4eq_ne, test_sql_int4eq, test_null_arg_*, test_strict | Covered | IT FunctionCallTests FunctionLanguagesAndNullInputsMatchSql |
| test_my_int4eq | Covered | IT FunctionCallTests CollationAndTextOwnershipSurviveNativeReturn |
| test_incompatible_return_type, fn_raises_error | Covered | IT FunctionCallTests ErrorsPreserveDiagnosticsRollbackAndSameBackendRecovery |
| test_too_many_args, unknown_function | Covered | IT FunctionCallTests InvalidCallsFailBeforeExecutionAndRecover |
| blank_function | Added | Same test, empty name; a blank name now reaches PostgreSQL's name parser and fails with 42602, as a malformed name does |
| invalid_identifier | Covered | IT FunctionCallTests IdentifierAndOverloadResolutionUsePostgresRules |
| test_with_only_default_* | Covered | IT FunctionCallTests TypedDefaultsResolveOverloadsWithoutGuessing |
| test_with_default, test_with_functional_default | Covered | IT FunctionCallTests DefaultExpressionsRemainTypedAndExecuteOnce |
| test_with_two_defaults, test_with_arg_and_two_defaults_*, test_with_unspecified_default, test_with_null_default | Added | IT FunctionCallTests OmittedTrailingDefaultsAreFilledAndRequiredArgumentsAreNot (by name and by OID; STRICT NULL default; missing required argument) |
| test_func_with_collation | Added | IT FunctionCallTests CollationAndTextOwnershipSurviveNativeReturn, default collation 100 |

### fcinfo, default-argument, variadic and pg_extern tests

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| fcinfo: test_takes_i16/i32/i64/bool/f32/f64/i8, strings, options, void, tuple | Covered | IT DatumConversionTests ScalarValuesPreservePostgresRepresentation, RealPreservesBitPattern, DoublePreservesBitPattern, NullableContractsPreserveNullAndEmpty, GreetingUsesGeneratedTextConversion; IT SetReturningTests TableColumnsPreserveNamesTypesAndValues |
| fcinfo: test_takes_char | Added | IT RuneTests RunesRoundTripAsVarchar (`🚨` row) |
| fcinfo: test_panics | Covered | IT ErrorsExampleTests ErrorsSamplePreservesDiagnosticsAndRecovery |
| fcinfo: test_same_name | Added | IT FunctionDeclarationTests SqlDispatchHonorsDeclarations: `same_name(same_name text)` called by position and by name |
| fcinfo: test_null_strict_type, test_null_error_type | Covered | IT CustomTextTypeTests CustomTextNullInputOptionsPreserveSqlNull |
| fcinfo: make_idea_happy, test_add_two_numbers | Rust-specific | A `[PgFunction]` method remains an ordinary static method |
| default_arg_value: negative, positive and specified defaults | Covered | IT FunctionDeclarationTests SqlDispatchHonorsDeclarations |
| default_arg_value: test_option_default_argument | Added | IT FunctionDeclarationTests SqlDispatchHonorsDeclarations: `int? = null` and `string? = null`, omitted, supplied and NULL |
| variadic: test_func_with_variadic_array_args | Covered | IT ArrayDatumTests ParamsArraysDeclareSqlVariadicFunctions |
| pg_extern: immutable, security invoker/definer, overridden SQL, anyelement, name | Covered | IT FunctionDeclarationTests CatalogRetainsPlannerAndArgumentContracts, SecurityModeControlsPrivilegesAndRestoresContext; IT SqlGenerationTests; IT PolymorphicTests; IT PlannerSupportTests |
| pg_extern: test_create_or_replace | Added | IT FunctionDeclarationTests SqlDispatchHonorsDeclarations: a `CreateOrReplace` function replaces a SQL function that installation SQL created earlier, and another creates a new function; re-applying a definition: GeneratedReplacementPreservesDependentObjects |

## SPI, sets, tuples, relations and lists

### spi_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| test_spi_failure, test_panic_via_spi | Covered | IT SpiCommandBoundaryTests InvalidBindingContextsRecoverTheSameCallback; IT SpiSessionTests FailedCallbackClosesSessionAndPlans; IT SpiTests RecursiveManagedErrorCanBeCaughtByOuterFunction |
| test_spi_can_nest | Covered | IT SpiSessionTests SessionOperationsPreserveScopeAndResults |
| test_spi_returns_primitive, _str, _string, test_option | Covered | IT SpiQueryTests ScalarAndRowLimitSemanticsArePreserved, ParametersPreserveValuesAndDeclaredTypes |
| test_spi_get_one | Different | pgrx's `get_one` runs its query with a row limit of 1; Ankus scalar helpers run the whole command and copy the first row, so `INSERT ... RETURNING` inserts every row ([SPI guide](../../src/content/docs/spi.md)). IT SpiQueryTests ExecuteScalarCompletesAllWriteEffects |
| test_spi_get_two, _three and their failures | Covered | IT SpiScalarTests FirstRowValuesRemainExactAndOwned, ResultErrorsPreserveBackendRecovery. Ankus reads int4 as `int` and rejects widening by design. |
| test_spi_select_zero_rows | Added | IT SpiEdgeTests SpiBoundaryCasesReportExactOutcomes: a non-nullable read of an empty result reports that the query returned no rows; nullable reads return null |
| test_spi_run, test_spi_run_with_args | Covered | IT SpiTests ExecuteMutatesCallingTransaction; IT SpiQueryTests BoundParametersRemainData |
| test_spi_explain, test_spi_explain_with_args | Covered | IT SpiHelperTests ExplainUsesTypedParametersAndOwnedJson |
| test_inserting_null | Covered | IT ExtendedDatumTests ExtendedTypesSurviveEverySpiPath |
| test_cursor, test_cursor_prepared_statement, test_cursor_by_name, test_cursor_not_found | Covered | IT SpiCursorTests CursorBatchesPreserveValuesAndOwnership, DetachedCursorCanBeFoundAndContinuedByName, CursorErrorsPreserveBackend |
| test_cursor_prepared_statement_panics_less_args, _more_args, test_cursor_failure | Added | IT SpiEdgeTests SpiBoundaryCasesReportExactOutcomes (argument counts at cursor open; parse error 42601 at open, then SPI continues) |
| test_columns | Added | Same test: a utility statement reports zero columns; names and OIDs are covered by IT SpiQueryTests EmptyResultsRetainMetadata |
| test_open_multiple_tuptables, _rev | Added | IT SpiQueryTests MaterializedRowsSurviveSubsequentSpiCalls; IT SpiEdgeTests (indexing an empty result throws, as arrays do) |
| test_prepared_statement, test_prepared_statement_argument_mismatch, test_owned_prepared_statement | Covered | IT SpiPreparedTests InvalidParametersLeavePlanUsable; IT SpiSessionTests KeptSessionPlanSurvivesTransactionEnd |
| read-only and read-write selection, prepared statements in each mode, visibility of prior writes | Covered | IT SpiSelectionTests SelectionPreservesImmutableTransaction, WritableIntentSelectsFreshSnapshotsAndResetsAfterRollback; IT SpiPreparedTests |
| spi_can_read_domain_types | Covered | IT SpiQueryTests DomainResultsPreserveDeclaredType; IT ExtendedDatumTests DomainResultsResolveBaseTypesAndOwnTheirValues |
| spi_can_read_domain_types_based_on_domain_types | Added | IT SpiEdgeTests (a domain over a text domain reads as text and keeps the outer domain's OID) |
| spi_can_read_binary_coercible_types | Different | `cidr` reads as `PgCidr`, not `PgInet` (RT PgNetworkTests BinaryTransportRetainsNetworkBits) |
| test_quote_identifier, test_quote_literal | Covered | IT SpiHelperTests IdentifierQuotingUsesServerRules, LiteralQuotingPreservesNativeEscapeSettings |
| test_quote_qualified_identifier | Added | IT SpiEdgeTests (mixed quoted and unquoted components); IT SpiHelperTests QualifiedIdentifiersPreserveComponentBoundaries |
| can_return_borrowed_str | Covered | IT SpiExampleTests SpiSampleDetachesCatalogAndCursorResults |
| test_connect_return_anything, test_spi_non_mut | Rust-specific | `Spi.Connect<TResult>` is an ordinary generic; borrow mutability has no .NET counterpart |

### srf_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| test_generate_series, test_composite_set, test_return_table_iterator, test_return_setof_iterator, empty iterators, test_one_col_table | Covered | IT SetReturningTests ScalarSetsPreserveEmptyAndNullSemantics, TableColumnsPreserveNamesTypesAndValues; IT SpiExampleTests SpiSampleMapsAndFiltersRows |
| spi_in_iterator, spi_in_setof | Added | IT SetSpiErrorTests SpiErrorsInsideSetsReachTheClient (before the first row, while streaming and while materializing; cleanup runs once; the backend reruns the function) and CaughtSpiErrorsContinueTheSet |
| test_srf_setof_datum_detoasting_with_borrow, test_srf_table_datum_detoasting_with_borrow | Added | IT BorrowedBufferTests ToastedTextFeedsBorrowedSetFunctions: borrowed-text SETOF and TABLE functions reread table columns stored compressed inline, out of line and compressed out of line, with every token checked in order |
| test_result_table_1 to _3 | Rust-specific | Result wrappers; values are covered by TableColumnsPreserveNamesTypesAndValues |
| test_result_table_4_err, test_result_table_5_none | Covered | IT SpiExampleTests SpiSampleRejectsInvalidAgesAndRecovers; IT SetReturningTests IteratorFailuresPreserveOwnershipAndRecover |

### heap_tuple.rs, rel_tests.rs and list_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| test_gets_name_field, _strict, test_create_dog, test_scritch, _strict, test_new_composite_type | Covered | IT CompositeDatumTests TupleCloneReplacesOnlyItsOwnSlots, DescriptorConstructionPreservesAllNullRowsAndArrayIdentity, NamedAndNestedTuplesPreserveExactValuesAcrossOwners; IT CompositesExampleTests |
| test_gets_name_field_default, test_gets_name_field_variadic | Added | IT CompositeDatumTests CompositeDefaultsVariadicsAndCastsExecute: a row default, variadic rows including NULL rows, and a defaulted variadic, called with and without arguments; GT PgCompositeGenerationTests CompositeVariadicsAndDefaultsKeepBindings |
| test_missing_type | Added | IT CompositeDatumTests MissingTypeDescriptorFailsAndRecovers (42704, then the same backend loads an existing type) |
| test_missing_field, test_missing_number | Added | IT CompositeDatumTests InvalidTupleOperationsLeaveExistingCellsIntact (get and set by unknown name or ordinal) |
| test_wrong_type_assumed | Added | Same test. As in pgrx, a typed read of a NULL cell returns null without checking the requested type; non-NULL cells and every write are checked. |
| test_compatibility | Added | Same test: reading `SELECT 1` as a tuple throws InvalidCastException |
| test_tuple_desc_clone | Added | IT CompositeDatumTests LargeCompositeSetsShareOneDescriptor: 10,000 rows from one descriptor, read by the client and through SPI |
| test_accept_relation | Covered | IT RelationTests MetadataMatchesCatalog, PhysicalDescriptorsMatchCatalogAfterClose |
| list_length_10, list_length_1000, list_length_drained | Covered | IT ListTests GrowthUsesOriginalOwner, RemovalAndDrainPreserveRemainingCells |

## Arrays, polymorphic values and datum edges

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| array_tests: enum, int, bigint, bool, NULL counting, optional and defaulted arrays, deny-null iteration | Covered | IT EnumDatumTests EnumOwnershipPathsPreserveIdentity; IT ArrayExampleTests; IT ArrayDatumTests ArraysPreserveBinaryValuesAcrossOwners, LossyArrayConversionsRaiseManagedErrors |
| array_tests and array_borrowed: *_overflow (one million elements) | Added | IT ArrayDatumTests MillionElementOverflowFailsAndRecovers: a checked sum over one million copied or borrowed elements fails with the managed overflow and the same backend then sums 65,535 elements; mid-iteration failure: IT ArrayJsonTests AnyArrayJsonIterationRejectsNullAndRecovers |
| array JSON serialization (empty, all NULL, Unicode, large, i32, date, timestamp, json, deny-null) | Covered | IT JsonExampleTests JsonTextSamplePreservesCells, JsonTextSamplePreservesEveryLargeCaseValue; IT ArrayJsonTests ArrayJsonPreservesExactUpstreamValues, RequiredArrayJsonRejectsNullCellsAndRecovers |
| text array returns, zero-length results, slices passed to SPI, data pointers, ranks, copies | Covered | IT ArrayExampleTests; IT ArrayDatumTests VectorsAndDotnetElementsUseExactConversions, ShapeAndIndexingMatchPostgresSubscripts; IT BorrowedArrayTests; IT BorrowedArraySliceTests |
| test_display_get_arr_nullbitmap (owned and borrowed) | Added | `PgArrayView.DangerousGetNullBitmap` and the typed view borrow the bitmap (IT BorrowedArrayCallbackTests BorrowedArraysExposeTheirNullBitmap, including two bitmap bytes and two dimensions); `HasNulls` and per-cell NULLs are covered elsewhere |
| cstring arrays, numeric slices, slices with NULL, point arrays, text arrays and iterators | Covered | IT CStringTests CStringArraysPreserveShapeAndCellBytes; IT BorrowedArraySliceTests BorrowedArraySlicesRejectAndRecover; IT GeometryDatumTests; IT BorrowedArrayTests BorrowedArrayIteratorsAdvanceIndependently |
| array_borrowed: read_array_back, error_cases | Added | `PgMemoryContext.CreateFlatArray<T>` is the counterpart of `FlatArray::new_zeroed_in`: IT TypedArrayCallbackTests FlatArraysBuildInPlaceAndReturn fills, reads back and returns flat arrays of every fixed-size element type, two dimensions and lower bounds; RT PgFlatArrayTests LimitsAreCheckedBeforeAllocation rejects too many elements, a zero-length dimension, too many bytes and too many dimensions before allocating |
| array_borrowed: borrow_test_count_true | Added | IT TypedArrayCallbackTests GeneratedTypedArraysEnforceNullCellContracts counts true cells through a generated `PgArrayView<bool>` argument |
| anyarray, anyelement and anynumeric arguments | Covered | IT PolymorphicTests ArrayCopiesAndBuiltinCallsPreserveValues, ScalarValuesPreserveResolvedTypes; IT ArrayJsonTests AnyArrayJsonIterationPreservesNativeWords; IT NumericTests |
| zero_datum_edge_cases (zero and false are values, not NULL) | Covered | IT BorrowedArrayTests BorrowedArrayCellsShareNativeStorage; IT DatumConversionTests; IT SpiRawTests CopySurvivesDisposalAndExpiresWithDestination. The pair-of-booleans datum array is Rust-specific. |
| borrow_datum (eight `BorrowDatum` clones) | Rust-specific | Values are passed by copy; the same values are covered by IT DatumConversionTests and IT CStringTests |
| from_into_datum: test_incompatible_datum_returns_error | Added | IT SpiRawTests ManagedConversionsAndErrorsRecover reads a boolean datum as `string` and asserts the message naming both types |
| from_into_datum: test_cstring_roundtrip, null_string_is_none | Covered | IT CStringTests; IT SpiRawTests; IT BorrowedBufferTests |
| roundtrip_tests: scalars, geometry, numeric, temporal, uuid, complex and their arrays | Covered | IT DatumConversionTests; IT TransactionIdTests; IT GeometryDatumTests; IT NumericTests; IT TemporalDatumTests; IT ExtendedDatumTests; IT DatumMappingTests MappedFixedStoragePreservesEveryComponentAndOwner; IT MappedArrayTests; IT ArrayDatumTests |
| roundtrip_tests: test_rt_char_0 to _7, test_rt_array_char | Different | The values pass (IT RuneTests RunesRoundTripAsVarchar, RunesSurviveEverySpiPath); text that is not one scalar value fails (TextThatIsNotOneScalarValueIsRejected) where pgrx takes the first character. Documented in the text guide. |
| roundtrip_tests: test_rt_random_data, test_rt_array_random_data | Added | IT SerializedTypeTests SeededRandomDataSurvivesStorageArraysAndExchange: seeded `RandomData(ulong, string, List<DateOnly>)` with up to 1,000 characters and dates, stored in a table, compressed, in arrays with a NULL element and through every exchange path; FrameworkValueMembersKeepTheirText covers `ulong.MaxValue` and the date bounds |

## Errors, logging, configuration, memory and workers

### pg_try_tests.rs, log_tests.rs and result_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| test_we_dont_blow_out_errdata_stack_size | Covered | IT PgDiagnosticTests RepeatedFailuresReleaseDiagnosticContexts (1,000 caught errors) |
| test_panic_in_extern_c_fn, test_pg_try_execute_with_crash, _crash_ignore, _crash_rethrow | Added | IT NativeFieldCallbackTests NativeFieldCallbackErrorsUnwindAndRecover (an ordinary exception from a callback PostgreSQL invokes becomes 38000 with its message) and OrdinaryCallbackExceptionsCanBeCaughtAcrossTheNativeFrame (caught from a subtransaction, then SPI continues) |
| test_postgres_error_contains_backtrace, test_postgres_error_runs_drop_and_has_backtrace | Added | IT PgDiagnosticTests BackendErrorsCarryTheirManagedStackTrace (a caught backend ERROR's stack trace names the failing backend call and the catching method); cleanup during a backend error: IT NativeFieldCallbackTests, IT UnrecoveredErrorTests RawErrorsRequireRollback |
| pg_try builder success, error, rethrow, ignore and finally cases | Covered | IT TryCatchExampleTests TryCatchSampleRecoversFromBackendErrors, TryCatchSampleTrapsManagedExceptions, TryCatchSampleRethrowsSelectedErrors; IT PgLogTests ErrorUnwindsAndRemainsCatchable |
| test_pg_try_throw_different_error, test_pg_try_finally_with_catch_rethrow, test_drop variants | Rust-specific | C# `try`, `catch`, `finally` and `using`; replacing a caught backend error keeps the original (IT UnrecoveredErrorTests) |
| test_drop_with_panic_no_catch | Covered | IT BadIdeasExampleTests BadIdeasSampleConvertsManagedFailuresToErrors |
| log levels, test_error, test_ereport, test_check_for_interrupts, test_panic | Covered | IT PgLogHelperTests SeverityHelpersUsePostgresRouting, ErrorHelpersPreserveDiagnosticsAndRecovery; IT PgLogTests; RT PgInterruptsTests; IT ErrorsExampleTests |
| test_ereport_domain, test_ereport_domain_value | Added | IT PgLogTests ErrorsCarryTheirMessageDomain: `PgDiagnostic.Domain` and `PgException.Domain` cross `errstart` and the captured ErrorData; backend errors report `postgres-<major>` |
| test_debug_skips_arg_evaluation, test_warning_evaluates_args | Added | IT PgLogTests InterpolatedMessagesSkipDisabledLevels: `PgLog.Write(level, $"...")` evaluates nothing for DEBUG at default thresholds and evaluates WARNING |
| result_tests: errors, custom codes, set results | Covered | IT ErrorsExampleTests; IT TryCatchExampleTests; IT SetReturningTests NestedSetFailuresRecoverInsideSpi, IteratorFailuresPreserveOwnershipAndRecover |
| result_tests: test_proper_sql_errcode | Different | An exception becomes SQLSTATE 38000; pgrx maps `Err` to 22000 |
| result_tests: Ok and Option wrappers | Rust-specific | Ordinary and nullable returns |

### guc_tests.rs

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| bool, int, MB, float, string and null-default settings | Covered | IT GucTests DefaultsAndMetadataPreserveNativeTypes, FiveTypesUseNativeParsingAndOwnedStorage; IT GucPreloadTests UnitsUseServerConversionsAndBlockSizes; IT GucLifetimeTests |
| test_enum_guc | Added | Labels, aliases and hidden values (IT GucTests); an unlabelled member is listed and set by its C# name, case-insensitively, and restored in parallel workers (IT GucTests FiveTypesUseNativeParsingAndOwnedStorage, GucParallelTests) |
| test_guc_flags | Added | IT GucTests FlagsPreserveNativeListingResetAndIdentifierRules: pg_settings flags, `RESET ALL`, and `SHOW ALL` omitting a no-show setting while listing the others |
| test_guc_check_hook and the per-type check, message and hint variants | Added | IT GucTests CheckRejectionsCoverEveryType (bool, real, string and enum: PostgreSQL's standard message, a custom message with detail and a custom message with hint; no assignment) and CheckErrorsPreserveDiagnosticsAndRecover (integer) |
| test_check_hook_fail | Added | IT GucTests ThrowingBootCheckFailsLoadAndKeepsTheBackend: an exception from the boot-value check fails LOAD with its message as an ERROR and the same backend loads the library again; a returned boot rejection stays FATAL (RejectedBootDefaultTerminatesOnlyItsBackend) |
| test_assign_hook, test_show_hook, combined hooks and check sources | Covered | IT GucTests HooksRestoreValuesAndExtraAcrossLocalAndSavepoints, AllHookTypesNormalizeAndDisplayIndependently; IT GucPreloadTests; IT GucParallelTests; IT GucSourceTests |

### memcxt, shmem, bgworker, xact_callback, xid64 and internal tests

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| memcxt: test_leak_and_drop, _with_panic, switch_to_should_switch_back_on_panic, highly_aligned_type | Covered | IT MemoryAllocationTests; IT MemoryCallbackTests; IT MemoryContextTests NestedScopesRestoreAfterFailureAndAllowDeletionRetry |
| memcxt: parent | Added | IT MemoryContextTests: in the backend the top context has no parent and the current context has one |
| memcxt: owned-context drops and allocator tests | Rust-specific | Scoped `Run`; Rust's `allocator_api` |
| shmem: lock with elog, release on drop, spinlock | Covered | IT ToolCommandSharedMemoryTests SharedMemoryLocksPreserveValuesAcrossBackendsAndFailures; RT PgLwLockTests; IT ToolCommandSpinLockTests |
| shmem: test_lock_is_released_on_unwind | Added | IT ToolCommandSharedMemoryTests (`shared_unwind`: a guard released by managed unwinding without a transaction abort, observed from a second backend) |
| bgworker: untracked worker, transaction return, slot exhaustion | Covered | IT ToolCommandBackgroundWorkerTests BackgroundWorkersRegisterAndShareState; IT ToolCommandWorkerConnectionTests |
| bgworker: test_dynamic_bgworker | Added | IT ToolCommandBackgroundWorkerTests BackgroundWorkersRegisterAndShareState: after termination ends the wait loop, the worker commits an update the launching backend reads |
| bgworker: untracked termination handle | Added | IT ToolCommandBackgroundWorkerTests: waiting on an untracked worker after termination reports Untracked, and the worker stops |
| xact_callback, xid64 | Covered | IT TransactionCallbackTests RollbackRunsAbortAndClearsCommitRegistrations; IT TransactionIdTests |
| internal: get-or-insert | Covered | IT InternalTests InternalFunctionStateSurvivesCallbacksAndReleasesAtQueryEnd |
| internal: internal_insert | Added | IT InternalTests InternalValuePayloadsKeepTheirExactType: `int` payloads read back by value and a read as `long` is rejected; Ankus state is immutable, so a second value is new state rather than an in-place insert |

## Types, triggers, schemas, aggregates and declarations

| pgrx test | Status | Ankus evidence |
| --- | --- | --- |
| postgres_type: raw-layout, custom-text, JSON and tagged-enum types | Covered | IT NativeLayoutTypeTests; IT CustomTextTypeTests; IT SerializedTypeTests; IT PolymorphicTypeTests; IT TypeVariantsExampleTests |
| postgres_type: test_my_enum_type | Different | A raw-layout root enum is rejected (ANKUS436); enums are allowed as fields |
| postgres_type: test_call_with_null, test_call_with_enum_null | Added | IT CustomTextTypeTests NullableCustomTextParametersReceiveSqlNull: the methods run on NULL for a custom-text record and enum, and neither function is strict |
| trigger tests | Covered | IT TriggerTests; IT TriggerLifecycleTests; IT SqlGenerationTests CustomSqlTriggerFunctionsExecuteAndRecover; GT PgTriggerGenerationTests |
| trigger: before_insert_metadata_safe | Added | IT TriggerTests TriggersOpenTheirRelationFromItsOid; other metadata: IT TriggerTests |
| schema tests | Covered | IT SchemasExampleTests; IT FunctionDeclarationTests; IT TypedArrayCallbackTests; GT SqlGenerationGeneratorTests; IT SqlGenerationTests; IT DeclarationSqlTests |
| aggregate: sum, moving window, shared implementation, percentile, anyelement | Covered | IT AggregateTests; IT SharedTypedAggregateTests; IT AggregatePercentileSampleTests; IT PolymorphicAggregateTests |
| aggregate: aggregate_demo_custom_state | Added | IT AggregateTests CustomStateFromAnotherSchemaSums: the state is `custom_values.number` for an aggregate in `aggregate_values`; same-schema custom state and cross-schema composite state are covered elsewhere |
| aggregate: aggregate_first_json, aggregate_first_jsonb | Added | IT AggregateTests JsonStatesKeepTheFirstValue (state type, strict, immutable and parallel-safe transition in the catalog) |
| type_ident tests | Rust-specific | Compiler type identity; GT ManagedTypeIdentityTests, PgFunctionGeneratorTests, PgEnumGenerationTests |
| pg_cast: explicit, assignment and implicit casts and catalog flags | Covered | IT OperatorCastTests |
| pg_cast: assert_composite_cast_exists | Added | IT CompositeDatumTests CompositeDefaultsVariadicsAndCastsExecute installs and runs an explicit function cast between two composite types, including strict NULL |
| struct_type and complex tests | Covered | IT DatumMappingTests MappedFixedStoragePreservesEveryComponentAndOwner |
| postgres_type_variants_smoke | Covered | IT TypeVariantsExampleTests |
| sql_translatable_signature: nested arrays | Added | GT PgFunctionGeneratorTests UnsupportedSignaturesAreRejected: ANKUS039 for a nested result and ANKUS040 for nested vector and `PgArray` arguments |
| sql_translatable_signature: signature matrix | Added | IT SignatureMatrixTests InstalledSignaturesMatchTheTypeMatrix reads 38 installed identity, set and table signatures back from `pg_proc` |
| proptests (eight temporal round trips) | Added | IT PropertyRunnerTests TemporalValuesRoundTripForGeneratedInputs runs all eight with `PgPropertyRunner` and pgrx's strategies over raw values (saturating dates and timestamps, wrapping times and offsets), through SPI parameters and the type's text literal, with shrinking. IT TemporalDatumTests SeededTemporalValuesRoundTripExactly also checks 256 seeded values per type through seven exchange paths against the `*_send` bytes |
| operator, pgbox, oid, enum, attribute, name, long-name tests | Covered | IT OperatorCastTests; IT CompositeDatumTests; IT NativeBoxTests; IT OidTests; IT EnumDatumTests; IT ToolCommandBackendTests; IT SchemasExampleTests; GT PgBackendTestDeclarationTests |
| composite_type: test_array_of_composite_type | Added | IT CompositeDatumTests CompositeDefaultsVariadicsAndCastsExecute sets a composite field and an array-of-composite field with a NULL row in code |
| cfg_tests | Added | Test publications define `ANKUS_TESTS` and benchmark publications `ANKUS_BENCHMARKS` (BT SdkPostgresSelectionTests TestAndBenchmarkPublicationsDefineTheirSymbols); IT ToolCommandBackendTests calls a function compiled only under `#if ANKUS_TESTS` from a backend test, beside code under an undefined symbol that names a missing type |
| bindings_of_inline_fn | Added | IT ItemPointerTests InlineItemPointerBindingRunsInTheBackend calls the generated binding of the static inline `itemptr_encode` in the backend and compares it with `PgItemPointer.ToIndexKey` |
| pgrx_module_qualification | Added | GT PgFunctionGeneratorTests DeclarationsCompileBesideShadowingTypeNames: every declaration kind compiles beside types named `System`, `Ankus`, `Spi`, `Exception` and other names generated code uses |
| pg_guard, lifetime and derive_pgtype_lifetimes (compile-only) | Rust-specific | Callback boundaries (GT NativeCallbackGeneratorTests) and borrowed views (IT BorrowedBufferTests, BorrowedBufferResultTests, CStringResultTests) |
| issue1134 | Covered | IT AggregateOrderingTests HypotheticalRankMatchesNativeMultipleKeysAndNullRows |

## Remaining gaps and partials

None. Every case is covered, added, a documented difference or Rust-specific.
