# pgrx inline unit-test mapping

This maps the inline unit tests of pgrx 0.19.3, the functions marked `#[test]` or
`#[pg_test]` in the `src/` trees below, to the Ankus tests that verify the same
behavior, or records why a case does not apply. It is the inline unit-test
mapping that the [parity requirements](parity-requirements.md) call for, beside
the [unit-test mapping](pgrx-test-mapping.md) and the
[tooling test mapping](pgrx-tooling-test-mapping.md).

- `pgrx/src/`: `iter.rs`, `list.rs`, `pgbox.rs`, `prelude.rs` and `varlena.rs`
- `pgrx-macros/src/lib.rs`
- `pgrx-sql-entity-graph/src/`: `aggregate/aggregate_type.rs`, `aggregate/mod.rs`,
  `control_file.rs`, `extension_sql/entity.rs`, `extern_args.rs`,
  `metadata/sql_translatable.rs`, `pg_extern/attribute.rs`, `pgrx_sql.rs`,
  `section.rs` and `used_type.rs`
- `pgrx-pg-config/src/lib.rs`
- `pgrx-pg-sys/src/`: `node.rs` and `submodules/datum.rs`
- `pgrx-bench/src/lib.rs`
- `cargo-pgrx/src/`: `cargo.rs`, `metadata.rs`, `object_utils.rs` and
  `command/{init,install,regress,run,schema,test,upgrade}.rs`

The inline tests in `cargo-pgrx/src/command/bench.rs` and
`pgrx-tests/src/framework.rs` are mapped in the tooling test mapping. Cases listed
here that the tooling test mapping already maps keep its status and refer to it.

A case is one pgrx test function; a row that lists several counts each. The
audit covers 156 cases, read against Ankus at revision **2b841a3** and updated
with the tests added on 2026-10-09 and 2026-10-10: 19 `pgrx` cases, 5 macro cases, 71 SQL
entity graph cases, 2 `pg_config` cases, 6 binding cases, 7 benchmark cases and
46 `cargo-pgrx` cases, in 107 rows. By row, 78 are Covered, 14 Different and
15 Rust-specific. Only the same behavior and edge cases count as
covered; a test of the same feature with different boundaries is partial.

Paths are relative to the repository: **IT** is `tests/Ankus.IntegrationTests/`,
**RT** is `tests/Ankus.Runtime.Tests/`, **GT** is `tests/Ankus.Generators.Tests/`,
**BT** is `tests/Ankus.Build.Tests/`, **TE** is `tests/Ankus.TestExtension/` and
**PT** is `tests/Ankus.PgConfig.Tests/`. As in the other mappings, a test is named
by its file and method; a file can hold part of a class with another name. Guides
are under `docs/src/content/docs/`.

Statuses: **Covered**, **Partial** (what is missing is named), **Gap** (no Ankus
test; the row says whether the feature exists), **Different** (a deliberate
behavior difference, with the guide that documents it) and **Rust-specific** (no
.NET analogue; the replacing .NET guarantee and its test are named).

## Summary

The Ankus source generator resolves SQL types, NULL handling and dependency order
while compiling, so most SQL entity graph cases map to generator tests, and
pgrx's errors at schema generation become diagnostics at the declaration. Ankus
embeds one length-delimited schema document per library, so pgrx's sentinel and
padding entries have no counterpart; the section reader is tested on ELF, PE and
Mach-O images. `ankus schema` selects items with their prerequisites and
`ALTER EXTENSION` attachments as `cargo pgrx schema` does. Command-line cases
differ where Ankus uses `--pg`, literal `--property` assignments, runner filters
after `--` and NuGet version ranges in place of Cargo's positional arguments,
flags and requirements. Download retries and `pg_config` vendor suffixes now
match pgrx; `pg_config` values supplied through environment variables serve
cross builds that Ankus's native layout probe does not allow. No row remains
partial.

## pgrx

### prelude.rs and iter.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| prelude_reexports_impl_sql_translatable | Rust-specific | The test checks that the prelude re-exports `impl_sql_translatable!`. Ankus attributes are reached through the `Ankus` namespace, and `[PgDatumType]` with `PgTypeOrigin.External` maps a managed type to an existing SQL type for arguments and results without `CREATE TYPE`: GT DatumMappingGeneratorTests DatumMappingsPreserveEveryScalarSetAndTableContract. The macro's defaults are compared in the `sql_translatable.rs` rows |
| setof_iterator_is_not_argument_sql | Covered | GT PgSetGenerationTests SetSupportRetainsOrdinaryFunctionRestrictions: an `IEnumerable<int>` argument is ANKUS040 |
| table_iterator_maps_multi_column_returns | Covered | GT PgSetGenerationTests TableTupleColumnsGenerateExactNamesAndTypes: a three-column tuple result becomes `RETURNS TABLE` with each column's SQL type in order |
| table_iterator_returns_first_error | Covered | A TABLE row with invalid columns is one ANKUS397 at the return type, including a row with two invalid columns: GT PgSetGenerationTests InvalidSetRowShapesAndNamesAreDiagnosed, SetResultDiagnosticsPointAtTheCorrectSyntax |

### list.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| list_nil_is_default, list_nil_is_empty, list_nil_as_ptr_is_null | Covered | RT PgListTests DefaultNilIsBackendIndependent: a default `PgList<int>` is NIL, with `Count` and `Capacity` 0, `IsEmpty` set and null list and cell pointers, without a backend |
| list_nil_into_ptr_is_null | Covered | Detaching a NIL list returns null without a backend and consumes the wrapper: RT PgListTests DefaultNilIsBackendIndependent; a null pointer borrows as NIL: BorrowAndDetachPreserveNativeIdentity |

