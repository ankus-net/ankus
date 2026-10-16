// Regenerate from the pinned PostgreSQL catalogs with eng/Ankus.SqlStates.cs.
namespace Ankus;

/// <summary>
/// Provides named PostgreSQL SQLSTATE strings for error reporting, diagnostics and exception filters.
/// </summary>
/// <remarks>
/// Includes the union of the PostgreSQL 13 through 18 and 19 RC1 catalogs, including aliases and retired names.
/// A constant does not imply that its associated server feature exists in every PostgreSQL version.
/// These names supplement the string-based diagnostic APIs; extension-specific SQLSTATE strings remain supported.
/// </remarks>
public static class PgSqlStates
{
    /// <summary>
    /// The PostgreSQL <c>active_sql_transaction</c> SQLSTATE (<c>25001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_ACTIVE_SQL_TRANSACTION</c>.
    /// </remarks>
    public const string ActiveSqlTransaction = "25001";

    /// <summary>
    /// The PostgreSQL <c>admin_shutdown</c> SQLSTATE (<c>57P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_ADMIN_SHUTDOWN</c>.
    /// </remarks>
    public const string AdminShutdown = "57P01";

    /// <summary>
    /// The PostgreSQL <c>ambiguous_alias</c> SQLSTATE (<c>42P09</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_AMBIGUOUS_ALIAS</c>.
    /// </remarks>
    public const string AmbiguousAlias = "42P09";

    /// <summary>
    /// The PostgreSQL <c>ambiguous_column</c> SQLSTATE (<c>42702</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_AMBIGUOUS_COLUMN</c>.
    /// </remarks>
    public const string AmbiguousColumn = "42702";

    /// <summary>
    /// The PostgreSQL <c>ambiguous_function</c> SQLSTATE (<c>42725</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_AMBIGUOUS_FUNCTION</c>.
    /// </remarks>
    public const string AmbiguousFunction = "42725";

    /// <summary>
    /// The PostgreSQL <c>ambiguous_parameter</c> SQLSTATE (<c>42P08</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_AMBIGUOUS_PARAMETER</c>.
    /// </remarks>
    public const string AmbiguousParameter = "42P08";

    /// <summary>
    /// The PostgreSQL <c>array_element_error</c> SQLSTATE (<c>2202E</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_ARRAY_ELEMENT_ERROR</c>.
    /// </remarks>
    public const string ArrayElementError = "2202E";

    /// <summary>
    /// The PostgreSQL <c>array_subscript_error</c> SQLSTATE (<c>2202E</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_ARRAY_SUBSCRIPT_ERROR</c>.
    /// </remarks>
    public const string ArraySubscriptError = "2202E";

    /// <summary>
    /// The PostgreSQL <c>assert_failure</c> SQLSTATE (<c>P0004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_ASSERT_FAILURE</c>.
    /// </remarks>
    public const string AssertFailure = "P0004";

    /// <summary>
    /// The PostgreSQL <c>bad_copy_file_format</c> SQLSTATE (<c>22P04</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_BAD_COPY_FILE_FORMAT</c>.
    /// </remarks>
    public const string BadCopyFileFormat = "22P04";

    /// <summary>
    /// The PostgreSQL <c>branch_transaction_already_active</c> SQLSTATE (<c>25002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_BRANCH_TRANSACTION_ALREADY_ACTIVE</c>.
    /// </remarks>
    public const string BranchTransactionAlreadyActive = "25002";

    /// <summary>
    /// The PostgreSQL <c>cannot_coerce</c> SQLSTATE (<c>42846</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CANNOT_COERCE</c>.
    /// </remarks>
    public const string CannotCoerce = "42846";

    /// <summary>
    /// The PostgreSQL <c>cannot_connect_now</c> SQLSTATE (<c>57P03</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CANNOT_CONNECT_NOW</c>.
    /// </remarks>
    public const string CannotConnectNow = "57P03";

    /// <summary>
    /// The PostgreSQL <c>cant_change_runtime_param</c> SQLSTATE (<c>55P02</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CANT_CHANGE_RUNTIME_PARAM</c>.
    /// </remarks>
    public const string CantChangeRuntimeParam = "55P02";

    /// <summary>
    /// The PostgreSQL <c>cardinality_violation</c> SQLSTATE (<c>21000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CARDINALITY_VIOLATION</c>.
    /// </remarks>
    public const string CardinalityViolation = "21000";

    /// <summary>
    /// The PostgreSQL <c>case_not_found</c> SQLSTATE (<c>20000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CASE_NOT_FOUND</c>.
    /// </remarks>
    public const string CaseNotFound = "20000";

    /// <summary>
    /// The PostgreSQL <c>character_not_in_repertoire</c> SQLSTATE (<c>22021</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CHARACTER_NOT_IN_REPERTOIRE</c>.
    /// </remarks>
    public const string CharacterNotInRepertoire = "22021";

    /// <summary>
    /// The PostgreSQL <c>check_violation</c> SQLSTATE (<c>23514</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CHECK_VIOLATION</c>.
    /// </remarks>
    public const string CheckViolation = "23514";

    /// <summary>
    /// The PostgreSQL <c>collation_mismatch</c> SQLSTATE (<c>42P21</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_COLLATION_MISMATCH</c>.
    /// </remarks>
    public const string CollationMismatch = "42P21";

    /// <summary>
    /// The PostgreSQL <c>configuration_limit_exceeded</c> SQLSTATE (<c>53400</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CONFIGURATION_LIMIT_EXCEEDED</c>.
    /// </remarks>
    public const string ConfigurationLimitExceeded = "53400";

    /// <summary>
    /// The PostgreSQL <c>config_file_error</c> SQLSTATE (<c>F0000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CONFIG_FILE_ERROR</c>.
    /// </remarks>
    public const string ConfigFileError = "F0000";

    /// <summary>
    /// The PostgreSQL <c>connection_does_not_exist</c> SQLSTATE (<c>08003</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CONNECTION_DOES_NOT_EXIST</c>.
    /// </remarks>
    public const string ConnectionDoesNotExist = "08003";

    /// <summary>
    /// The PostgreSQL <c>connection_exception</c> SQLSTATE (<c>08000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CONNECTION_EXCEPTION</c>.
    /// </remarks>
    public const string ConnectionException = "08000";

    /// <summary>
    /// The PostgreSQL <c>connection_failure</c> SQLSTATE (<c>08006</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CONNECTION_FAILURE</c>.
    /// </remarks>
    public const string ConnectionFailure = "08006";

    /// <summary>
    /// The PostgreSQL <c>crash_shutdown</c> SQLSTATE (<c>57P02</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_CRASH_SHUTDOWN</c>.
    /// </remarks>
    public const string CrashShutdown = "57P02";

    /// <summary>
    /// The PostgreSQL <c>database_dropped</c> SQLSTATE (<c>57P04</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DATABASE_DROPPED</c>.
    /// </remarks>
    public const string DatabaseDropped = "57P04";

    /// <summary>
    /// The PostgreSQL <c>datatype_mismatch</c> SQLSTATE (<c>42804</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DATATYPE_MISMATCH</c>.
    /// </remarks>
    public const string DatatypeMismatch = "42804";

    /// <summary>
    /// The PostgreSQL <c>data_corrupted</c> SQLSTATE (<c>XX001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DATA_CORRUPTED</c>.
    /// </remarks>
    public const string DataCorrupted = "XX001";

    /// <summary>
    /// The PostgreSQL <c>data_exception</c> SQLSTATE (<c>22000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DATA_EXCEPTION</c>.
    /// </remarks>
    public const string DataException = "22000";

    /// <summary>
    /// The PostgreSQL <c>datetime_field_overflow</c> SQLSTATE (<c>22008</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DATETIME_FIELD_OVERFLOW</c>.
    /// </remarks>
    public const string DatetimeFieldOverflow = "22008";

    /// <summary>
    /// The PostgreSQL <c>datetime_value_out_of_range</c> SQLSTATE (<c>22008</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DATETIME_VALUE_OUT_OF_RANGE</c>.
    /// </remarks>
    public const string DatetimeValueOutOfRange = "22008";

    /// <summary>
    /// The PostgreSQL <c>dependent_objects_still_exist</c> SQLSTATE (<c>2BP01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DEPENDENT_OBJECTS_STILL_EXIST</c>.
    /// </remarks>
    public const string DependentObjectsStillExist = "2BP01";

    /// <summary>
    /// The PostgreSQL <c>dependent_privilege_descriptors_still_exist</c> SQLSTATE (<c>2B000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DEPENDENT_PRIVILEGE_DESCRIPTORS_STILL_EXIST</c>.
    /// </remarks>
    public const string DependentPrivilegeDescriptorsStillExist = "2B000";

