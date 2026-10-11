namespace Ankus.Generators;

/// <summary>
/// Resolves catalog function calls and evaluates them using PostgreSQL's expression executor.
/// </summary>
internal static class NativeFunctionInvocation
{
    /// <summary>
    /// Gets scalar function invocation inside the existing native error and subtransaction boundary.
    /// </summary>
    internal const string Source = """
        #include "catalog/pg_proc.h"
        #include "catalog/pg_collation.h"
        #include "nodes/nodeFuncs.h"
        #include "nodes/readfuncs.h"
        #include "optimizer/optimizer.h"
        #include "parser/parse_collate.h"
        #include "parser/parse_func.h"
        #include "utils/acl.h"
        #include "utils/regproc.h"
        #include "utils/syscache.h"

        static Expr *
        ankus_function_default(HeapTuple tuple, int index)
        {
            Form_pg_proc procedure = (Form_pg_proc) GETSTRUCT(tuple);
            int first = procedure->pronargs - procedure->pronargdefaults;
            if (index < first || index >= procedure->pronargs)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                    errmsg("Function argument %d has no default", index + 1)));
            bool is_null;
            Datum stored = SysCacheGetAttr(PROCOID, tuple, Anum_pg_proc_proargdefaults, &is_null);
            if (is_null)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Function has no default arguments")));
            List *defaults = (List *) stringToNode(TextDatumGetCString(stored));
            return (Expr *) list_nth(defaults, index - first);
        }

        static void
        ankus_function_require_scalar(Form_pg_proc procedure)
        {
            if (procedure->prokind != PROKIND_FUNCTION || procedure->proretset)
                ereport(ERROR, (errcode(ERRCODE_WRONG_OBJECT_TYPE), errmsg("The call requires a scalar function")));
        }

        static FuncExpr *
        ankus_function_by_oid(AnkusRequest *request, ParseState *parse, List *arguments)
        {
            HeapTuple tuple = SearchSysCache1(PROCOID, ObjectIdGetDatum(request->function_oid));
            if (!HeapTupleIsValid(tuple))
                ereport(ERROR, (errcode(ERRCODE_UNDEFINED_FUNCTION), errmsg("Function with OID %u does not exist", request->function_oid)));
            Form_pg_proc procedure = (Form_pg_proc) GETSTRUCT(tuple);
            ankus_function_require_scalar(procedure);
            if (request->parameter_count > procedure->pronargs)
                ereport(ERROR, (errcode(ERRCODE_TOO_MANY_ARGUMENTS), errmsg("Too many arguments for function with OID %u", request->function_oid)));

            for (int index = 0; index < procedure->pronargs; index++)
            {
                if (index >= request->parameter_count)
                    arguments = lappend(arguments, ankus_function_default(tuple, index));
                else if (request->argument_defaults[index])
                    lfirst(list_nth_cell(arguments, index)) = ankus_function_default(tuple, index);
            }

            Oid actual[FUNC_MAX_ARGS];
            Oid declared[FUNC_MAX_ARGS];
            for (int index = 0; index < procedure->pronargs; index++)
            {
                actual[index] = exprType(list_nth(arguments, index));
                declared[index] = procedure->proargtypes.values[index];
                if (declared[index] == INTERNALOID)
                    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("An internal argument requires a native entry-point call")));
            }

            Oid result_type = enforce_generic_type_consistency(actual, declared, procedure->pronargs, procedure->prorettype, false);
            if (result_type == INTERNALOID)
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("An internal result requires a native entry-point call")));
            if (!can_coerce_type(procedure->pronargs, actual, declared, COERCION_IMPLICIT))
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Function arguments do not match the declared types")));
            make_fn_arguments(parse, arguments, actual, declared);
            FuncExpr *expression = makeFuncExpr(request->function_oid, result_type, arguments,
                InvalidOid, InvalidOid, COERCE_EXPLICIT_CALL);
            expression->funcvariadic = OidIsValid(procedure->provariadic);
            ReleaseSysCache(tuple);
            return expression;
        }

        static FuncExpr *
        ankus_function_by_name(AnkusRequest *request, ParseState *parse, List *arguments)
        {
            char *name = pg_any_to_server(request->command, request->command_length, PG_UTF8);
        #if PG_VERSION_NUM >= 160000
            List *parts = stringToQualifiedNameList(name, NULL);
        #else
            List *parts = stringToQualifiedNameList(name);
        #endif
            FuncCall *syntax = makeNode(FuncCall);
            syntax->funcname = parts;
            syntax->func_variadic = request->variadic;
        #if PG_VERSION_NUM >= 140000
            syntax->funcformat = COERCE_EXPLICIT_CALL;
        #endif
            syntax->location = -1;
            Node *resolved = ParseFuncOrColumn(parse, parts, arguments, NULL, syntax, false, -1);
            if (!IsA(resolved, FuncExpr))
                ereport(ERROR, (errcode(ERRCODE_WRONG_OBJECT_TYPE), errmsg("The name must resolve to a scalar function")));
            FuncExpr *expression = (FuncExpr *) resolved;
            HeapTuple tuple = SearchSysCache1(PROCOID, ObjectIdGetDatum(expression->funcid));
            if (!HeapTupleIsValid(tuple))
                ereport(ERROR, (errcode(ERRCODE_UNDEFINED_FUNCTION), errmsg("Function disappeared during lookup")));
            Form_pg_proc procedure = (Form_pg_proc) GETSTRUCT(tuple);
            ankus_function_require_scalar(procedure);
            /* As for a call by OID: an internal value is a native pointer only its own entry points can interpret. */
            for (int index = 0; index < procedure->pronargs; index++)
            {
                if (procedure->proargtypes.values[index] == INTERNALOID)
                    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("An internal argument requires a native entry-point call")));
            }

            if (expression->funcresulttype == INTERNALOID)
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("An internal result requires a native entry-point call")));
            for (int index = 0; index < request->parameter_count; index++)
            {
                if (!request->argument_defaults[index])
                    continue;
                if (index >= list_length(expression->args))
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("A variadic element has no declared default")));
                Expr *value = ankus_function_default(tuple, index);
                Node *coerced = coerce_to_target_type(parse, (Node *) value, exprType((Node *) value),
                    exprType(list_nth(expression->args, index)), -1, COERCION_IMPLICIT, COERCE_IMPLICIT_CAST, -1);
                if (coerced == NULL)
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Function default does not match the resolved argument type")));
                lfirst(list_nth_cell(expression->args, index)) = coerced;
            }

        #if PG_VERSION_NUM >= 140000
            expression->args = expand_function_arguments(expression->args, false, expression->funcresulttype, tuple);
        #else
            expression->args = expand_function_arguments(expression->args, expression->funcresulttype, tuple);
        #endif
            ReleaseSysCache(tuple);
            return expression;
        }

        static void
        ankus_function_check_result(Oid actual, Oid expected, bool exact)
        {
            if (exact)
            {
                if (actual != expected)
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH),
                        errmsg("Function result type %s does not match requested type %s", format_type_be(actual), format_type_be(expected))));
                return;
            }

            Oid base = getBaseType(actual);
            if (expected == ANYELEMENTOID || (expected == ANYARRAYOID && OidIsValid(get_element_type(base))))
                return;
            if (OidIsValid(expected) && base != expected && !(expected == RECORDOID && type_is_rowtype(base)))
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH),
                    errmsg("Function result type %s does not match requested type %s", format_type_be(actual), format_type_be(expected))));
        }

        static void
        ankus_function_store_result(AnkusRequest *request, AnkusResult *result, Datum value, bool is_null, Oid type)
        {
            result->result_type_oid = type;
            result->processed = getBaseType(type);
            result->text.is_null = is_null;
            if (is_null || type == VOIDOID)
                return;
            if (request->result_context != 0)
                result->text.integral = (int64) (uintptr_t) ankus_copy_raw_datum(value, type,
                    request->result_context, request->result_generation);
            else
            {
                ankus_check_result_enum((Oid) result->processed);
                ankus_result_value(value, (Oid) result->processed, &result->text);
            }
        }

        static void
        ankus_function_invoke(AnkusRequest *request, AnkusResult *result)
        {
            if (request->scalar_operation < 0 || request->scalar_operation > 3 ||
                (request->scalar_operation != 0 &&
                    (!OidIsValid(request->scalar_result_oid) || request->native_function != NULL)))
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Invalid function result validation mode")));

            if (request->native_function != NULL)
            {
                if (request->parameter_count < 0 || request->parameter_count > PG_INT16_MAX)
                    ereport(ERROR, (errcode(ERRCODE_TOO_MANY_ARGUMENTS), errmsg("Too many native function arguments")));
                if (request->result_context != 0)
                    ankus_datum_context(request->result_context, request->result_generation);
                FunctionCallInfo call = palloc0(SizeForFunctionCallInfo(request->parameter_count));
                InitFunctionCallInfoData(*call, NULL, (int16) request->parameter_count, request->collation_oid, NULL, NULL);
                for (int index = 0; index < request->parameter_count; index++)
                {
                    call->args[index].isnull = request->parameters[index].value.is_null;
                    call->args[index].value = ankus_parameter_datum(&request->parameters[index]);
                }

                Datum value = request->native_function(call);
                ankus_function_store_result(request, result, value, call->isnull, request->scalar_result_oid);
                return;
            }

            if (request->parameter_count < 0 || request->parameter_count > FUNC_MAX_ARGS)
                ereport(ERROR, (errcode(ERRCODE_TOO_MANY_ARGUMENTS), errmsg("Too many function arguments")));
            if (request->result_context != 0)
                ankus_datum_context(request->result_context, request->result_generation);

            ParseState *parse = make_parsestate(NULL);
            parse->p_expr_kind = EXPR_KIND_OTHER;
            List *arguments = NIL;
            for (int index = 0; index < request->parameter_count; index++)
            {
                const AnkusParameter *parameter = &request->parameters[index];
                int16 length;
                bool by_value;
                get_typlenbyval(parameter->type_oid, &length, &by_value);
                bool is_default = request->argument_defaults[index];
                bool is_null = is_default || parameter->value.is_null;
                Datum value = is_default ? (Datum) 0 : ankus_parameter_datum(parameter);
                arguments = lappend(arguments, makeConst(parameter->type_oid, -1, get_typcollation(parameter->type_oid),
                    length, value, is_null, by_value));
            }

            FuncExpr *expression = OidIsValid(request->function_oid)
                ? ankus_function_by_oid(request, parse, arguments) : ankus_function_by_name(request, parse, arguments);
            assign_expr_collations(parse, (Node *) expression);
            if (request->has_collation)
            {
                if (OidIsValid(request->collation_oid) && !SearchSysCacheExists1(COLLOID, ObjectIdGetDatum(request->collation_oid)))
                    ereport(ERROR, (errcode(ERRCODE_UNDEFINED_OBJECT), errmsg("Collation with OID %u does not exist", request->collation_oid)));
                expression->inputcollid = request->collation_oid;
                if (OidIsValid(get_typcollation(expression->funcresulttype)))
                    expression->funccollid = request->collation_oid;
            }

            Oid type = expression->funcresulttype;
            if (request->scalar_operation >= 2)
                ankus_array_element_contract(getBaseType(type), request->scalar_result_oid, request->scalar_operation == 3);
            else
                ankus_function_check_result(type, request->scalar_result_oid, request->scalar_operation == 1);
            /* Check before constant folding can replace a STRICT NULL call. */
        #if PG_VERSION_NUM >= 160000
            AclResult access = object_aclcheck(ProcedureRelationId, expression->funcid, GetUserId(), ACL_EXECUTE);
        #else
            AclResult access = pg_proc_aclcheck(expression->funcid, GetUserId(), ACL_EXECUTE);
        #endif
            if (access != ACLCHECK_OK)
                aclcheck_error(access, OBJECT_FUNCTION, get_func_name(expression->funcid));
            result->function_oid = expression->funcid;
            MemoryContext previous = CurrentMemoryContext;
            EState *estate = CreateExecutorState();
            PG_TRY();
            {
                ExprState *state = ExecPrepareExpr((Expr *) expression, estate);
                bool is_null;
                Datum value = ExecEvalExprSwitchContext(state, GetPerTupleExprContext(estate), &is_null);
                ankus_function_store_result(request, result, value, is_null, type);
            }
            PG_FINALLY();
            {
                MemoryContextSwitchTo(previous);
                FreeExecutorState(estate);
            }
            PG_END_TRY();
            free_parsestate(parse);
        }
        """;
}