### varlena.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| encode_vlen_4b_known_values, encode_vlen_4b_roundtrip | Covered | Generated C sets 4-byte headers with PostgreSQL's `SET_VARSIZE`. The one managed encoder, in `PgMemoryContext.CreateFlatArray<T>`, writes the total size shifted left two bits on little-endian targets, which reads back as the size, followed by the array fields: RT PgFlatArrayTests HeadersUseTheFourByteVarlenaEncoding; PostgreSQL reads those arrays back: IT TypedArrayCallbackTests FlatArraysBuildInPlaceAndReturn |
| encode_vlen_1b_known_values, encode_vlen_1b_roundtrip, encode_vlen_1b_always_sets_short_flag | Rust-specific | pgrx reimplements the short-header macro in Rust. Ankus's generated C calls PostgreSQL's `SET_VARSIZE_SHORT`: IT VarlenaOwnershipTests VarlenaShortHeaderBoundaryPreservesZeroPayload reads the short-header flag for 125 and 126 payload bytes and a 4-byte header at 127, on either byte order |

### pgbox.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| from_pg_null_is_null, from_pg_null_as_ptr_returns_null, into_pg_null_returns_null | Rust-specific | `PgBox` can wrap a null pointer; Ankus has no null wrapper. `DangerousBorrow` and `DangerousAdoptBox` return a null reference for a null address, without backend access: RT NativeReferenceTests NullRawViewsAndOwnersNeedNoCapabilityOrLiveContext; IT NativeBoxTests NativeBoxConstructionPreservesFiveZeroAndNullPointerContracts |
| from_pg_non_null_is_not_null, from_pg_non_null_as_ptr_matches | Covered | RT NativeReferenceTests RawStackAliasesShareValuesUsingExplicitLifetimeAnchor: borrowing a stack address gives a non-null reference whose `DangerousGetPointer` is that address, and nothing is allocated, adopted or freed |
| into_pg_non_null_returns_same_ptr | Covered | `DangerousDetach` returns the box's original pointer: IT NativeBoxTests RawDetachInvalidatesSharedViewsBeforeFreshAdoption; it returns the allocation's address and frees nothing: RT NativeBoxTests FailedDetachPreservesOwnerAndViewsUntilSuccessfulTransfer |

## pgrx-macros

### lib.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| short_name_unchanged, exactly_63_chars_unchanged, exactly_64_chars_is_shortened, very_long_name_fits_in_63 | Different | pgrx keeps a test name of up to 63 characters and shortens longer ones to 63. Ankus names every backend test function `ankus_test_` and 32 hexadecimal digits from a SHA-256 hash of the assembly and method name, 43 characters at any length, and reports keep the C# name (`getting-started/testing.md`, "Declare tests inside the extension"): GT PgBackendTestDeclarationTests PreservesBackendCaseMetadataAndStableNames, where a 79-character method gets such a name that stays stable across unrelated source changes |
| different_long_names_get_different_shortened_names | Covered | GT PgBackendTestDeclarationTests LongNamesDifferingAtTheEndGetDifferentSqlNames: two test names past the identifier limit that differ only at the end get different SQL names within the limit; PreservesBackendCaseMetadataAndStableNames keeps the names stable across unrelated edits |

## pgrx-sql-entity-graph

### control_file.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| uses_the_supplied_cargo_version_for_substitution | Different | Ankus writes `default_version` from `AnkusExtensionVersion`; an authored value must match it, and control assignments are literal, with no token expansion (`reference/build-settings.md`, "Extension control settings" and "Version-specific control files"): BT ExtensionControlCommandTests PrimaryPackageRetainsIdentityAndSql; BT ExtensionControlSettingsTests RejectsConflictingOrUnsupportedSettings; IT ToolCommandPropertyTests ProjectGlobalPropertiesReachNativePublicationAndQueries (`get default_version` prints the project's version) |

### section.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| round_trip_basic_entry | Covered | The writer and reader agree: GT InstallationGraphEncodingTests InstallationGraphEncodingRoundTripsWithConsumer (empty and populated graphs); BT NativeSchemaEmitterTests LinkedLibraryRetainsEmbeddedSchema reads string, integer and Boolean fields back from a linked, dead-stripped library; PT ExtensionSchemaTests ReadsIndependentMetadata |
| ignores_trailing_zero_padding | Covered | PT ExtensionSchemaTests ReadsIndependentMetadata accepts 0, 1 and 511 zero bytes after the document; PT ExtensionSchemaTests RejectsInvalidFraming rejects a nonzero trailing byte |
| zero_filled_section_decodes_as_empty, sentinel_entry_decodes_as_empty, sentinel_entry_is_ignored_mid_section | Rust-specific | The linker joins pgrx's per-declaration entries and a sentinel, so a section can hold only padding or a sentinel. Ankus emits one length-delimited document per library, rejects empty and zero-filled sections (PT ExtensionSchemaTests RejectsInvalidFraming, RejectsShortHeaders) and duplicate sections (PT NativeSchemaSectionTests RejectsMissingAndDuplicateSections), and embeds a valid empty graph for an extension without declarations: GT InstallationGraphEncodingTests InstallationGraphEncodingRoundTripsWithConsumer; PT ExtensionSchemaGraphTests EmptyGraphHasDefinedOutputs |
| recognizes_macho_qualified_section_name | Covered | The reader parses section headers itself: `.ankusc` in ELF and PE, and `__ankusc` in the `__DATA` segment of x64 and ARM64 Mach-O (PT NativeSchemaSectionTests ReadsEachPublishedFormat). A renamed section is not found (PT NativeSchemaSectionTests RejectsMissingAndDuplicateSections), and a Mach-O section in another segment or an ELF name with an extra character is rejected (PT NativeSchemaSectionTests RejectsMalformedImage) |
| schema_section_names_fit_windows_image_limits | Covered | `.ankusc` fits PE's 8-byte section name: PT NativeSchemaSectionTests ReadsEachPublishedFormat reads the win-x64 name field, and BT NativeSchemaEmitterTests LinkedLibraryRetainsEmbeddedSchema links a DLL with clang-cl on Windows and reads the section back |
| round_trip_function_metadata_type_preserves_type_origin, round_trip_function_metadata_type_preserves_array_mappings, round_trip_function_metadata_type_preserves_composite_array_mappings, round_trip_function_metadata_type_preserves_nested_array_errors | Rust-specific | pgrx records each argument's Rust type, origin and SQL mapping, or a deferred error, for `cargo pgrx schema` to resolve. The Ankus generator resolves them while compiling and embeds finished SQL. Origin: GT DatumMappingGeneratorTests DatumMappingsPreserveEveryScalarSetAndTableContract; array types: GT RawDatumGeneratorTests RawSignaturesCompileWithExactSqlBindings; composite arrays: GT PgCompositeGenerationTests CompositeSignaturesCompileWithNamedAndAnonymousTypes; nested arrays are ANKUS039 and ANKUS040 at compile time: GT PgFunctionGeneratorTests UnsupportedSignaturesAreRejected |
| round_trip_sql_declared_type_preserves_type_ident_and_sql | Rust-specific | pgrx keeps the Rust type identity so later resolution can find the declared SQL type. Ankus resolves the managed identity with the compiler's type symbol (GT ManagedTypeIdentityTests ManagedProviderKeysPreserveCompilerEquality), and the encoded graph keeps the provided type's exact names and `TYPE` attachment (GT PgFunctionGeneratorTests SqlTypeProviderValidNamesPreserveExactGraphIdentity) |
| round_trip_sql_declared_function_skips_type_ident | Covered | GT PgSchemaGraphMetadataTests SchemaGraphRetainsCustomFunctionProviders: function providers survive graph encoding as exact signatures and `FUNCTION` attachments with no type identity |