    /// <summary>
    /// The PostgreSQL <c>diagnostics_exception</c> SQLSTATE (<c>0Z000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DIAGNOSTICS_EXCEPTION</c>.
    /// </remarks>
    public const string DiagnosticsException = "0Z000";

    /// <summary>
    /// The PostgreSQL <c>disk_full</c> SQLSTATE (<c>53100</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DISK_FULL</c>.
    /// </remarks>
    public const string DiskFull = "53100";

    /// <summary>
    /// The PostgreSQL <c>division_by_zero</c> SQLSTATE (<c>22012</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DIVISION_BY_ZERO</c>.
    /// </remarks>
    public const string DivisionByZero = "22012";

    /// <summary>
    /// The PostgreSQL <c>duplicate_alias</c> SQLSTATE (<c>42712</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_ALIAS</c>.
    /// </remarks>
    public const string DuplicateAlias = "42712";

    /// <summary>
    /// The PostgreSQL <c>duplicate_column</c> SQLSTATE (<c>42701</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_COLUMN</c>.
    /// </remarks>
    public const string DuplicateColumn = "42701";

    /// <summary>
    /// The PostgreSQL <c>duplicate_cursor</c> SQLSTATE (<c>42P03</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_CURSOR</c>.
    /// </remarks>
    public const string DuplicateCursor = "42P03";

    /// <summary>
    /// The PostgreSQL <c>duplicate_database</c> SQLSTATE (<c>42P04</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_DATABASE</c>.
    /// </remarks>
    public const string DuplicateDatabase = "42P04";

    /// <summary>
    /// The PostgreSQL <c>duplicate_file</c> SQLSTATE (<c>58P02</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_FILE</c>.
    /// </remarks>
    public const string DuplicateFile = "58P02";

    /// <summary>
    /// The PostgreSQL <c>duplicate_function</c> SQLSTATE (<c>42723</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_FUNCTION</c>.
    /// </remarks>
    public const string DuplicateFunction = "42723";

    /// <summary>
    /// The PostgreSQL <c>duplicate_json_object_key_value</c> SQLSTATE (<c>22030</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_JSON_OBJECT_KEY_VALUE</c>.
    /// </remarks>
    public const string DuplicateJsonObjectKeyValue = "22030";

    /// <summary>
    /// The PostgreSQL <c>duplicate_object</c> SQLSTATE (<c>42710</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_OBJECT</c>.
    /// </remarks>
    public const string DuplicateObject = "42710";

    /// <summary>
    /// The PostgreSQL <c>duplicate_prepared_statement</c> SQLSTATE (<c>42P05</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_PSTATEMENT</c>.
    /// </remarks>
    public const string DuplicatePreparedStatement = "42P05";

    /// <summary>
    /// The PostgreSQL <c>duplicate_schema</c> SQLSTATE (<c>42P06</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_SCHEMA</c>.
    /// </remarks>
    public const string DuplicateSchema = "42P06";

    /// <summary>
    /// The PostgreSQL <c>duplicate_table</c> SQLSTATE (<c>42P07</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_DUPLICATE_TABLE</c>.
    /// </remarks>
    public const string DuplicateTable = "42P07";

    /// <summary>
    /// The PostgreSQL <c>error_in_assignment</c> SQLSTATE (<c>22005</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_ERROR_IN_ASSIGNMENT</c>.
    /// </remarks>
    public const string ErrorInAssignment = "22005";

    /// <summary>
    /// The PostgreSQL <c>escape_character_conflict</c> SQLSTATE (<c>2200B</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_ESCAPE_CHARACTER_CONFLICT</c>.
    /// </remarks>
    public const string EscapeCharacterConflict = "2200B";

    /// <summary>
    /// The PostgreSQL <c>exclusion_violation</c> SQLSTATE (<c>23P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_EXCLUSION_VIOLATION</c>.
    /// </remarks>
    public const string ExclusionViolation = "23P01";

    /// <summary>
    /// The PostgreSQL <c>external_routine_exception</c> SQLSTATE (<c>38000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_EXTERNAL_ROUTINE_EXCEPTION</c>.
    /// </remarks>
    public const string ExternalRoutineException = "38000";

    /// <summary>
    /// The PostgreSQL <c>external_routine_invocation_exception</c> SQLSTATE (<c>39000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_EXTERNAL_ROUTINE_INVOCATION_EXCEPTION</c>.
    /// </remarks>
    public const string ExternalRoutineInvocationException = "39000";

    /// <summary>
    /// The PostgreSQL <c>containing_sql_not_permitted</c> SQLSTATE (<c>38001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_E_CONTAINING_SQL_NOT_PERMITTED</c>.
    /// </remarks>
    public const string ExternalRoutineContainingSqlNotPermitted = "38001";

    /// <summary>
    /// The PostgreSQL <c>modifying_sql_data_not_permitted</c> SQLSTATE (<c>38002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_E_MODIFYING_SQL_DATA_NOT_PERMITTED</c>.
    /// </remarks>
    public const string ExternalRoutineModifyingSqlDataNotPermitted = "38002";

    /// <summary>
    /// The PostgreSQL <c>prohibited_sql_statement_attempted</c> SQLSTATE (<c>38003</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_E_PROHIBITED_SQL_STATEMENT_ATTEMPTED</c>.
    /// </remarks>
    public const string ExternalRoutineProhibitedSqlStatementAttempted = "38003";

    /// <summary>
    /// The PostgreSQL <c>reading_sql_data_not_permitted</c> SQLSTATE (<c>38004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_E_READING_SQL_DATA_NOT_PERMITTED</c>.
    /// </remarks>
    public const string ExternalRoutineReadingSqlDataNotPermitted = "38004";

    /// <summary>
    /// The PostgreSQL <c>event_trigger_protocol_violated</c> SQLSTATE (<c>39P03</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_I_E_EVENT_TRIGGER_PROTOCOL_VIOLATED</c>.
    /// </remarks>
    public const string ExternalRoutineInvocationEventTriggerProtocolViolated = "39P03";

    /// <summary>
    /// The PostgreSQL <c>invalid_sqlstate_returned</c> SQLSTATE (<c>39001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_I_E_INVALID_SQLSTATE_RETURNED</c>.
    /// </remarks>
    public const string ExternalRoutineInvocationInvalidSqlStateReturned = "39001";

    /// <summary>
    /// The PostgreSQL <c>null_value_not_allowed</c> SQLSTATE (<c>39004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_I_E_NULL_VALUE_NOT_ALLOWED</c>.
    /// </remarks>
    public const string ExternalRoutineInvocationNullValueNotAllowed = "39004";

    /// <summary>
    /// The PostgreSQL <c>srf_protocol_violated</c> SQLSTATE (<c>39P02</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_I_E_SRF_PROTOCOL_VIOLATED</c>.
    /// </remarks>
    public const string ExternalRoutineInvocationSrfProtocolViolated = "39P02";

    /// <summary>
    /// The PostgreSQL <c>trigger_protocol_violated</c> SQLSTATE (<c>39P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_E_R_I_E_TRIGGER_PROTOCOL_VIOLATED</c>.
    /// </remarks>
    public const string ExternalRoutineInvocationTriggerProtocolViolated = "39P01";

    /// <summary>
    /// The PostgreSQL <c>fdw_column_name_not_found</c> SQLSTATE (<c>HV005</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_COLUMN_NAME_NOT_FOUND</c>.
    /// </remarks>
    public const string FdwColumnNameNotFound = "HV005";

    /// <summary>
    /// The PostgreSQL <c>fdw_dynamic_parameter_value_needed</c> SQLSTATE (<c>HV002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_DYNAMIC_PARAMETER_VALUE_NEEDED</c>.
    /// </remarks>
    public const string FdwDynamicParameterValueNeeded = "HV002";

    /// <summary>
    /// The PostgreSQL <c>fdw_error</c> SQLSTATE (<c>HV000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_ERROR</c>.
    /// </remarks>
    public const string FdwError = "HV000";

    /// <summary>
    /// The PostgreSQL <c>fdw_function_sequence_error</c> SQLSTATE (<c>HV010</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_FUNCTION_SEQUENCE_ERROR</c>.
    /// </remarks>
    public const string FdwFunctionSequenceError = "HV010";

