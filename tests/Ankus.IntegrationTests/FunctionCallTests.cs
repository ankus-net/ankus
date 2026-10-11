using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Compares direct function invocation with PostgreSQL's native lookup and execution behavior.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class FunctionCallTests(TestContext context)
{
    /// <summary>
    /// Direct native calls preserve zero versus NULL, copy referenced results, and recover from PostgreSQL errors.
    /// </summary>
    /// <param name="raw">Whether to use context-owned raw return values.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeEntryPointsPreserveDatumAndErrorContracts(bool raw)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        string flag = raw ? "true" : "false";
        string address = "tests.function_address('tests.native_nullable_sum(integer,integer)')";
        Assert.AreEqual(42, await ScalarAsync<int>(connection, $"SELECT datatype.call_native({address},40,2,{flag})", token));
        Assert.AreEqual(0, await ScalarAsync<int>(connection, $"SELECT datatype.call_native({address},0,0,{flag})", token));
        Assert.AreEqual(DBNull.Value, await ScalarAsync<object>(connection, $"SELECT datatype.call_native({address},NULL,2,{flag})", token));
        Assert.AreEqual(0, await ScalarAsync<int>(connection, $"SELECT datatype.call_native_many({address},0)", token));
        Assert.AreEqual(101, await ScalarAsync<int>(connection, $"SELECT datatype.call_native_many({address},101)", token));
        PostgresException error = await ErrorAsync(connection,
            $"SELECT datatype.call_native(tests.function_address('pg_catalog.int4div(integer,integer)'),1,0,{flag})", token);
        Assert.AreEqual(PostgresErrorCodes.DivisionByZero, error.SqlState);
        Assert.AreEqual(42, await ScalarAsync<int>(connection, $"SELECT datatype.call_native({address},40,2,{flag})", token));
        Assert.AreEqual(string.Concat(Enumerable.Repeat("owned", 10000)) + " 🐘", await ScalarAsync<string>(connection,
            "SELECT datatype.call_native_text(tests.function_address('pg_catalog.textcat(text,text)'),repeat('owned',10000),' 🐘')", token));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Checks EXECUTE before strict-NULL folding and preserves security-definer identity and local configuration.
    /// </summary>
    /// <param name="byOid">Whether to use direct catalog lookup.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PermissionsAndSecurityDefinerStateFollowPostgres(bool byOid)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        string role = "call_user_" + Guid.NewGuid().ToString("N");
        string schema = "call_access_" + Guid.NewGuid().ToString("N");
        string owner = await ScalarAsync<string>(connection, "SELECT current_user::text", token);
        string originalPath = await ScalarAsync<string>(connection, "SHOW search_path", token);
        await ExecuteAsync(connection, $"""
            CREATE ROLE {role};
            CREATE SCHEMA {schema};
            GRANT USAGE ON SCHEMA {schema},datatype TO {role};
            CREATE FUNCTION {schema}.denied(integer) RETURNS integer LANGUAGE sql STRICT IMMUTABLE AS 'SELECT $1+1';
            REVOKE EXECUTE ON FUNCTION {schema}.denied(integer) FROM PUBLIC;
            CREATE FUNCTION {schema}.identity(text) RETURNS text LANGUAGE sql SECURITY DEFINER
                SET search_path=pg_catalog AS 'SELECT current_user::text || ''|'' || current_setting(''search_path'')';
            SET LOCAL ROLE {role};
            """, token);
        string deniedOid = byOid ? $"'{schema}.denied(integer)'::regprocedure::oid" : "0::oid";
        await transaction.SaveAsync("denied_call", token);
        PostgresException denied = await ErrorAsync(connection, $"SELECT datatype.call_integer('{schema}.denied',{deniedOid},NULL,0)", token);
        Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await transaction.RollbackAsync("denied_call", token);
        string identityOid = byOid ? $"'{schema}.identity(text)'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(owner + "|pg_catalog", await ScalarAsync<string>(connection,
            $"SELECT datatype.call_text('{schema}.identity',{identityOid},'input',NULL)", token));
        Assert.AreEqual(role, await ScalarAsync<string>(connection, "SELECT current_user::text", token));
        Assert.AreEqual(originalPath, await ScalarAsync<string>(connection, "SHOW search_path", token));
    }

    /// <summary>
    /// Rejects wrong call kinds, missing functions/defaults, and excessive arity while keeping the backend usable.
    /// </summary>
    /// <param name="name">The target function name.</param>
    /// <param name="mode">The managed argument arrangement.</param>
    /// <param name="sqlState">The native diagnostic.</param>
    [TestMethod]
    [DataRow("pg_catalog.abs", 4, "22023")]
    [DataRow("pg_catalog.abs", 6, "54023")]
    [DataRow("pg_catalog.ankus_missing_function", 0, "42883")]
    [DataRow("pg_catalog.generate_series", 2, "42809")]
    [DataRow("pg_catalog.sum", 0, "42809")]
    [DataRow("", 1, "42602")]
    public async Task InvalidCallsFailBeforeExecutionAndRecover(string name, int mode, string sqlState)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        PostgresException error = await ErrorAsync(connection, $"SELECT datatype.call_integer('{name}',0,42,{mode})", token);
        Assert.AreEqual(sqlState, error.SqlState);
        Assert.AreEqual(42, await ScalarAsync<int>(connection, "SELECT datatype.call_integer('pg_catalog.abs',0,-42,0)", token));
    }

    /// <summary>
    /// Calls SQL, PL/pgSQL, and built-in functions with strict and nullable inputs.
    /// </summary>
    /// <param name="byOid">Whether to resolve the target by exact catalog identity.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FunctionLanguagesAndNullInputsMatchSql(bool byOid)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, """
            CREATE FUNCTION pg_temp.sql_number(integer) RETURNS integer LANGUAGE sql AS 'SELECT coalesce($1,41)+1';
            CREATE FUNCTION pg_temp.pl_number(integer) RETURNS integer LANGUAGE plpgsql AS 'BEGIN RETURN coalesce($1,41)+1; END';
            CREATE FUNCTION pg_temp.strict_number(integer) RETURNS integer LANGUAGE plpgsql STRICT AS 'BEGIN RAISE EXCEPTION ''must not run''; END';
            """, token);
        foreach (string function in new[] { "sql_number", "pl_number" })
        {
            string oid = byOid ? $"'pg_temp.{function}(integer)'::regprocedure::oid" : "0::oid";
            Assert.AreEqual(42, await ScalarAsync<int>(connection, $"SELECT datatype.call_integer('pg_temp.{function}',{oid},41,0)", token));
            Assert.AreEqual(42, await ScalarAsync<int>(connection, $"SELECT datatype.call_integer('pg_temp.{function}',{oid},NULL,0)", token));
        }

        string strictOid = byOid ? "'pg_temp.strict_number(integer)'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(DBNull.Value, await ScalarAsync<object>(connection, $"SELECT datatype.call_integer('pg_temp.strict_number',{strictOid},NULL,0)", token));
        string absOid = byOid ? "'pg_catalog.abs(integer)'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(42, await ScalarAsync<int>(connection, $"SELECT datatype.call_integer('pg_catalog.abs',{absOid},-42,0)", token));
        Assert.AreEqual("True|False|True", await ScalarAsync<string>(connection, "SELECT datatype.call_boolean_values()", token));
    }

    /// <summary>
    /// Typed defaults select the intended overload; missing arguments and unknown catalog identities stay errors.
    /// </summary>
    [TestMethod]
    public async Task TypedDefaultsResolveOverloadsWithoutGuessing()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, """
            CREATE FUNCTION pg_temp.typed_default(integer DEFAULT 11) RETURNS integer LANGUAGE sql AS 'SELECT $1';
            CREATE FUNCTION pg_temp.typed_default(bigint DEFAULT 22) RETURNS integer LANGUAGE sql AS 'SELECT $1::integer';
            """, token);
        Assert.AreEqual(11, await ScalarAsync<int>(connection, "SELECT datatype.call_integer('pg_temp.typed_default',0,NULL,4)", token));
        Assert.AreEqual(22, await ScalarAsync<int>(connection, "SELECT datatype.call_integer('pg_temp.typed_default',0,NULL,5)", token));
        PostgresException ambiguous = await ErrorAsync(connection, "SELECT datatype.call_integer('pg_temp.typed_default',0,NULL,1)", token);
        Assert.AreEqual(PostgresErrorCodes.AmbiguousFunction, ambiguous.SqlState);
        PostgresException missing = await ErrorAsync(connection, "SELECT datatype.call_integer('pg_catalog.abs',0,NULL,1)", token);
        Assert.AreEqual(PostgresErrorCodes.UndefinedFunction, missing.SqlState);
        PostgresException oid = await ErrorAsync(connection, "SELECT datatype.call_integer('',4294967295::oid,NULL,1)", token);
        Assert.AreEqual(PostgresErrorCodes.UndefinedFunction, oid.SqlState);
    }

    /// <summary>
    /// Evaluates omitted and explicit defaults in order, including volatile and NULL defaults.
    /// </summary>
    /// <param name="byOid">Whether to invoke by OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DefaultExpressionsRemainTypedAndExecuteOnce(bool byOid)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, """
            CREATE TEMP SEQUENCE default_calls;
            CREATE FUNCTION pg_temp.defaults(integer DEFAULT nextval('pg_temp.default_calls')::integer, integer DEFAULT 10)
                RETURNS integer LANGUAGE sql AS 'SELECT $1*100+$2';
            CREATE FUNCTION pg_temp.null_default(integer DEFAULT NULL) RETURNS integer LANGUAGE sql AS 'SELECT $1';
            """, token);
        string oid = byOid ? "'pg_temp.defaults(integer,integer)'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(110, await ScalarAsync<int>(connection, $"SELECT datatype.call_integer('pg_temp.defaults',{oid},NULL,1)", token));
        Assert.AreEqual(242, await ScalarAsync<int>(connection, $"SELECT datatype.call_integer('pg_temp.defaults',{oid},42,3)", token));
        Assert.AreEqual(710, await ScalarAsync<int>(connection, $"SELECT datatype.call_integer('pg_temp.defaults',{oid},7,2)", token));
        Assert.AreEqual(2L, await ScalarAsync<long>(connection, "SELECT last_value FROM pg_temp.default_calls", token));
        string nullOid = byOid ? "'pg_temp.null_default(integer)'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(DBNull.Value, await ScalarAsync<object>(connection, $"SELECT datatype.call_integer('pg_temp.null_default',{nullOid},NULL,4)", token));
    }

    /// <summary>
    /// Variable-length geometry arguments and results cross direct calls intact.
    /// </summary>
    [TestMethod]
    public async Task GeometryArgumentsAndResultsCrossDirectCalls()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        Assert.AreEqual("[(1,2),(3,4),(5,0)]|False|((1,2),(3,4),(5,0))|(5,4),(1,0)",
            await ScalarAsync<string>(connection, "SELECT datatype.call_geometry()", token));
    }

    /// <summary>
    /// Omitting only trailing defaulted arguments fills them, as pgrx's unspecified-default calls do; a STRICT function
    /// whose omitted argument defaults to NULL returns NULL without running; omitting a required argument fails.
    /// </summary>
    /// <param name="byOid">Whether to resolve by exact catalog identity, which fills defaults separately.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OmittedTrailingDefaultsAreFilledAndRequiredArgumentsAreNot(bool byOid)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, """
            CREATE FUNCTION pg_temp.trailing(integer, integer DEFAULT 10) RETURNS integer LANGUAGE sql AS 'SELECT $1*100+$2';
            CREATE FUNCTION pg_temp.strict_null(integer DEFAULT NULL) RETURNS integer LANGUAGE plpgsql STRICT
                AS 'BEGIN RAISE EXCEPTION ''must not run''; END';
            """, token);
        string oid = byOid ? "'pg_temp.trailing(integer,integer)'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(710, await ScalarAsync<int>(connection, $"SELECT datatype.call_integer('pg_temp.trailing',{oid},7,0)", token));
        string strictOid = byOid ? "'pg_temp.strict_null(integer)'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(DBNull.Value, await ScalarAsync<object>(connection,
            $"SELECT datatype.call_integer('pg_temp.strict_null',{strictOid},NULL,1)", token));
        PostgresException missing = await ErrorAsync(connection, $"SELECT datatype.call_integer('pg_temp.trailing',{oid},NULL,1)", token);
        Assert.AreEqual(byOid ? "22023" : "42883", missing.SqlState, missing.MessageText);
        Assert.AreEqual(42, await ScalarAsync<int>(connection, "SELECT datatype.call_integer('pg_catalog.abs',0,-42,0)", token));
    }

    /// <summary>
    /// Preserves quoted names, overload selection, and the active search path without treating names as SQL.
    /// </summary>
    [TestMethod]
    public async Task IdentifierAndOverloadResolutionUsePostgresRules()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        await ExecuteAsync(connection, """
            CREATE SCHEMA call_lookup;
            CREATE FUNCTION call_lookup."Odd.Name"(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1+1';
            CREATE FUNCTION call_lookup."Odd.Name"(bigint) RETURNS integer LANGUAGE sql AS 'SELECT 99';
            SET LOCAL search_path=call_lookup,pg_catalog;
            """, token);
        Assert.AreEqual(42, await ScalarAsync<int>(connection, "SELECT datatype.call_integer('\"Odd.Name\"',0,41,0)", token));
        await transaction.SaveAsync("invalid_name", token);
        PostgresException invalid = await ErrorAsync(connection, "SELECT datatype.call_integer('abs; SELECT 42',0,1,0)", token);
        Assert.AreEqual(PostgresErrorCodes.InvalidName, invalid.SqlState);
        await transaction.RollbackAsync("invalid_name", token);
        Assert.AreEqual(42, await ScalarAsync<int>(connection, "SELECT datatype.call_integer('abs',0,-42,0)", token));
    }

    /// <summary>
    /// Binds variadic arrays and resolves polymorphic argument/result types.
    /// </summary>
    /// <param name="byOid">Whether to use direct catalog identities.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VariadicAndPolymorphicCallsPreserveTypes(bool byOid)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, "CREATE FUNCTION pg_temp.total(VARIADIC integer[]) RETURNS integer LANGUAGE sql AS 'SELECT sum(n)::integer FROM unnest($1) n'", token);
        string oid = byOid ? "'pg_temp.total(integer[])'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(15, await ScalarAsync<int>(connection, $"SELECT datatype.call_variadic('pg_temp.total',{oid},true)", token));
        if (!byOid)
        {
            Assert.AreEqual(15, await ScalarAsync<int>(connection, "SELECT datatype.call_variadic('pg_temp.total',0,false)", token));
        }

        string arraySignature = PostgresFixture.Cluster.Installation.Version.Major >= 14
            ? "anycompatiblearray,anycompatible" : "anyarray,anyelement";
        string arrayOid = byOid ? $"'pg_catalog.array_append({arraySignature})'::regprocedure::oid" : "0::oid";
        int[] result = await ScalarAsync<int[]>(connection, $"SELECT datatype.call_polymorphic({arrayOid})", token);
        Assert.AreSequenceEqual([3, 5, 7], result);
    }

    /// <summary>
    /// Carries an explicit collation through nested managed dispatch and returns owned large text.
    /// </summary>
    /// <param name="byOid">Whether to call by OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CollationAndTextOwnershipSurviveNativeReturn(bool byOid)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string oid = byOid ? "'datatype.call_collation(text)'::regprocedure::oid" : "0::oid";
        string collation = await ScalarAsync<string>(connection, "SELECT '\"C\"'::regcollation::oid::text", token);
        Assert.AreEqual(collation, await ScalarAsync<string>(connection, $"SELECT datatype.call_text('datatype.call_collation',{oid},'value','\"C\"'::regcollation::oid)", token));
        Assert.AreEqual("0", await ScalarAsync<string>(connection, $"SELECT datatype.call_text('datatype.call_collation',{oid},'value',0)", token));
        Assert.AreEqual("100", await ScalarAsync<string>(connection, $"SELECT datatype.call_text('datatype.call_collation',{oid},'value',NULL)", token),
            "Without an explicit collation, calls use the database default collation, as pgrx's do.");
        await ExecuteAsync(connection, "CREATE FUNCTION pg_temp.echo(text) RETURNS text LANGUAGE sql AS 'SELECT $1'", token);
        string echoOid = byOid ? "'pg_temp.echo(text)'::regprocedure::oid" : "0::oid";
        Assert.AreEqual(string.Concat(Enumerable.Repeat("héllo 🐘", 10000)), await ScalarAsync<string>(connection,
            $"SELECT datatype.call_text('pg_temp.echo',{echoOid},repeat('héllo 🐘',10000),NULL)", token));
        Assert.AreEqual("1|nested/1|1|0|0", await ScalarAsync<string>(connection, "SELECT datatype.call_managed_state()", token));
    }

    /// <summary>
    /// Preserves domain and unregistered enum return identities with caller-owned raw storage.
    /// </summary>
    /// <param name="byOid">Whether to resolve by OID.</param>
    /// <param name="isNull">Whether the returned domain value is NULL.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task RawResultsPreserveExactIdentityAndLifetime(bool byOid, bool isNull)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, $"""
            CREATE DOMAIN pg_temp.result_domain AS text;
            CREATE TYPE pg_temp.result_enum AS ENUM ('owned');
            CREATE FUNCTION pg_temp.domain_result() RETURNS pg_temp.result_domain LANGUAGE sql AS 'SELECT {(isNull ? "NULL" : "''owned''")}::pg_temp.result_domain';
            CREATE FUNCTION pg_temp.enum_result() RETURNS pg_temp.result_enum LANGUAGE sql AS 'SELECT ''owned''::pg_temp.result_enum';
            CREATE FUNCTION pg_temp.record_result(OUT number integer, OUT text text) LANGUAGE sql AS 'SELECT 42,''owned''::text';
            """, token);
        string domainType = await ScalarAsync<string>(connection, "SELECT 'pg_temp.result_domain'::regtype::oid::text", token);
        string oid = byOid ? "'pg_temp.domain_result()'::regprocedure::oid" : "0::oid";
        Assert.AreEqual($"{domainType}|{isNull}|{(isNull ? "NULL" : "owned")}|expired", await ScalarAsync<string>(connection, $"SELECT datatype.call_raw('pg_temp.domain_result',{oid})", token));
        string enumType = await ScalarAsync<string>(connection, "SELECT 'pg_temp.result_enum'::regtype::oid::text", token);
        string enumOid = byOid ? "'pg_temp.enum_result()'::regprocedure::oid" : "0::oid";
        Assert.AreEqual($"{enumType}|False|owned|expired", await ScalarAsync<string>(connection, $"SELECT datatype.call_raw('pg_temp.enum_result',{enumOid})", token));
        string recordOid = byOid ? "'pg_temp.record_result()'::regprocedure::oid" : "0::oid";
        Assert.AreEqual("2249|False|(42,owned)|expired", await ScalarAsync<string>(connection, $"SELECT datatype.call_raw('pg_temp.record_result',{recordOid})", token));
    }

    /// <summary>
    /// Binds exact raw domain values, retaining their real type through polymorphic resolution and return copying.
    /// </summary>
    /// <param name="isNull">Whether to bind a typed NULL.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RawArgumentsPreserveDomainIdentityAndNull(bool isNull)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, """
            CREATE DOMAIN pg_temp.call_domain AS text;
            CREATE FUNCTION pg_temp.raw_identity(anyelement) RETURNS anyelement LANGUAGE plpgsql AS 'BEGIN RETURN $1; END';
            DO $body$
            DECLARE target record;
            BEGIN
                SELECT probin,prosrc INTO target FROM pg_proc WHERE oid='datatype.call_raw_argument(text)'::regprocedure;
                EXECUTE format('CREATE FUNCTION pg_temp.domain_call(pg_temp.call_domain) RETURNS text AS %L,%L LANGUAGE c',target.probin,target.prosrc);
            END
            $body$;
            """, token);
        string value = isNull ? "NULL" : "repeat('owned',10000)";
        Assert.AreEqual($"True|{isNull}|{(isNull ? "NULL" : string.Concat(Enumerable.Repeat("owned", 10000)))}",
            await ScalarAsync<string>(connection, $"SELECT pg_temp.domain_call({value}::pg_temp.call_domain)", token));
    }

    /// <summary>
    /// Validates result types before execution and rolls back function-side writes when errors are caught.
    /// </summary>
    /// <param name="byOid">Whether the target uses an OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ErrorsPreserveDiagnosticsRollbackAndSameBackendRecovery(bool byOid)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await ExecuteAsync(connection, """
            CREATE TEMP TABLE call_writes(value integer);
            CREATE FUNCTION pg_temp.writes() RETURNS integer LANGUAGE plpgsql AS 'BEGIN INSERT INTO pg_temp.call_writes VALUES(1); RETURN 1; END';
            CREATE FUNCTION pg_temp.fails() RETURNS integer LANGUAGE plpgsql AS 'BEGIN INSERT INTO pg_temp.call_writes VALUES(2); RAISE EXCEPTION USING ERRCODE=''P7801'', MESSAGE=''call failed'', DETAIL=''owned detail'', HINT=''owned hint''; END';
            CREATE FUNCTION pg_temp.void_write(integer) RETURNS void LANGUAGE sql AS 'INSERT INTO pg_temp.call_writes VALUES($1)';
            """, token);
        PostgresException mismatch = await ErrorAsync(connection, "SELECT datatype.call_wrong_result('pg_temp.writes')", token);
        Assert.AreEqual(PostgresErrorCodes.DatatypeMismatch, mismatch.SqlState);
        Assert.AreEqual(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_temp.call_writes", token));
        string internalOid = byOid ? "'pg_catalog.array_agg_transfn(internal,anynonarray)'::regprocedure::oid" : "0::oid";
        // Current PostgreSQL minor releases reject the call while resolving the name; Ankus rejects it on older ones.
        Assert.AreEqual("0A000|42",
            await ScalarAsync<string>(connection, $"SELECT datatype.call_internal({internalOid})", token));
        string oid = byOid ? "'pg_temp.fails()'::regprocedure::oid" : "0::oid";
        Assert.AreEqual("P7801|call failed|owned detail|owned hint|1|42", await ScalarAsync<string>(connection, $"SELECT datatype.call_recover('pg_temp.fails',{oid})", token));
        Assert.AreEqual(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_temp.call_writes", token));
        string voidOid = byOid ? "'pg_temp.void_write(integer)'::regprocedure::oid" : "0::oid";
        await ExecuteAsync(connection, $"SELECT datatype.call_void('pg_temp.void_write',{voidOid})", token);
        Assert.AreEqual(42, await ScalarAsync<int>(connection, "SELECT value FROM pg_temp.call_writes", token));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Executes setup or a void result using the same backend.
    /// </summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Reads one exactly typed SQL result.
    /// </summary>
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Observes a PostgreSQL diagnostic without accepting transport failures.
    /// </summary>
    private static async Task<PostgresException> ErrorAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
    }
}