### used_type.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| internal_is_marked_optional | Different | pgrx always treats `Internal` as optional. Ankus follows the parameter's annotation: `PgInternal?` receives SQL NULL and `PgInternal` is required (`internal-state.md`, "Native pointers"; `getting-started/from-pgrx.md`, "SQL NULL and values"): IT InternalTests InternalNativePointersPreserveNullZeroAndWritableValues; GT InternalGeneratorTests InternalFunctionSignaturesCompileWithCheckedTransport |
| nullable_is_marked_optional | Covered | A nullable parameter makes the function `CALLED ON NULL INPUT` and a required signature is `STRICT`: GT SqlNullabilityTests ExplicitSqlAnnotationsDetermineStrictness (`string?`, `byte[]?`); GT DatumMappingGeneratorTests DatumMappingsPreserveEveryScalarSetAndTableContract (nullable class, struct and enum mappings) |

### extern_args.rs and pg_extern/attribute.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| extern_args.rs: parse_args; attribute.rs: plain_string_expected, escaped_quotes_in_plain_string | Covered | `[PgTest(ExpectedError = ...)]` keeps the exact decoded text: GT PgFunctionGeneratorTests BackendTestDiagnosticMetadataControlsRemainExact (`café\n"expected"`); IT ToolCommandBackendTests GeneratedBackendTestsDiscoverExecuteAndRollback (`foo "bar"` matches the error raised in the backend) |
| raw_string_with_embedded_quotes, raw_string_with_nested_hashes | Rust-specific | Rust raw-string syntax has no counterpart, and the generator reads the attribute's constant value from the compiler rather than the literal's tokens, so no delimiter can reach the message. A named constant arrives decoded and changes the catalog when it changes: GT PgTestIncrementalTests PgTestCatalogTracksReferencedMetadata; GT PgFunctionGeneratorTests BackendTestDiagnosticMetadataControlsRemainExact |
| error_alias_works_like_expected | Rust-specific | `PgTest` has one property, `ExpectedError`, so there is no alias, and a misspelled property name is a C# compile error. No test |
| other_attrs_alongside_expected_do_not_interfere | Covered | `ExpectedError` beside other options keeps every value: GT PgFunctionGeneratorTests BackendTestDiagnosticMetadataControlsRemainExact (with `IgnoreReason`); GT PgBackendTestDeclarationTests PreservesBackendCaseMetadataAndStableNames (with `IgnoreReason` and `[PgSchema]`). Volatility and strictness are not `PgTest` options; `PgTest` with `PgFunction` is ANKUS299: GT PgFunctionGeneratorTests BackendTestFailuresHaveSpecificDiagnostics |
| malformed_input_is_a_syn_error_not_a_panic | Rust-specific | `[PgTest(ExpectedError)]` without a value is a C# compile error; the generator never parses attribute tokens. Invalid values are diagnostics, such as ANKUS307 for NUL and lone surrogates (GT PgFunctionGeneratorTests BackendTestFailuresHaveSpecificDiagnostics), and incomplete attribute arguments do not fault the generator (GT IncompleteAttributeRecoveryTests IncompleteColumnNamesReportWithoutFaulting) |

### aggregate/mod.rs and aggregate/aggregate_type.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| agg_required_only | Covered | GT AggregateGeneratorTests AggregateMinimalContainerEmitsExactCompilableSql: a transition-only aggregate emits exactly one support function, `sum_values_transition`, its aggregate and one export pair |
| agg_all_options | Covered | All eight support functions and parallel safety: GT TypedAggregateGeneratorTests TypedAggregateOptionalCapabilitiesCompileAndRetainSqlRoles; final and moving modify and `MINITCOND`: GT AggregateGeneratorTests AggregateMovingStateFinalExtraAndNullableInverseCompile; `HYPOTHETICAL` and `READ_WRITE`: GT AggregateGeneratorTests AggregateOrderedDirectAndExtraArgumentsHaveExactSignatures; `SORTOP`: GT AggregateGeneratorTests AggregateCallbackOverridesPreserveCommonOptionsAndQuotedNames |
| agg_missing_required | Covered | `[PgAggregate]` on an empty class is ANKUS029: GT AggregateGeneratorTests InvalidAggregateDiscoveryAndContainersAreDiagnosed; a missing transition is ANKUS112 and CS0535: GT TypedAggregateGeneratorTests TypedAggregateCompilerRejectsMissingTransition |
| aggregate_type.rs: solo, list | Covered | A scalar argument type is one SQL input: GT TypedAggregateGeneratorTests TypedAggregateScalarEmitsExactSqlAndCompiles; a tuple is one input per element, up to eight: GT TypedAggregateGeneratorTests TypedAggregateTupleInputsRemainSeparateSqlValues; a one-element tuple is one input: GT TypedAggregateGeneratorTests TypedAggregateSingleTupleInputCompiles |