    /// <summary>
    /// The PostgreSQL <c>fdw_inconsistent_descriptor_information</c> SQLSTATE (<c>HV021</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INCONSISTENT_DESCRIPTOR_INFORMATION</c>.
    /// </remarks>
    public const string FdwInconsistentDescriptorInformation = "HV021";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_attribute_value</c> SQLSTATE (<c>HV024</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_ATTRIBUTE_VALUE</c>.
    /// </remarks>
    public const string FdwInvalidAttributeValue = "HV024";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_column_name</c> SQLSTATE (<c>HV007</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_COLUMN_NAME</c>.
    /// </remarks>
    public const string FdwInvalidColumnName = "HV007";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_column_number</c> SQLSTATE (<c>HV008</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_COLUMN_NUMBER</c>.
    /// </remarks>
    public const string FdwInvalidColumnNumber = "HV008";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_data_type</c> SQLSTATE (<c>HV004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_DATA_TYPE</c>.
    /// </remarks>
    public const string FdwInvalidDataType = "HV004";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_data_type_descriptors</c> SQLSTATE (<c>HV006</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_DATA_TYPE_DESCRIPTORS</c>.
    /// </remarks>
    public const string FdwInvalidDataTypeDescriptors = "HV006";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_descriptor_field_identifier</c> SQLSTATE (<c>HV091</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_DESCRIPTOR_FIELD_IDENTIFIER</c>.
    /// </remarks>
    public const string FdwInvalidDescriptorFieldIdentifier = "HV091";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_handle</c> SQLSTATE (<c>HV00B</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_HANDLE</c>.
    /// </remarks>
    public const string FdwInvalidHandle = "HV00B";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_option_index</c> SQLSTATE (<c>HV00C</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_OPTION_INDEX</c>.
    /// </remarks>
    public const string FdwInvalidOptionIndex = "HV00C";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_option_name</c> SQLSTATE (<c>HV00D</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_OPTION_NAME</c>.
    /// </remarks>
    public const string FdwInvalidOptionName = "HV00D";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_string_format</c> SQLSTATE (<c>HV00A</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_STRING_FORMAT</c>.
    /// </remarks>
    public const string FdwInvalidStringFormat = "HV00A";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_string_length_or_buffer_length</c> SQLSTATE (<c>HV090</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_STRING_LENGTH_OR_BUFFER_LENGTH</c>.
    /// </remarks>
    public const string FdwInvalidStringLengthOrBufferLength = "HV090";

    /// <summary>
    /// The PostgreSQL <c>fdw_invalid_use_of_null_pointer</c> SQLSTATE (<c>HV009</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_INVALID_USE_OF_NULL_POINTER</c>.
    /// </remarks>
    public const string FdwInvalidUseOfNullPointer = "HV009";

    /// <summary>
    /// The PostgreSQL <c>fdw_no_schemas</c> SQLSTATE (<c>HV00P</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_NO_SCHEMAS</c>.
    /// </remarks>
    public const string FdwNoSchemas = "HV00P";

    /// <summary>
    /// The PostgreSQL <c>fdw_option_name_not_found</c> SQLSTATE (<c>HV00J</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_OPTION_NAME_NOT_FOUND</c>.
    /// </remarks>
    public const string FdwOptionNameNotFound = "HV00J";

    /// <summary>
    /// The PostgreSQL <c>fdw_out_of_memory</c> SQLSTATE (<c>HV001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_OUT_OF_MEMORY</c>.
    /// </remarks>
    public const string FdwOutOfMemory = "HV001";

    /// <summary>
    /// The PostgreSQL <c>fdw_reply_handle</c> SQLSTATE (<c>HV00K</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_REPLY_HANDLE</c>.
    /// </remarks>
    public const string FdwReplyHandle = "HV00K";

    /// <summary>
    /// The PostgreSQL <c>fdw_schema_not_found</c> SQLSTATE (<c>HV00Q</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_SCHEMA_NOT_FOUND</c>.
    /// </remarks>
    public const string FdwSchemaNotFound = "HV00Q";

    /// <summary>
    /// The PostgreSQL <c>fdw_table_not_found</c> SQLSTATE (<c>HV00R</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_TABLE_NOT_FOUND</c>.
    /// </remarks>
    public const string FdwTableNotFound = "HV00R";

    /// <summary>
    /// The PostgreSQL <c>fdw_too_many_handles</c> SQLSTATE (<c>HV014</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_TOO_MANY_HANDLES</c>.
    /// </remarks>
    public const string FdwTooManyHandles = "HV014";

    /// <summary>
    /// The PostgreSQL <c>fdw_unable_to_create_execution</c> SQLSTATE (<c>HV00L</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_UNABLE_TO_CREATE_EXECUTION</c>.
    /// </remarks>
    public const string FdwUnableToCreateExecution = "HV00L";

    /// <summary>
    /// The PostgreSQL <c>fdw_unable_to_create_reply</c> SQLSTATE (<c>HV00M</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_UNABLE_TO_CREATE_REPLY</c>.
    /// </remarks>
    public const string FdwUnableToCreateReply = "HV00M";

    /// <summary>
    /// The PostgreSQL <c>fdw_unable_to_establish_connection</c> SQLSTATE (<c>HV00N</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FDW_UNABLE_TO_ESTABLISH_CONNECTION</c>.
    /// </remarks>
    public const string FdwUnableToEstablishConnection = "HV00N";

    /// <summary>
    /// The PostgreSQL <c>feature_not_supported</c> SQLSTATE (<c>0A000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FEATURE_NOT_SUPPORTED</c>.
    /// </remarks>
    public const string FeatureNotSupported = "0A000";

    /// <summary>
    /// The PostgreSQL <c>file_name_too_long</c> SQLSTATE (<c>58P03</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FILE_NAME_TOO_LONG</c>. Present in the PostgreSQL 18, 19 RC1 source catalogs.
    /// </remarks>
    public const string FileNameTooLong = "58P03";

    /// <summary>
    /// The PostgreSQL <c>floating_point_exception</c> SQLSTATE (<c>22P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FLOATING_POINT_EXCEPTION</c>.
    /// </remarks>
    public const string FloatingPointException = "22P01";

    /// <summary>
    /// The PostgreSQL <c>foreign_key_violation</c> SQLSTATE (<c>23503</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_FOREIGN_KEY_VIOLATION</c>.
    /// </remarks>
    public const string ForeignKeyViolation = "23503";

    /// <summary>
    /// The PostgreSQL <c>generated_always</c> SQLSTATE (<c>428C9</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_GENERATED_ALWAYS</c>.
    /// </remarks>
    public const string GeneratedAlways = "428C9";

    /// <summary>
    /// The PostgreSQL <c>grouping_error</c> SQLSTATE (<c>42803</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_GROUPING_ERROR</c>.
    /// </remarks>
    public const string GroupingError = "42803";

    /// <summary>
    /// The PostgreSQL <c>held_cursor_requires_same_isolation_level</c> SQLSTATE (<c>25008</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_HELD_CURSOR_REQUIRES_SAME_ISOLATION_LEVEL</c>.
    /// </remarks>
    public const string HeldCursorRequiresSameIsolationLevel = "25008";

    /// <summary>
    /// The PostgreSQL <c>idle_in_transaction_session_timeout</c> SQLSTATE (<c>25P03</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_IDLE_IN_TRANSACTION_SESSION_TIMEOUT</c>.
    /// </remarks>
    public const string IdleInTransactionSessionTimeout = "25P03";

    /// <summary>
    /// The PostgreSQL <c>idle_session_timeout</c> SQLSTATE (<c>57P05</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_IDLE_SESSION_TIMEOUT</c>. Present in the PostgreSQL 14, 15, 16, 17, 18, 19 RC1 source catalogs.
    /// </remarks>
    public const string IdleSessionTimeout = "57P05";

    /// <summary>
    /// The PostgreSQL <c>inappropriate_access_mode_for_branch_transaction</c> SQLSTATE (<c>25003</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INAPPROPRIATE_ACCESS_MODE_FOR_BRANCH_TRANSACTION</c>.
    /// </remarks>
    public const string InappropriateAccessModeForBranchTransaction = "25003";

    /// <summary>
    /// The PostgreSQL <c>inappropriate_isolation_level_for_branch_transaction</c> SQLSTATE (<c>25004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INAPPROPRIATE_ISOLATION_LEVEL_FOR_BRANCH_TRANSACTION</c>.
    /// </remarks>
    public const string InappropriateIsolationLevelForBranchTransaction = "25004";

    /// <summary>
    /// The PostgreSQL <c>indeterminate_collation</c> SQLSTATE (<c>42P22</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INDETERMINATE_COLLATION</c>.
    /// </remarks>
    public const string IndeterminateCollation = "42P22";

    /// <summary>
    /// The PostgreSQL <c>indeterminate_datatype</c> SQLSTATE (<c>42P18</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INDETERMINATE_DATATYPE</c>.
    /// </remarks>
    public const string IndeterminateDatatype = "42P18";

    /// <summary>
    /// The PostgreSQL <c>index_corrupted</c> SQLSTATE (<c>XX002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INDEX_CORRUPTED</c>.
    /// </remarks>
    public const string IndexCorrupted = "XX002";

    /// <summary>
    /// The PostgreSQL <c>indicator_overflow</c> SQLSTATE (<c>22022</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INDICATOR_OVERFLOW</c>.
    /// </remarks>
    public const string IndicatorOverflow = "22022";

    /// <summary>
    /// The PostgreSQL <c>insufficient_privilege</c> SQLSTATE (<c>42501</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INSUFFICIENT_PRIVILEGE</c>.
    /// </remarks>
    public const string InsufficientPrivilege = "42501";

    /// <summary>
    /// The PostgreSQL <c>insufficient_resources</c> SQLSTATE (<c>53000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INSUFFICIENT_RESOURCES</c>.
    /// </remarks>
    public const string InsufficientResources = "53000";

    /// <summary>
    /// The PostgreSQL <c>integrity_constraint_violation</c> SQLSTATE (<c>23000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INTEGRITY_CONSTRAINT_VIOLATION</c>.
    /// </remarks>
    public const string IntegrityConstraintViolation = "23000";

    /// <summary>
    /// The PostgreSQL <c>internal_error</c> SQLSTATE (<c>XX000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INTERNAL_ERROR</c>.
    /// </remarks>
    public const string InternalError = "XX000";

    /// <summary>
    /// The PostgreSQL <c>interval_field_overflow</c> SQLSTATE (<c>22015</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INTERVAL_FIELD_OVERFLOW</c>.
    /// </remarks>
    public const string IntervalFieldOverflow = "22015";

    /// <summary>
    /// The PostgreSQL <c>invalid_argument_for_logarithm</c> SQLSTATE (<c>2201E</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ARGUMENT_FOR_LOG</c>.
    /// </remarks>
    public const string InvalidArgumentForLogarithm = "2201E";

    /// <summary>
    /// The PostgreSQL <c>invalid_argument_for_nth_value_function</c> SQLSTATE (<c>22016</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ARGUMENT_FOR_NTH_VALUE</c>.
    /// </remarks>
    public const string InvalidArgumentForNthValueFunction = "22016";

    /// <summary>
    /// The PostgreSQL <c>invalid_argument_for_ntile_function</c> SQLSTATE (<c>22014</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ARGUMENT_FOR_NTILE</c>.
    /// </remarks>
    public const string InvalidArgumentForNtileFunction = "22014";

    /// <summary>
    /// The PostgreSQL <c>invalid_argument_for_power_function</c> SQLSTATE (<c>2201F</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ARGUMENT_FOR_POWER_FUNCTION</c>.
    /// </remarks>
    public const string InvalidArgumentForPowerFunction = "2201F";

    /// <summary>
    /// The PostgreSQL <c>invalid_argument_for_sql_json_datetime_function</c> SQLSTATE (<c>22031</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ARGUMENT_FOR_SQL_JSON_DATETIME_FUNCTION</c>.
    /// </remarks>
    public const string InvalidArgumentForSqlJsonDatetimeFunction = "22031";

    /// <summary>
    /// The PostgreSQL <c>invalid_argument_for_width_bucket_function</c> SQLSTATE (<c>2201G</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ARGUMENT_FOR_WIDTH_BUCKET_FUNCTION</c>.
    /// </remarks>
    public const string InvalidArgumentForWidthBucketFunction = "2201G";

    /// <summary>
    /// The PostgreSQL <c>invalid_argument_for_xquery</c> SQLSTATE (<c>10608</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ARGUMENT_FOR_XQUERY</c>. Present in the PostgreSQL 18, 19 RC1 source catalogs.
    /// </remarks>
    public const string InvalidArgumentForXQuery = "10608";

    /// <summary>
    /// The PostgreSQL <c>invalid_authorization_specification</c> SQLSTATE (<c>28000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_AUTHORIZATION_SPECIFICATION</c>.
    /// </remarks>
    public const string InvalidAuthorizationSpecification = "28000";

    /// <summary>
    /// The PostgreSQL <c>invalid_binary_representation</c> SQLSTATE (<c>22P03</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_BINARY_REPRESENTATION</c>.
    /// </remarks>
    public const string InvalidBinaryRepresentation = "22P03";

    /// <summary>
    /// The PostgreSQL <c>invalid_catalog_name</c> SQLSTATE (<c>3D000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_CATALOG_NAME</c>.
    /// </remarks>
    public const string InvalidCatalogName = "3D000";

    /// <summary>
    /// The PostgreSQL <c>invalid_character_value_for_cast</c> SQLSTATE (<c>22018</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_CHARACTER_VALUE_FOR_CAST</c>.
    /// </remarks>
    public const string InvalidCharacterValueForCast = "22018";

    /// <summary>
    /// The PostgreSQL <c>invalid_column_definition</c> SQLSTATE (<c>42611</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_COLUMN_DEFINITION</c>.
    /// </remarks>
    public const string InvalidColumnDefinition = "42611";

    /// <summary>
    /// The PostgreSQL <c>invalid_column_reference</c> SQLSTATE (<c>42P10</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_COLUMN_REFERENCE</c>.
    /// </remarks>
    public const string InvalidColumnReference = "42P10";

    /// <summary>
    /// The PostgreSQL <c>invalid_cursor_definition</c> SQLSTATE (<c>42P11</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_CURSOR_DEFINITION</c>.
    /// </remarks>
    public const string InvalidCursorDefinition = "42P11";

    /// <summary>
    /// The PostgreSQL <c>invalid_cursor_name</c> SQLSTATE (<c>34000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_CURSOR_NAME</c>.
    /// </remarks>
    public const string InvalidCursorName = "34000";

    /// <summary>
    /// The PostgreSQL <c>invalid_cursor_state</c> SQLSTATE (<c>24000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_CURSOR_STATE</c>.
    /// </remarks>
    public const string InvalidCursorState = "24000";

    /// <summary>
    /// The PostgreSQL <c>invalid_database_definition</c> SQLSTATE (<c>42P12</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_DATABASE_DEFINITION</c>.
    /// </remarks>
    public const string InvalidDatabaseDefinition = "42P12";

    /// <summary>
    /// The PostgreSQL <c>invalid_datetime_format</c> SQLSTATE (<c>22007</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_DATETIME_FORMAT</c>.
    /// </remarks>
    public const string InvalidDatetimeFormat = "22007";

    /// <summary>
    /// The PostgreSQL <c>invalid_escape_character</c> SQLSTATE (<c>22019</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ESCAPE_CHARACTER</c>.
    /// </remarks>
    public const string InvalidEscapeCharacter = "22019";

    /// <summary>
    /// The PostgreSQL <c>invalid_escape_octet</c> SQLSTATE (<c>2200D</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ESCAPE_OCTET</c>.
    /// </remarks>
    public const string InvalidEscapeOctet = "2200D";

    /// <summary>
    /// The PostgreSQL <c>invalid_escape_sequence</c> SQLSTATE (<c>22025</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ESCAPE_SEQUENCE</c>.
    /// </remarks>
    public const string InvalidEscapeSequence = "22025";

    /// <summary>
    /// The PostgreSQL <c>invalid_foreign_key</c> SQLSTATE (<c>42830</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_FOREIGN_KEY</c>.
    /// </remarks>
    public const string InvalidForeignKey = "42830";

    /// <summary>
    /// The PostgreSQL <c>invalid_function_definition</c> SQLSTATE (<c>42P13</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_FUNCTION_DEFINITION</c>.
    /// </remarks>
    public const string InvalidFunctionDefinition = "42P13";

    /// <summary>
    /// The PostgreSQL <c>invalid_grantor</c> SQLSTATE (<c>0L000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_GRANTOR</c>.
    /// </remarks>
    public const string InvalidGrantor = "0L000";

    /// <summary>
    /// The PostgreSQL <c>invalid_grant_operation</c> SQLSTATE (<c>0LP01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_GRANT_OPERATION</c>.
    /// </remarks>
    public const string InvalidGrantOperation = "0LP01";

    /// <summary>
    /// The PostgreSQL <c>invalid_indicator_parameter_value</c> SQLSTATE (<c>22010</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_INDICATOR_PARAMETER_VALUE</c>.
    /// </remarks>
    public const string InvalidIndicatorParameterValue = "22010";

    /// <summary>
    /// The PostgreSQL <c>invalid_json_text</c> SQLSTATE (<c>22032</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_JSON_TEXT</c>.
    /// </remarks>
    public const string InvalidJsonText = "22032";

    /// <summary>
    /// The PostgreSQL <c>invalid_name</c> SQLSTATE (<c>42602</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_NAME</c>.
    /// </remarks>
    public const string InvalidName = "42602";

    /// <summary>
    /// The PostgreSQL <c>invalid_object_definition</c> SQLSTATE (<c>42P17</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_OBJECT_DEFINITION</c>.
    /// </remarks>
    public const string InvalidObjectDefinition = "42P17";

    /// <summary>
    /// The PostgreSQL <c>invalid_parameter_value</c> SQLSTATE (<c>22023</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_PARAMETER_VALUE</c>.
    /// </remarks>
    public const string InvalidParameterValue = "22023";

    /// <summary>
    /// The PostgreSQL <c>invalid_password</c> SQLSTATE (<c>28P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_PASSWORD</c>.
    /// </remarks>
    public const string InvalidPassword = "28P01";

    /// <summary>
    /// The PostgreSQL <c>invalid_preceding_or_following_size</c> SQLSTATE (<c>22013</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_PRECEDING_OR_FOLLOWING_SIZE</c>.
    /// </remarks>
    public const string InvalidPrecedingOrFollowingSize = "22013";

    /// <summary>
    /// The PostgreSQL <c>invalid_prepared_statement_definition</c> SQLSTATE (<c>42P14</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_PSTATEMENT_DEFINITION</c>.
    /// </remarks>
    public const string InvalidPreparedStatementDefinition = "42P14";

    /// <summary>
    /// The PostgreSQL <c>invalid_recursion</c> SQLSTATE (<c>42P19</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_RECURSION</c>.
    /// </remarks>
    public const string InvalidRecursion = "42P19";

    /// <summary>
    /// The PostgreSQL <c>invalid_regular_expression</c> SQLSTATE (<c>2201B</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_REGULAR_EXPRESSION</c>.
    /// </remarks>
    public const string InvalidRegularExpression = "2201B";

    /// <summary>
    /// The PostgreSQL <c>invalid_role_specification</c> SQLSTATE (<c>0P000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ROLE_SPECIFICATION</c>.
    /// </remarks>
    public const string InvalidRoleSpecification = "0P000";

    /// <summary>
    /// The PostgreSQL <c>invalid_row_count_in_limit_clause</c> SQLSTATE (<c>2201W</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ROW_COUNT_IN_LIMIT_CLAUSE</c>.
    /// </remarks>
    public const string InvalidRowCountInLimitClause = "2201W";

    /// <summary>
    /// The PostgreSQL <c>invalid_row_count_in_result_offset_clause</c> SQLSTATE (<c>2201X</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_ROW_COUNT_IN_RESULT_OFFSET_CLAUSE</c>.
    /// </remarks>
    public const string InvalidRowCountInResultOffsetClause = "2201X";

    /// <summary>
    /// The PostgreSQL <c>invalid_schema_definition</c> SQLSTATE (<c>42P15</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_SCHEMA_DEFINITION</c>.
    /// </remarks>
    public const string InvalidSchemaDefinition = "42P15";

    /// <summary>
    /// The PostgreSQL <c>invalid_schema_name</c> SQLSTATE (<c>3F000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_SCHEMA_NAME</c>.
    /// </remarks>
    public const string InvalidSchemaName = "3F000";

    /// <summary>
    /// The PostgreSQL <c>invalid_sql_json_subscript</c> SQLSTATE (<c>22033</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_SQL_JSON_SUBSCRIPT</c>.
    /// </remarks>
    public const string InvalidSqlJsonSubscript = "22033";

    /// <summary>
    /// The PostgreSQL <c>invalid_sql_statement_name</c> SQLSTATE (<c>26000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_SQL_STATEMENT_NAME</c>.
    /// </remarks>
    public const string InvalidSqlStatementName = "26000";

    /// <summary>
    /// The PostgreSQL <c>invalid_tablesample_argument</c> SQLSTATE (<c>2202H</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_TABLESAMPLE_ARGUMENT</c>.
    /// </remarks>
    public const string InvalidTablesampleArgument = "2202H";

    /// <summary>
    /// The PostgreSQL <c>invalid_tablesample_repeat</c> SQLSTATE (<c>2202G</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_TABLESAMPLE_REPEAT</c>.
    /// </remarks>
    public const string InvalidTablesampleRepeat = "2202G";

    /// <summary>
    /// The PostgreSQL <c>invalid_table_definition</c> SQLSTATE (<c>42P16</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_TABLE_DEFINITION</c>.
    /// </remarks>
    public const string InvalidTableDefinition = "42P16";

    /// <summary>
    /// The PostgreSQL <c>invalid_text_representation</c> SQLSTATE (<c>22P02</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_TEXT_REPRESENTATION</c>.
    /// </remarks>
    public const string InvalidTextRepresentation = "22P02";

    /// <summary>
    /// The PostgreSQL <c>invalid_time_zone_displacement_value</c> SQLSTATE (<c>22009</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_TIME_ZONE_DISPLACEMENT_VALUE</c>.
    /// </remarks>
    public const string InvalidTimeZoneDisplacementValue = "22009";

    /// <summary>
    /// The PostgreSQL <c>invalid_transaction_initiation</c> SQLSTATE (<c>0B000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_TRANSACTION_INITIATION</c>.
    /// </remarks>
    public const string InvalidTransactionInitiation = "0B000";

    /// <summary>
    /// The PostgreSQL <c>invalid_transaction_state</c> SQLSTATE (<c>25000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_TRANSACTION_STATE</c>.
    /// </remarks>
    public const string InvalidTransactionState = "25000";

    /// <summary>
    /// The PostgreSQL <c>invalid_transaction_termination</c> SQLSTATE (<c>2D000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_TRANSACTION_TERMINATION</c>.
    /// </remarks>
    public const string InvalidTransactionTermination = "2D000";

    /// <summary>
    /// The PostgreSQL <c>invalid_use_of_escape_character</c> SQLSTATE (<c>2200C</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_USE_OF_ESCAPE_CHARACTER</c>.
    /// </remarks>
    public const string InvalidUseOfEscapeCharacter = "2200C";

    /// <summary>
    /// The PostgreSQL <c>invalid_xml_comment</c> SQLSTATE (<c>2200S</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_XML_COMMENT</c>.
    /// </remarks>
    public const string InvalidXmlComment = "2200S";

    /// <summary>
    /// The PostgreSQL <c>invalid_xml_content</c> SQLSTATE (<c>2200N</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_XML_CONTENT</c>.
    /// </remarks>
    public const string InvalidXmlContent = "2200N";

    /// <summary>
    /// The PostgreSQL <c>invalid_xml_document</c> SQLSTATE (<c>2200M</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_XML_DOCUMENT</c>.
    /// </remarks>
    public const string InvalidXmlDocument = "2200M";

    /// <summary>
    /// The PostgreSQL <c>invalid_xml_processing_instruction</c> SQLSTATE (<c>2200T</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_INVALID_XML_PROCESSING_INSTRUCTION</c>.
    /// </remarks>
    public const string InvalidXmlProcessingInstruction = "2200T";

    /// <summary>
    /// The PostgreSQL <c>in_failed_sql_transaction</c> SQLSTATE (<c>25P02</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_IN_FAILED_SQL_TRANSACTION</c>.
    /// </remarks>
    public const string InFailedSqlTransaction = "25P02";

    /// <summary>
    /// The PostgreSQL <c>io_error</c> SQLSTATE (<c>58030</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_IO_ERROR</c>.
    /// </remarks>
    public const string IoError = "58030";

    /// <summary>
    /// The PostgreSQL <c>locator_exception</c> SQLSTATE (<c>0F000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_LOCATOR_EXCEPTION</c>.
    /// </remarks>
    public const string LocatorException = "0F000";

    /// <summary>
    /// The PostgreSQL <c>lock_file_exists</c> SQLSTATE (<c>F0001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_LOCK_FILE_EXISTS</c>.
    /// </remarks>
    public const string LockFileExists = "F0001";

    /// <summary>
    /// The PostgreSQL <c>lock_not_available</c> SQLSTATE (<c>55P03</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_LOCK_NOT_AVAILABLE</c>.
    /// </remarks>
    public const string LockNotAvailable = "55P03";

    /// <summary>
    /// The PostgreSQL <c>invalid_locator_specification</c> SQLSTATE (<c>0F001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_L_E_INVALID_SPECIFICATION</c>.
    /// </remarks>
    public const string InvalidLocatorSpecification = "0F001";

    /// <summary>
    /// The PostgreSQL <c>more_than_one_sql_json_item</c> SQLSTATE (<c>22034</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_MORE_THAN_ONE_SQL_JSON_ITEM</c>.
    /// </remarks>
    public const string MoreThanOneSqlJsonItem = "22034";

    /// <summary>
    /// The PostgreSQL <c>most_specific_type_mismatch</c> SQLSTATE (<c>2200G</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_MOST_SPECIFIC_TYPE_MISMATCH</c>.
    /// </remarks>
    public const string MostSpecificTypeMismatch = "2200G";

    /// <summary>
    /// The PostgreSQL <c>name_too_long</c> SQLSTATE (<c>42622</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NAME_TOO_LONG</c>.
    /// </remarks>
    public const string NameTooLong = "42622";

    /// <summary>
    /// The PostgreSQL <c>nonstandard_use_of_escape_character</c> SQLSTATE (<c>22P06</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NONSTANDARD_USE_OF_ESCAPE_CHARACTER</c>.
    /// </remarks>
    public const string NonstandardUseOfEscapeCharacter = "22P06";

    /// <summary>
    /// The PostgreSQL <c>non_numeric_sql_json_item</c> SQLSTATE (<c>22036</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NON_NUMERIC_SQL_JSON_ITEM</c>.
    /// </remarks>
    public const string NonNumericSqlJsonItem = "22036";

    /// <summary>
    /// The PostgreSQL <c>non_unique_keys_in_a_json_object</c> SQLSTATE (<c>22037</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NON_UNIQUE_KEYS_IN_A_JSON_OBJECT</c>.
    /// </remarks>
    public const string NonUniqueKeysInAJsonObject = "22037";

    /// <summary>
    /// The PostgreSQL <c>not_an_xml_document</c> SQLSTATE (<c>2200L</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NOT_AN_XML_DOCUMENT</c>.
    /// </remarks>
    public const string NotAnXmlDocument = "2200L";

    /// <summary>
    /// The PostgreSQL <c>not_null_violation</c> SQLSTATE (<c>23502</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NOT_NULL_VIOLATION</c>.
    /// </remarks>
    public const string NotNullViolation = "23502";

    /// <summary>
    /// The PostgreSQL <c>no_active_sql_transaction</c> SQLSTATE (<c>25P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NO_ACTIVE_SQL_TRANSACTION</c>.
    /// </remarks>
    public const string NoActiveSqlTransaction = "25P01";

    /// <summary>
    /// The PostgreSQL <c>no_active_sql_transaction_for_branch_transaction</c> SQLSTATE (<c>25005</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NO_ACTIVE_SQL_TRANSACTION_FOR_BRANCH_TRANSACTION</c>.
    /// </remarks>
    public const string NoActiveSqlTransactionForBranchTransaction = "25005";

    /// <summary>
    /// The PostgreSQL <c>no_additional_dynamic_result_sets_returned</c> SQLSTATE (<c>02001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NO_ADDITIONAL_DYNAMIC_RESULT_SETS_RETURNED</c>.
    /// </remarks>
    public const string NoAdditionalDynamicResultSetsReturned = "02001";

    /// <summary>
    /// The PostgreSQL <c>no_data</c> SQLSTATE (<c>02000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NO_DATA</c>.
    /// </remarks>
    public const string NoData = "02000";

    /// <summary>
    /// The PostgreSQL <c>no_data_found</c> SQLSTATE (<c>P0002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NO_DATA_FOUND</c>.
    /// </remarks>
    public const string NoDataFound = "P0002";

    /// <summary>
    /// The PostgreSQL <c>no_sql_json_item</c> SQLSTATE (<c>22035</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NO_SQL_JSON_ITEM</c>.
    /// </remarks>
    public const string NoSqlJsonItem = "22035";

    /// <summary>
    /// The PostgreSQL <c>null_value_not_allowed</c> SQLSTATE (<c>22004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NULL_VALUE_NOT_ALLOWED</c>.
    /// </remarks>
    public const string NullValueNotAllowed = "22004";

    /// <summary>
    /// The PostgreSQL <c>null_value_no_indicator_parameter</c> SQLSTATE (<c>22002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NULL_VALUE_NO_INDICATOR_PARAMETER</c>.
    /// </remarks>
    public const string NullValueNoIndicatorParameter = "22002";

    /// <summary>
    /// The PostgreSQL <c>numeric_value_out_of_range</c> SQLSTATE (<c>22003</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE</c>.
    /// </remarks>
    public const string NumericValueOutOfRange = "22003";

    /// <summary>
    /// The PostgreSQL <c>object_in_use</c> SQLSTATE (<c>55006</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_OBJECT_IN_USE</c>.
    /// </remarks>
    public const string ObjectInUse = "55006";

    /// <summary>
    /// The PostgreSQL <c>object_not_in_prerequisite_state</c> SQLSTATE (<c>55000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE</c>.
    /// </remarks>
    public const string ObjectNotInPrerequisiteState = "55000";

    /// <summary>
    /// The PostgreSQL <c>operator_intervention</c> SQLSTATE (<c>57000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_OPERATOR_INTERVENTION</c>.
    /// </remarks>
    public const string OperatorIntervention = "57000";

    /// <summary>
    /// The PostgreSQL <c>out_of_memory</c> SQLSTATE (<c>53200</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_OUT_OF_MEMORY</c>.
    /// </remarks>
    public const string OutOfMemory = "53200";

    /// <summary>
    /// The PostgreSQL <c>plpgsql_error</c> SQLSTATE (<c>P0000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_PLPGSQL_ERROR</c>.
    /// </remarks>
    public const string PlpgsqlError = "P0000";

    /// <summary>
    /// The PostgreSQL <c>program_limit_exceeded</c> SQLSTATE (<c>54000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_PROGRAM_LIMIT_EXCEEDED</c>.
    /// </remarks>
    public const string ProgramLimitExceeded = "54000";

    /// <summary>
    /// The PostgreSQL <c>protocol_violation</c> SQLSTATE (<c>08P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_PROTOCOL_VIOLATION</c>.
    /// </remarks>
    public const string ProtocolViolation = "08P01";

    /// <summary>
    /// The PostgreSQL <c>query_canceled</c> SQLSTATE (<c>57014</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_QUERY_CANCELED</c>.
    /// </remarks>
    public const string QueryCanceled = "57014";

    /// <summary>
    /// The PostgreSQL <c>raise_exception</c> SQLSTATE (<c>P0001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_RAISE_EXCEPTION</c>.
    /// </remarks>
    public const string RaiseException = "P0001";

    /// <summary>
    /// The PostgreSQL <c>read_only_sql_transaction</c> SQLSTATE (<c>25006</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_READ_ONLY_SQL_TRANSACTION</c>.
    /// </remarks>
    public const string ReadOnlySqlTransaction = "25006";

    /// <summary>
    /// The PostgreSQL <c>reserved_name</c> SQLSTATE (<c>42939</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_RESERVED_NAME</c>.
    /// </remarks>
    public const string ReservedName = "42939";

    /// <summary>
    /// The PostgreSQL <c>restrict_violation</c> SQLSTATE (<c>23001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_RESTRICT_VIOLATION</c>.
    /// </remarks>
    public const string RestrictViolation = "23001";

    /// <summary>
    /// The PostgreSQL <c>savepoint_exception</c> SQLSTATE (<c>3B000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SAVEPOINT_EXCEPTION</c>.
    /// </remarks>
    public const string SavepointException = "3B000";

    /// <summary>
    /// The PostgreSQL <c>schema_and_data_statement_mixing_not_supported</c> SQLSTATE (<c>25007</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SCHEMA_AND_DATA_STATEMENT_MIXING_NOT_SUPPORTED</c>.
    /// </remarks>
    public const string SchemaAndDataStatementMixingNotSupported = "25007";

    /// <summary>
    /// The PostgreSQL <c>sequence_generator_limit_exceeded</c> SQLSTATE (<c>2200H</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SEQUENCE_GENERATOR_LIMIT_EXCEEDED</c>.
    /// </remarks>
    public const string SequenceGeneratorLimitExceeded = "2200H";

    /// <summary>
    /// The PostgreSQL <c>singleton_sql_json_item_required</c> SQLSTATE (<c>22038</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SINGLETON_SQL_JSON_ITEM_REQUIRED</c>.
    /// </remarks>
    public const string SingletonSqlJsonItemRequired = "22038";

    /// <summary>
    /// The PostgreSQL <c>snapshot_too_old</c> SQLSTATE (<c>72000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SNAPSHOT_TOO_OLD</c>. Present in the PostgreSQL 13, 14, 15, 16 source catalogs.
    /// </remarks>
    public const string SnapshotTooOld = "72000";

    /// <summary>
    /// The PostgreSQL <c>sqlclient_unable_to_establish_sqlconnection</c> SQLSTATE (<c>08001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQLCLIENT_UNABLE_TO_ESTABLISH_SQLCONNECTION</c>.
    /// </remarks>
    public const string SqlClientUnableToEstablishSqlConnection = "08001";

    /// <summary>
    /// The PostgreSQL <c>sqlserver_rejected_establishment_of_sqlconnection</c> SQLSTATE (<c>08004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQLSERVER_REJECTED_ESTABLISHMENT_OF_SQLCONNECTION</c>.
    /// </remarks>
    public const string SqlServerRejectedEstablishmentOfSqlConnection = "08004";

    /// <summary>
    /// The PostgreSQL <c>sql_json_array_not_found</c> SQLSTATE (<c>22039</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQL_JSON_ARRAY_NOT_FOUND</c>.
    /// </remarks>
    public const string SqlJsonArrayNotFound = "22039";

    /// <summary>
    /// The PostgreSQL <c>sql_json_item_cannot_be_cast_to_target_type</c> SQLSTATE (<c>2203G</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQL_JSON_ITEM_CANNOT_BE_CAST_TO_TARGET_TYPE</c>. Present in the PostgreSQL 15, 16, 17, 18, 19 RC1 source catalogs.
    /// </remarks>
    public const string SqlJsonItemCannotBeCastToTargetType = "2203G";

    /// <summary>
    /// The PostgreSQL <c>sql_json_member_not_found</c> SQLSTATE (<c>2203A</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQL_JSON_MEMBER_NOT_FOUND</c>.
    /// </remarks>
    public const string SqlJsonMemberNotFound = "2203A";

    /// <summary>
    /// The PostgreSQL <c>sql_json_number_not_found</c> SQLSTATE (<c>2203B</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQL_JSON_NUMBER_NOT_FOUND</c>.
    /// </remarks>
    public const string SqlJsonNumberNotFound = "2203B";

    /// <summary>
    /// The PostgreSQL <c>sql_json_object_not_found</c> SQLSTATE (<c>2203C</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQL_JSON_OBJECT_NOT_FOUND</c>.
    /// </remarks>
    public const string SqlJsonObjectNotFound = "2203C";

    /// <summary>
    /// The PostgreSQL <c>sql_json_scalar_required</c> SQLSTATE (<c>2203F</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQL_JSON_SCALAR_REQUIRED</c>.
    /// </remarks>
    public const string SqlJsonScalarRequired = "2203F";

    /// <summary>
    /// The PostgreSQL <c>sql_routine_exception</c> SQLSTATE (<c>2F000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQL_ROUTINE_EXCEPTION</c>.
    /// </remarks>
    public const string SqlRoutineException = "2F000";

    /// <summary>
    /// The PostgreSQL <c>sql_statement_not_yet_complete</c> SQLSTATE (<c>03000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SQL_STATEMENT_NOT_YET_COMPLETE</c>.
    /// </remarks>
    public const string SqlStatementNotYetComplete = "03000";

    /// <summary>
    /// The PostgreSQL <c>stacked_diagnostics_accessed_without_active_handler</c> SQLSTATE (<c>0Z002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_STACKED_DIAGNOSTICS_ACCESSED_WITHOUT_ACTIVE_HANDLER</c>.
    /// </remarks>
    public const string StackedDiagnosticsAccessedWithoutActiveHandler = "0Z002";

    /// <summary>
    /// The PostgreSQL <c>statement_too_complex</c> SQLSTATE (<c>54001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_STATEMENT_TOO_COMPLEX</c>.
    /// </remarks>
    public const string StatementTooComplex = "54001";

    /// <summary>
    /// The PostgreSQL <c>string_data_length_mismatch</c> SQLSTATE (<c>22026</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_STRING_DATA_LENGTH_MISMATCH</c>.
    /// </remarks>
    public const string StringDataLengthMismatch = "22026";

    /// <summary>
    /// The PostgreSQL <c>string_data_right_truncation</c> SQLSTATE (<c>22001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_STRING_DATA_RIGHT_TRUNCATION</c>.
    /// </remarks>
    public const string StringDataRightTruncation = "22001";

    /// <summary>
    /// The PostgreSQL <c>substring_error</c> SQLSTATE (<c>22011</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SUBSTRING_ERROR</c>.
    /// </remarks>
    public const string SubstringError = "22011";

    /// <summary>
    /// The PostgreSQL <c>successful_completion</c> SQLSTATE (<c>00000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SUCCESSFUL_COMPLETION</c>.
    /// </remarks>
    public const string SuccessfulCompletion = "00000";

    /// <summary>
    /// The PostgreSQL <c>syntax_error</c> SQLSTATE (<c>42601</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SYNTAX_ERROR</c>.
    /// </remarks>
    public const string SyntaxError = "42601";

    /// <summary>
    /// The PostgreSQL <c>syntax_error_or_access_rule_violation</c> SQLSTATE (<c>42000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SYNTAX_ERROR_OR_ACCESS_RULE_VIOLATION</c>.
    /// </remarks>
    public const string SyntaxErrorOrAccessRuleViolation = "42000";

    /// <summary>
    /// The PostgreSQL <c>system_error</c> SQLSTATE (<c>58000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_SYSTEM_ERROR</c>.
    /// </remarks>
    public const string SystemError = "58000";

    /// <summary>
    /// The PostgreSQL <c>invalid_savepoint_specification</c> SQLSTATE (<c>3B001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_S_E_INVALID_SPECIFICATION</c>.
    /// </remarks>
    public const string InvalidSavepointSpecification = "3B001";

    /// <summary>
    /// The PostgreSQL <c>function_executed_no_return_statement</c> SQLSTATE (<c>2F005</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_S_R_E_FUNCTION_EXECUTED_NO_RETURN_STATEMENT</c>.
    /// </remarks>
    public const string SqlRoutineFunctionExecutedNoReturnStatement = "2F005";

    /// <summary>
    /// The PostgreSQL <c>modifying_sql_data_not_permitted</c> SQLSTATE (<c>2F002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_S_R_E_MODIFYING_SQL_DATA_NOT_PERMITTED</c>.
    /// </remarks>
    public const string SqlRoutineModifyingSqlDataNotPermitted = "2F002";

    /// <summary>
    /// The PostgreSQL <c>prohibited_sql_statement_attempted</c> SQLSTATE (<c>2F003</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_S_R_E_PROHIBITED_SQL_STATEMENT_ATTEMPTED</c>.
    /// </remarks>
    public const string SqlRoutineProhibitedSqlStatementAttempted = "2F003";

    /// <summary>
    /// The PostgreSQL <c>reading_sql_data_not_permitted</c> SQLSTATE (<c>2F004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_S_R_E_READING_SQL_DATA_NOT_PERMITTED</c>.
    /// </remarks>
    public const string SqlRoutineReadingSqlDataNotPermitted = "2F004";

    /// <summary>
    /// The PostgreSQL <c>too_many_arguments</c> SQLSTATE (<c>54023</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TOO_MANY_ARGUMENTS</c>.
    /// </remarks>
    public const string TooManyArguments = "54023";

    /// <summary>
    /// The PostgreSQL <c>too_many_columns</c> SQLSTATE (<c>54011</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TOO_MANY_COLUMNS</c>.
    /// </remarks>
    public const string TooManyColumns = "54011";

    /// <summary>
    /// The PostgreSQL <c>too_many_connections</c> SQLSTATE (<c>53300</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TOO_MANY_CONNECTIONS</c>.
    /// </remarks>
    public const string TooManyConnections = "53300";

    /// <summary>
    /// The PostgreSQL <c>too_many_json_array_elements</c> SQLSTATE (<c>2203D</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TOO_MANY_JSON_ARRAY_ELEMENTS</c>.
    /// </remarks>
    public const string TooManyJsonArrayElements = "2203D";

    /// <summary>
    /// The PostgreSQL <c>too_many_json_object_members</c> SQLSTATE (<c>2203E</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TOO_MANY_JSON_OBJECT_MEMBERS</c>.
    /// </remarks>
    public const string TooManyJsonObjectMembers = "2203E";

    /// <summary>
    /// The PostgreSQL <c>too_many_rows</c> SQLSTATE (<c>P0003</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TOO_MANY_ROWS</c>.
    /// </remarks>
    public const string TooManyRows = "P0003";

    /// <summary>
    /// The PostgreSQL <c>transaction_resolution_unknown</c> SQLSTATE (<c>08007</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TRANSACTION_RESOLUTION_UNKNOWN</c>.
    /// </remarks>
    public const string TransactionResolutionUnknown = "08007";

    /// <summary>
    /// The PostgreSQL <c>transaction_rollback</c> SQLSTATE (<c>40000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TRANSACTION_ROLLBACK</c>.
    /// </remarks>
    public const string TransactionRollback = "40000";

    /// <summary>
    /// The PostgreSQL <c>transaction_timeout</c> SQLSTATE (<c>25P04</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TRANSACTION_TIMEOUT</c>. Present in the PostgreSQL 17, 18, 19 RC1 source catalogs.
    /// </remarks>
    public const string TransactionTimeout = "25P04";

    /// <summary>
    /// The PostgreSQL <c>triggered_action_exception</c> SQLSTATE (<c>09000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TRIGGERED_ACTION_EXCEPTION</c>.
    /// </remarks>
    public const string TriggeredActionException = "09000";

    /// <summary>
    /// The PostgreSQL <c>triggered_data_change_violation</c> SQLSTATE (<c>27000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TRIGGERED_DATA_CHANGE_VIOLATION</c>.
    /// </remarks>
    public const string TriggeredDataChangeViolation = "27000";

    /// <summary>
    /// The PostgreSQL <c>trim_error</c> SQLSTATE (<c>22027</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_TRIM_ERROR</c>.
    /// </remarks>
    public const string TrimError = "22027";

    /// <summary>
    /// The PostgreSQL <c>deadlock_detected</c> SQLSTATE (<c>40P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_T_R_DEADLOCK_DETECTED</c>.
    /// </remarks>
    public const string DeadlockDetected = "40P01";

    /// <summary>
    /// The PostgreSQL <c>transaction_integrity_constraint_violation</c> SQLSTATE (<c>40002</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_T_R_INTEGRITY_CONSTRAINT_VIOLATION</c>.
    /// </remarks>
    public const string TransactionIntegrityConstraintViolation = "40002";

    /// <summary>
    /// The PostgreSQL <c>serialization_failure</c> SQLSTATE (<c>40001</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_T_R_SERIALIZATION_FAILURE</c>.
    /// </remarks>
    public const string SerializationFailure = "40001";

    /// <summary>
    /// The PostgreSQL <c>statement_completion_unknown</c> SQLSTATE (<c>40003</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_T_R_STATEMENT_COMPLETION_UNKNOWN</c>.
    /// </remarks>
    public const string StatementCompletionUnknown = "40003";

    /// <summary>
    /// The PostgreSQL <c>undefined_column</c> SQLSTATE (<c>42703</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_COLUMN</c>.
    /// </remarks>
    public const string UndefinedColumn = "42703";

    /// <summary>
    /// The PostgreSQL <c>undefined_cursor</c> SQLSTATE (<c>34000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_CURSOR</c>.
    /// </remarks>
    public const string UndefinedCursor = "34000";

    /// <summary>
    /// The PostgreSQL <c>undefined_database</c> SQLSTATE (<c>3D000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_DATABASE</c>.
    /// </remarks>
    public const string UndefinedDatabase = "3D000";

    /// <summary>
    /// The PostgreSQL <c>undefined_file</c> SQLSTATE (<c>58P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_FILE</c>.
    /// </remarks>
    public const string UndefinedFile = "58P01";

    /// <summary>
    /// The PostgreSQL <c>undefined_function</c> SQLSTATE (<c>42883</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_FUNCTION</c>.
    /// </remarks>
    public const string UndefinedFunction = "42883";

    /// <summary>
    /// The PostgreSQL <c>undefined_object</c> SQLSTATE (<c>42704</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_OBJECT</c>.
    /// </remarks>
    public const string UndefinedObject = "42704";

    /// <summary>
    /// The PostgreSQL <c>undefined_parameter</c> SQLSTATE (<c>42P02</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_PARAMETER</c>.
    /// </remarks>
    public const string UndefinedParameter = "42P02";

    /// <summary>
    /// The PostgreSQL <c>undefined_pstatement</c> SQLSTATE (<c>26000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_PSTATEMENT</c>.
    /// </remarks>
    public const string UndefinedPreparedStatement = "26000";

    /// <summary>
    /// The PostgreSQL <c>undefined_schema</c> SQLSTATE (<c>3F000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_SCHEMA</c>.
    /// </remarks>
    public const string UndefinedSchema = "3F000";

    /// <summary>
    /// The PostgreSQL <c>undefined_table</c> SQLSTATE (<c>42P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNDEFINED_TABLE</c>.
    /// </remarks>
    public const string UndefinedTable = "42P01";

    /// <summary>
    /// The PostgreSQL <c>unique_violation</c> SQLSTATE (<c>23505</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNIQUE_VIOLATION</c>.
    /// </remarks>
    public const string UniqueViolation = "23505";

    /// <summary>
    /// The PostgreSQL <c>unsafe_new_enum_value_usage</c> SQLSTATE (<c>55P04</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNSAFE_NEW_ENUM_VALUE_USAGE</c>.
    /// </remarks>
    public const string UnsafeNewEnumValueUsage = "55P04";

    /// <summary>
    /// The PostgreSQL <c>unterminated_c_string</c> SQLSTATE (<c>22024</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNTERMINATED_C_STRING</c>.
    /// </remarks>
    public const string UnterminatedCString = "22024";

    /// <summary>
    /// The PostgreSQL <c>untranslatable_character</c> SQLSTATE (<c>22P05</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_UNTRANSLATABLE_CHARACTER</c>.
    /// </remarks>
    public const string UntranslatableCharacter = "22P05";

    /// <summary>
    /// The PostgreSQL <c>warning</c> SQLSTATE (<c>01000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WARNING</c>.
    /// </remarks>
    public const string Warning = "01000";

    /// <summary>
    /// The PostgreSQL <c>deprecated_feature</c> SQLSTATE (<c>01P01</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WARNING_DEPRECATED_FEATURE</c>.
    /// </remarks>
    public const string WarningDeprecatedFeature = "01P01";

    /// <summary>
    /// The PostgreSQL <c>dynamic_result_sets_returned</c> SQLSTATE (<c>0100C</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WARNING_DYNAMIC_RESULT_SETS_RETURNED</c>.
    /// </remarks>
    public const string WarningDynamicResultSetsReturned = "0100C";

    /// <summary>
    /// The PostgreSQL <c>implicit_zero_bit_padding</c> SQLSTATE (<c>01008</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WARNING_IMPLICIT_ZERO_BIT_PADDING</c>.
    /// </remarks>
    public const string WarningImplicitZeroBitPadding = "01008";

    /// <summary>
    /// The PostgreSQL <c>null_value_eliminated_in_set_function</c> SQLSTATE (<c>01003</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WARNING_NULL_VALUE_ELIMINATED_IN_SET_FUNCTION</c>.
    /// </remarks>
    public const string WarningNullValueEliminatedInSetFunction = "01003";

    /// <summary>
    /// The PostgreSQL <c>privilege_not_granted</c> SQLSTATE (<c>01007</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WARNING_PRIVILEGE_NOT_GRANTED</c>.
    /// </remarks>
    public const string WarningPrivilegeNotGranted = "01007";

    /// <summary>
    /// The PostgreSQL <c>privilege_not_revoked</c> SQLSTATE (<c>01006</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WARNING_PRIVILEGE_NOT_REVOKED</c>.
    /// </remarks>
    public const string WarningPrivilegeNotRevoked = "01006";

    /// <summary>
    /// The PostgreSQL <c>string_data_right_truncation</c> SQLSTATE (<c>01004</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WARNING_STRING_DATA_RIGHT_TRUNCATION</c>.
    /// </remarks>
    public const string WarningStringDataRightTruncation = "01004";

    /// <summary>
    /// The PostgreSQL <c>windowing_error</c> SQLSTATE (<c>42P20</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WINDOWING_ERROR</c>.
    /// </remarks>
    public const string WindowingError = "42P20";

    /// <summary>
    /// The PostgreSQL <c>with_check_option_violation</c> SQLSTATE (<c>44000</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WITH_CHECK_OPTION_VIOLATION</c>.
    /// </remarks>
    public const string WithCheckOptionViolation = "44000";

    /// <summary>
    /// The PostgreSQL <c>wrong_object_type</c> SQLSTATE (<c>42809</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_WRONG_OBJECT_TYPE</c>.
    /// </remarks>
    public const string WrongObjectType = "42809";

    /// <summary>
    /// The PostgreSQL <c>zero_length_character_string</c> SQLSTATE (<c>2200F</c>).
    /// </summary>
    /// <remarks>
    /// PostgreSQL macro <c>ERRCODE_ZERO_LENGTH_CHARACTER_STRING</c>.
    /// </remarks>
    public const string ZeroLengthCharacterString = "2200F";
}