### extension_sql/entity.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| build_type_accepts_extension_owned_types | Covered | `PgSqlTypeProvider` supplies an extension-owned mapping under its SQL name, and the block precedes its consumers: GT DatumMappingGeneratorTests DatumMappingProvidersShareOneCatalogDeclaration |
| build_type_rejects_external_types | Covered | A managed provider for an external mapping is ANKUS383: GT DatumMappingGeneratorTests DatumMappingProvidersRejectInvalidOwnership; GT PgFunctionGeneratorTests SqlTypeProviderCannotClaimExternalManagedMapping. pgrx's separate `Enum` form has no counterpart; one provider form serves every type |
| function_declarations_do_not_carry_type_idents | Covered | GT PgSchemaGraphMetadataTests SchemaGraphRetainsCustomFunctionProviders: `PgSqlFunctionProvider` records exact signatures with no type identity |

### metadata/sql_translatable.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| impl_sql_translatable_sets_external_defaults | Different | pgrx's macro defaults to an external, unqualified mapping. `[PgDatumType]` defaults to `PgTypeOrigin.ThisExtension`, and an external mapping requires an explicit `Schema` (`raw-values.md`, "Ownership and external types"): GT DatumMappingGeneratorTests DatumMappingsPreserveEveryScalarSetAndTableContract (an external mapping in arguments and results), DatumMappingsRejectInvalidMetadata (an external mapping without a schema is ANKUS141). The identity is the compiler's type symbol: GT ManagedTypeIdentityTests ManagedProviderKeysPreserveCompilerEquality |
| impl_sql_translatable_supports_arg_only_types | Covered | A reader-only converter maps arguments, and using it as a result is ANKUS153: GT DatumMappingGeneratorTests DatumMappingsRespectOneWayCapabilities, DatumMappingsRejectUnavailableFunctionDirections |
| array_argument_sql_wraps_scalar_kinds, array_return_sql_wraps_scalar_kinds | Different | Scalar and composite element types become `T[]` in arguments and results: GT RawDatumGeneratorTests RawSignaturesCompileWithExactSqlBindings; GT PgCompositeGenerationTests CompositeSignaturesCompileWithNamedAndAnonymousTypes. pgrx also writes `NUMERIC(10, 2)[]`, which PostgreSQL discards in function signatures. Ankus writes no type modifiers and enforces `[PgNumericPrecision]` on scalar values in the dispatcher (`numeric.md`, "Function constraints"); on a nonnumeric type such as `int` or `string` it is ANKUS003: GT PgFunctionGeneratorTests InvalidNumericConstraintsAreRejected |
| nested_vec_arrays_fail_fast | Covered | GT PgFunctionGeneratorTests UnsupportedSignaturesAreRejected: `int[][]` and `PgArray<PgArray<int>>` are ANKUS039 as results and ANKUS040 as arguments |
| nested_numeric_arrays_fail_fast, nested_composite_arrays_fail_fast | Covered | Nested arrays are rejected for `int`, `PgNumeric` and `PgHeapTuple` as results and arguments: GT PgFunctionGeneratorTests UnsupportedSignaturesAreRejected; and for mapped types: GT DatumMappingGeneratorTests DatumMappingsRejectUnsupportedContainers; GT DatumMappingDiagnosticsTests DatumMappingSignatureDiagnosticsIdentifyConsumedTypes (`Value[][]`, ANKUS154) |

### pgrx_sql.rs: dependency graph and type resolution

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| external_function_type_resolution_succeeds | Covered | A consumer of an external mapping needs no provider and does not depend on an unused one: GT DatumMappingGeneratorTests DatumMappingSchemasAndExternalOriginsRemainIndependent; existing catalog bindings need no provider: GT DeclaredTypeProviderGeneratorTests DeclaredTypeProvidersLeaveExistingExternalBindingsAndNativeContractsUnchanged |
| extension_sql_declared_type_orders_before_function_and_aggregate | Covered | A `PgSqlTypeProvider` block precedes every consumer, including parameters, results, arrays, sets, TABLE columns, aggregate states and arguments, operators and casts: GT DeclaredTypeProviderGeneratorTests DeclaredTypeProvidersOrderEveryBoundSignature; the installed extension uses each signature: IT DeclaredTypeProviderTests DeclaredProvidersInstallEveryBoundSignature |
| declared_type_cycle_prefers_explicit_requirements_with_shell_type | Covered | Explicit requirements place the shell type before the input and output functions and the complete type after them: GT DeclaredTypeProviderGeneratorTests DeclaredTypeProvidersDeferOnlyExplicitReversePaths; GT TypedDependencyGeneratorTests TypedDependenciesPreserveExplicitShellOrdering; an explicit hard cycle is ANKUS499: GT DatumMappingGeneratorTests DatumMappingProvidersPreserveExplicitShellOrdering. TE's `u24` shell type, input and output functions and complete type install and run: IT DeclaredTypeProviderTests CompleteProviderPreservesManualByValueStorage |
| extension_sql_declared_type_in_custom_schema_prefixes_aggregate_state_type | Covered | A type declared in custom SQL in another schema keeps its schema as a transition state (IT DeclaredTypeProviderTests DeclaredProviderAggregateRetainsFirstValue; GT AggregateGeneratorTests AggregateDependenciesOrderEnumCompositeAndCustomSqlDeterministically) and as a moving state, `MSTYPE = "pets"."dog"`: GT AggregateGeneratorTests MovingStatesKeepCustomSchemaTypes |
| skipped_function_argument_does_not_require_schema_resolution | Covered | Injected `PgFunctionContext` and `PgMemoryContext` parameters are left out of the SQL signature, and names, defaults and NULL handling are unchanged: GT FunctionContextGeneratorTests FunctionContextsPreserveSqlSignatures |
| explicit_composite_type_does_not_require_schema_resolution, explicit_composite_array_type_does_not_require_schema_resolution | Covered | Named composites and composite arrays in arguments and results render as the bound name, with `[]` for arrays, and need no `CREATE TYPE` or provider: GT PgCompositeGenerationTests CompositeSignaturesCompileWithNamedAndAnonymousTypes |
| explicit_composite_array_aggregate_state_does_not_require_schema_resolution | Covered | A `PgArray<PgHeapTuple?>` state bound to `pets.dog` gives `STYPE = "pets"."dog"[]` (GT AggregateGeneratorTests AggregateOrdinaryStatesKeepContextualTypeBindings) and, as a moving state, `MSTYPE = "pets"."dog"[]` (GT AggregateGeneratorTests MovingStatesKeepCustomSchemaTypes) |
| duplicate_type_ident_errors | Rust-specific | A mapping's identity is the compiler's type symbol rather than an authored `TYPE_IDENT` string: GT ManagedTypeIdentityTests ManagedProviderKeysPreserveCompilerEquality. A second mapping or provider claim for one identity is reported at the second declaration: GT DatumMappingDiagnosticsTests DuplicateDatumMappingDiagnosticsIdentifyTheSecondDeclaration; GT PgFunctionGeneratorTests SqlTypeProviderDuplicateClaimsIdentifySecondIdentity (ANKUS393, ANKUS394) |
| unresolved_function_argument_type_ident_errors, unresolved_function_return_type_ident_errors, unresolved_aggregate_argument_type_ident_errors, unresolved_aggregate_stype_type_ident_errors, unresolved_aggregate_mstype_type_ident_errors | Different | pgrx reports an unresolved type at each argument, result, `STYPE` or `MSTYPE` while generating SQL. Ankus reports an extension-owned mapping without a managed provider once, as ANKUS395 at the mapping declaration, so no use reaches SQL generation unresolved (`custom-sql.md`, "Type provider diagnostics"; `raw-values.md`, "Ownership and external types"): GT DatumMappingGeneratorTests DatumMappingProvidersRejectInvalidOwnership |

### pgrx_sql.rs: item selection and extension attachments

`cargo pgrx schema` emits named items with their prerequisites, wrapped in a
transaction with `ALTER EXTENSION ... ADD` statements unless
`--no-alter-extension` is given. `ankus schema` item selection does the same
(`getting-started/publishing.md`, "Select declarations").

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| to_sql_for_items_emits_only_targets_and_deps_with_lib_substitution | Covered | Selection includes transitive prerequisites once, in installation order, leaves out unrelated declarations and uses `'$libdir/<library>'`: PT ExtensionSchemaGraphTests SelectionClosesDependenciesAndDeclarationFamilies, OperatorSelectionResolvesItsFamilyWithoutConsumers; IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| resolve_item_rejects_ambiguous_name_without_path | Covered | An overloaded name fails with an error that lists the candidates, and an exact signature or a qualified managed name selects one: PT ExtensionSchemaGraphTests SelectionRejectsAmbiguityAndAcceptsExactSignatures |
| item_slice_uses_control_schema_without_changing_full_schema | Covered | A fixed control schema qualifies generated function names and owned type references in selected SQL and its `ADD FUNCTION`, while the full script keeps none of the markers: PT ExtensionSchemaGraphTests SelectionQualifiesCompilerMarkedIdentifiers. With a fixed schema and `search_path` set to `public`, the selected type, enum, functions and aggregates are created in the control schema and join the extension: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| item_slice_keeps_external_array_types_unqualified_and_honors_pg_schema | Covered | Only compiler-marked identifiers are qualified: PT ExtensionSchemaGraphTests SelectionQualifiesCompilerMarkedIdentifiers; declarations with an explicit schema carry no marker: GT PgSchemaGraphMetadataTests SchemaGraphRetainsTypedDeclarationsAndFamilies; under a fixed control schema, a selected `count_arguments(VARIADIC integer[])` and a function generated in its own `[PgSchema("own")]` install and join the extension: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_attaches_bare_function | Covered | A zero-argument function is attached as `ADD FUNCTION query();` inside `BEGIN` and `COMMIT`: PT SchemaProvenanceTests ProvenancePreambleSurvivesSelection; the selected script is one transaction: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_includes_argument_types | Covered | GT PgSchemaGraphMetadataTests SchemaGraphRetainsTypedDeclarationsAndFamilies (`FUNCTION "s"."same"("s"."kind","s"."kind")`); `read_value(value)` and `make_value(integer)` join the extension: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_attaches_operator_in_addition_to_function | Covered | A function owns its operator and each has its own attachment: GT PgSchemaGraphMetadataTests SchemaGraphRetainsTypedDeclarationsAndFamilies, SchemaGraphRetainsSuppressedAndReplacedFamilies; selecting the function adds `ADD OPERATOR s.===(s.kind, s.kind)`: PT ExtensionSchemaGraphTests SelectionClosesDependenciesAndDeclarationFamilies |
| alter_extension_attaches_type_and_its_io_functions | Covered | The selected `Value` type joins the extension with its input and output functions, checked in `pg_depend`, and `DROP EXTENSION` removes them: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_attaches_enum | Covered | GT PgSchemaGraphMetadataTests SchemaGraphRetainsTypedDeclarationsAndFamilies (`TYPE "s"."kind"`); the selected `choice` enum joins the extension: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_attaches_aggregate_with_args | Covered | Aggregate attachments use PostgreSQL's star, variadic and ordered-set signatures: GT PgSchemaGraphMetadataTests SchemaGraphPreservesAggregateAttachmentSignatures; `total(integer)` joins the extension: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_attaches_trigger | Covered | A selected trigger function joins the extension under its argument-free signature, checked in `pg_depend`: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_attaches_ord_emits_family_and_class, alter_extension_attaches_hash_emits_family_and_class | Covered | The `choice_btree_ops` and `choice_hash_ops` operator classes and their operator families join the extension, checked in `pg_depend`: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery; the families are selectable by name: GT PgSchemaGraphMetadataTests SchemaGraphRetainsCompleteDerivedFamilies |
| alter_extension_attaches_schema_but_skips_public | Covered | A declared schema is attached (PT ExtensionSchemaGraphTests SelectionClosesDependenciesAndDeclarationFamilies; IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery), `public` with creation enabled is not, while its function still joins the extension (the same IT test), and `Create = false` gives no attachment: GT SchemaIncrementalTests SchemaEmissionTracksCreationPolicy |
| alter_extension_custom_sql_with_creates_emits_add_type | Covered | A `PgSqlTypeProvider` block carries a `TYPE` attachment: GT PgFunctionGeneratorTests SqlTypeProviderValidNamesPreserveExactGraphIdentity; a selected function's prerequisite type is emitted with `ADD TYPE` and no warning: PT ExtensionSchemaGraphTests SelectionClosesDependenciesAndDeclarationFamilies; a custom SQL function provider joins the extension: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_custom_sql_without_creates_warns | Covered | A custom block without a declared object inventory is emitted unchanged with no attachment and one warning naming the block and, when its provenance comment records one, its source file and line; detached selection gives no warning: PT ExtensionSchemaGraphTests CustomSqlReportsUnattachedObjects |
| no_alter_extension_mode_matches_pre_feature_output | Covered | `alterExtension: false` emits no `BEGIN`, `COMMIT` or `ALTER EXTENSION`: PT ExtensionSchemaGraphTests OperatorSelectionResolvesItsFamilyWithoutConsumers; with `--no-alter-extension` the selected script runs and its objects stay out of the extension: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| alter_extension_substitutes_module_pathname | Covered | IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery (no `MODULE_PATHNAME`; `$libdir/` and the library name); PT ExtensionSchemaGraphTests OperatorSelectionResolvesItsFamilyWithoutConsumers (`'$libdir/Probe.so'`) |

## pgrx-pg-config

### lib.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| parse_version | Covered | PT PostgresVersionTests ParseStableVersionReturnsExpectedValues accepts pgrx's stable versions, including `11.2-FOO-BAR+` and `10.22-` with a vendor suffix after the minor version; ParseInvalidVersionThrowsFormatException rejects pgrx's five invalid strings |
| from_empty_env | Different | Ankus runs the selected `pg_config` and measures native layouts with a probe that must execute on the build host (`raw-values.md`), so a build always has a runnable `pg_config`. pgrx's environment-supplied values serve cross builds that this measurement does not allow |

## pgrx-pg-sys

### node.rs and submodules/datum.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| cast_roundtrip | Covered | IT NodeTests RangeTableNodeRoundtripPreservesNativeValueAndAddress: a `RangeTblRef` with `rtindex` 9 cast to `Node` and back keeps its tag, index and address; RT NodeReferenceTests NodeCastsShareValuesTagsAndOriginalRawAddress; BT NativeBindingNativeCastTests CompiledNodesPreserveCastInheritanceAndAliases |
| inheritance_downcast, inheritance_upcast | Covered | IT NodeTests NativeInheritanceRetainsTagAndRejectsUnrelatedNodes: an `AlternativeSubPlan` cast to `Expr` and `Node` and back keeps its tag and address, and a cast to `Var` fails; BT NativeBindingNativeCastTests CompiledNodesPreserveCastInheritanceAndAliases |
| value_struct_cast, value_union_cast | Covered | IT NodeTests ValueNodesCastByServerVersion casts through the bindings generated from the server's own headers: on 13 and 14, `Value` with `T_Integer` 42 and with the `T_Float`, `T_String`, `T_BitString` and `T_Null` tags keeps its tag and payload through `Node` and back; from 15, a `ValUnion` holding a `String` reads as `Node` and `String`, and neither the union nor `Node` downcasts to `ValUnion`. BT NativeBindingNativeCastTests CompiledValueTagsPreserveVersionedUnionRules checks the same rules for every major 13 through 19 |
| datum.rs: roundtrip_integers | Rust-specific | `Datum`'s conversions from `i64`, `isize`, `u64` and `usize` have no counterpart; a `PgDatum` holds a `nuint` word that C# casts produce. RT PgDatumTests RawBitsAndNullIdentitySurviveParameterTransport keeps `nuint.MaxValue` through creation and SPI transport, and IT DatumConversionTests ScalarValuesPreservePostgresRepresentation round-trips the `bigint` minimum and maximum through the backend |

## pgrx-bench

### lib.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| find_new_report_dir_prefers_criterion_new_directory, materialize_baseline_artifacts_writes_criterion_base_layout, collect_artifacts_includes_change_estimates_when_present | Rust-specific | Mapped in the [tooling test mapping](pgrx-tooling-test-mapping.md) |
| criterion_summary_matches_expected_labels, criterion_p_value_with_rng_detects_large_regression, criterion_p_value_with_rng_is_high_for_identical_samples, parse_comparison_uses_persisted_baseline_artifacts | Covered | Mapped in the [tooling test mapping](pgrx-tooling-test-mapping.md) |

## cargo-pgrx

### cargo.rs and metadata.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| cdylib_rustc_args_carry_no_gc_sections_workaround, cdylib_rustc_args_skip_non_gnuish_targets, cargo_rustc_args_are_not_cargo_config | Rust-specific | pgrx passes `-Wl,--no-gc-sections` to rustc on GNU-like targets so the linker keeps its schema entries, without overriding Cargo configuration. Ankus emits its schema as a used, exported array in its own section, and a library linked with `--gc-sections` and stripped on Linux, `-dead_strip` on macOS or `/OPT:REF` on Windows keeps it: BT NativeSchemaEmitterTests LinkedLibraryRetainsEmbeddedSchema; a Native AOT publication keeps it: IT ToolCommandSchemaTests PublishedLibraryRetainsExecutableSchemaWithoutSidecars |
| cargo_flags_are_forwarded_and_whitespace_split | Different | Repeated `--property` assignments reach every build step, but each is one literal value that is never split (`reference/cli.md`, "Pass MSBuild properties"): IT ToolCommandPropertyTests ProjectGlobalPropertiesReachNativePublicationAndQueries passes ten assignments, including paths with spaces, through `get`, `build`, `publish`, `schema`, `install` and `package`; IT ToolCommandPropertyFlowTests ProjectGlobalPropertyQueriesPreserveLiteralValues keeps separators and quotes |
| metadata.rs: split_cargo_flags_handles_both_forms | Different | Each `--property` is one literal assignment, so a value containing spaces stays one value and pgrx's documented limitation for such paths does not apply (`reference/cli.md`, "Pass MSBuild properties"): IT ToolCommandPropertyTests ProjectGlobalPropertiesReachNativePublicationAndQueries; IT ToolCommandPropertyFlowTests ProjectGlobalPropertyQueriesPreserveLiteralValues |

### object_utils.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| reads_schema_section_from_minimal_macho64 | Covered | PT NativeSchemaSectionTests ReadsEachPublishedFormat: independently built thin Mach-O images for `osx-x64` and `osx-arm64`, and ELF and PE images, return the exact payload bytes |
| reads_sentinel_schema_section_from_minimal_macho64 | Rust-specific | pgrx's module magic writes a sentinel entry so every library has a schema section. Every Ankus publication embeds one section with a length-prefixed header, and the reader requires exactly one: PT NativeSchemaSectionTests ReadsEachPublishedFormat, RejectsMissingAndDuplicateSections |
| parses_managed_postmasters | Covered | The reader parses real images linked by each platform's toolchain (BT NativeSchemaEmitterTests LinkedLibraryRetainsEmbeddedSchema) and by Native AOT (IT ToolCommandSchemaTests PublishedLibraryRetainsExecutableSchemaWithoutSidecars), and rejects the .NET runtime's own native library, which Ankus did not build: PT NativeSchemaSectionTests ForeignLibrariesHaveNoSchemaSection. The reader accepts only shared libraries, so a postmaster executable is outside its contract |
| returns_none_for_valid_binary_without_schema_section, parses_universal_binary_slice, slice_unknown_architecture_returns_none | Covered | Mapped in the [tooling test mapping](pgrx-tooling-test-mapping.md) |

### command/regress.rs and command/run.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| parse_args_treats_single_non_version_as_test_filter | Covered | A positional argument is a case-sensitive test-name filter, and the server comes from the project when `--pg` is absent: IT ToolCommandRegressionTests RegressDryRunAndSelectionAreReadOnly |
| parse_args_accepts_pg_version_then_test_filter | Covered | `--pg` selects the server and the positional filter selects tests: IT ToolCommandRegressionTests RegressBuildsAndPreservesSetupLifecycle, RegressBootstrapsSetupAndPreservesErrorVerbosity |
| parse_args_rejects_test_filter_then_pg_version, parse_args_does_not_treat_unsupported_pg_label_as_version | Different | Ankus never reads a version from a positional argument: `--pg` selects the server and the one positional argument is always a filter (`reference/cli.md`, "Run SQL regression suites"), so the order error cannot occur and `pg99` is a filter. No test passes a version-like filter |
| run.rs: regress_cargo_flags_are_parsed_and_forwarded_to_run | Covered | `regress` builds the extension itself with repeated `--property` assignments, which select the configuration's publication, the extension name and the `<extension>_regress` database; the source fails to compile unless the forwarded constants arrive: IT ToolCommandPropertyFlowTests ProjectGlobalPropertiesReachRunConnectAndRegression; malformed values fail: IT ToolCommandPropertyTests ProjectCommandsRejectMalformedGlobalProperties |

### command/init.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| download_error_summary_keeps_retry_warning_single_line | Covered | A failed PostgreSQL download is tried four times in all, after 1, 2 and 4 seconds, with a one-line warning before each retry: PT PostgresDistributionClientTests TransientDownloadFailuresAreRetriedWithBackoff, DownloadErrorSummaryKeepsOneLine |

### command/schema.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| schema_parses_repeatable_cargo_flags | Covered | `schema` takes repeated `--property` and `-p` assignments, the last value of a repeated name wins, and its SQL matches the publication built with them: IT ToolCommandPropertyTests ProjectGlobalPropertiesReachNativePublicationAndQueries; malformed values fail: IT ToolCommandPropertyTests ProjectCommandsRejectMalformedGlobalProperties |
| test_missing_schema_section_errors | Covered | Mapped in the [tooling test mapping](pgrx-tooling-test-mapping.md) |
| empty_args_yield_no_version_and_no_items | Covered | Without items, `schema` prints the complete installation script: IT ToolCommandSchemaTests PublishedLibraryRetainsExecutableSchemaWithoutSidecars, SchemaBuildsBeforeEmittingSql |
| version_alone_is_captured | Covered | `--pg` alone selects the major and prints the complete script: IT ToolCommandSchemaTests SchemaReadsExistingProjectPublication, SchemaBuildsBeforeEmittingSql; unsupported majors fail: IT ToolCommandSchemaTests SchemaRejectsInvalidProjectSelection |
| items_only_without_version | Covered | IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery selects nine items, including operator-class names and a signature, without a version |
| version_followed_by_items | Covered | Selecting items from a project build chosen with `--pg` prints the same SQL as selecting them `--from` the published library: IT ToolCommandSchemaSelectionTests SchemaSelectionPreservesCatalogOwnershipAndRecovery |
| first_arg_that_looks_like_version_but_isnt_is_an_item | Different | Every positional argument is an item, because `--pg` selects the version (`getting-started/publishing.md`, "Select declarations"); a name that matches nothing fails as an unknown item: IT ToolCommandSchemaSelectionTests InvalidSchemaSelectionPreservesOutputs |

### command/install.rs and command/test.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| auto_detected_package_builds_against_resolved_manifest | Covered | From a solution directory, `install` builds only the solution's one extension project, with the solution's shared build settings: IT ToolCommandProjectResolutionTests InstallFromSolutionDirectorySelectsTheExtensionProject; a solution file or directory passed with `--project` selects the same project: IT ToolCommandProjectResolutionTests ExtensionIdentitySelectsEvaluatedSolutionProject |
| explicit_package_keeps_user_manifest_targeting | Rust-specific | Cargo selects a workspace member with `--package` against the workspace manifest. Ankus selects a project file with `--project`, and a solution with several extensions fails before building until one is chosen: IT ToolCommandProjectResolutionTests ExtensionCommandsRejectAmbiguousSolutions |
| test_cli_accepts_multiple_testnames, resolve_test_args_treats_non_version_first_arg_as_testname | Different | `ankus test` takes no positional test names; runner arguments after `--`, including filters, select tests (`reference/cli.md`, "Run extension tests"): IT ToolCommandTestRunnerTests TestCommandRunsSelectedBackendTests selects exactly three tests with one `--filter` holding two alternatives |
| resolve_test_args_keeps_pg_selector | Covered | `--pg` or `--all` selects the servers: IT ToolCommandTestRunnerTests TestCommandRunsSelectedBackendTests; unsupported majors fail before tests start: IT ToolCommandTestRunnerTests TestCommandRejectsInvalidSelection |
| test_filters_are_passed_to_libtest_after_separator | Covered | Arguments after `--` reach `dotnet test` unchanged, and the report holds exactly the selected tests: IT ToolCommandTestRunnerTests TestCommandRunsSelectedBackendTests |
| test_command_targets_resolved_manifest_for_outer_and_inner_builds | Covered | The test host build and the fixture's publication use the same resolved project and PostgreSQL selection from a solution directory, a `.slnx`, a `.sln` or the test project: IT PostgresTestSelectionTests CommandSelectionAcceptsOrdinaryProjectLayouts; forwarded properties reach both builds: IT ToolCommandTestRunnerTests TestCommandRunsSelectedBackendTests |

### command/upgrade.rs

| pgrx case | Status | Ankus evidence |
| --- | --- | --- |
| find_package_manifest, find_package_manifest_in_workspace, process_package_manifest, process_workspace_manifest, process_workspace_package_manifest | Covered | Mapped in the [tooling test mapping](pgrx-tooling-test-mapping.md) |
| parse_to_accepts_bare_version | Covered | `--to 0.2.0`: IT ToolCommandFrameworkUpgradeTests UpgradePreservesUnrelatedVersionsAndFormatting, UpgradeSelectsProjectAndImportedVersions |
| parse_to_defaults_to_none | Covered | Without `--to`, discovery selects the latest stable version, or a prerelease with `--include-prereleases`: IT ToolCommandFrameworkUpgradeTests UpgradeSelectsNuGetVersions |
| parse_to_accepts_range | Different | `--to` takes NuGet range notation, so pgrx's `>=0.16, <0.17` is written `[0.16, 0.17)` (`reference/cli.md`, "Upgrade Ankus references"): IT ToolCommandFrameworkUpgradeTests UpgradeResolvesSdkRangeToConcreteVersion keeps `[0.2.0, 0.3.0)` on the package reference and resolves the SDK to 0.2.0 |
| parse_to_accepts_partial_version, parse_to_accepts_caret, parse_to_accepts_tilde | Different | `--to` takes a NuGet version or range (`reference/cli.md`, "Upgrade Ankus references"). Cargo's `^` and `~` operators are not NuGet syntax and are rejected: IT ToolCommandFrameworkUpgradeTests UpgradeInvalidRequestsPreserveAllFiles (`caret`, `tilde`). NuGet reads `0.2` as a minimum version where Cargo reads a caret requirement, and `--to 0.2` writes it as given: UpgradeWritesExactPinsAndPrereleases |
| parse_to_accepts_exact_pin, parse_to_accepts_prerelease | Covered | `--to` writes an exact pin `[0.2.0]` and a prerelease `0.3.0-beta.1` as given: IT ToolCommandFrameworkUpgradeTests UpgradeWritesExactPinsAndPrereleases; automatic updates keep an existing exact pin and select prereleases with `--include-prereleases`: UpgradeSelectsNuGetVersions |
| parse_to_rejects_garbage, parse_to_rejects_empty | Covered | IT ToolCommandFrameworkUpgradeTests UpgradeInvalidRequestsPreserveAllFiles: `--to not-a-version` and `--to ""` fail with an error naming the rejected value and leave every file unchanged |

## Remaining gaps and partials

None. Rows marked Different or Rust-specific explain why the pgrx case has no
identical Ankus counterpart.
