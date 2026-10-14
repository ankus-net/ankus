namespace Ankus.Generators;

/// <summary>
/// Emits selected-header relation access with checked resource-owner lifetimes and native error containment.
/// </summary>
internal static class NativeRelationBridge
{
    /// <summary>
    /// Gets retained relation references, precise close obligations, live metadata, and PostgreSQL statistics operations.
    /// </summary>
    internal const string Source = """
        #include "access/relation.h"
        #include "catalog/pg_class.h"
        #include "catalog/pg_index.h"
        #include "pgstat.h"
        #include "utils/rel.h"
        #include "utils/relcache.h"
        #include "utils/resowner.h"

        typedef struct AnkusRelation
        {
            int64 identity;
            Relation relation;
            ResourceOwner owner;
            LOCKMODE lockmode;
            bool owned;
            bool private_owner;
            struct AnkusRelation *next;
        } AnkusRelation;

        static AnkusRelation *ankus_relations = NULL;
        static int64 ankus_next_relation = 1;
        static bool ankus_relation_callback_registered = false;

        static void
        ankus_relation_resource_release(ResourceReleasePhase phase, bool is_commit, bool is_top_level, void *argument)
        {
            (void) is_commit;
            (void) is_top_level;
            (void) argument;
            if (phase != RESOURCE_RELEASE_BEFORE_LOCKS)
                return;
            /* PostgreSQL has already released this owner's relcache references.
             * Never close those pins again, or delete the owner while it is releasing. */
            AnkusRelation **link = &ankus_relations;
            while (*link != NULL)
            {
                AnkusRelation *entry = *link;
                if (entry->owner == CurrentResourceOwner)
                {
                    *link = entry->next;
                    free(entry);
                }
                else
                    link = &entry->next;
            }
        }

        static AnkusRelation *
        ankus_relation_find(int64 identity)
        {
            for (AnkusRelation *entry = ankus_relations; entry != NULL; entry = entry->next)
                if (entry->identity == identity)
                    return entry;
            return NULL;
        }

        static void
        ankus_relation_delete_owner(ResourceOwner owner)
        {
            ResourceOwnerRelease(owner, RESOURCE_RELEASE_BEFORE_LOCKS, false, false);
            ResourceOwnerRelease(owner, RESOURCE_RELEASE_LOCKS, false, false);
            ResourceOwnerRelease(owner, RESOURCE_RELEASE_AFTER_LOCKS, false, false);
            ResourceOwnerDelete(owner);
        }

        static void
        ankus_relation_close(int64 identity)
        {
            AnkusRelation **link = &ankus_relations;
            while (*link != NULL && (*link)->identity != identity)
                link = &(*link)->next;
            if (*link == NULL)
                return;
            AnkusRelation *entry = *link;
            ResourceOwner previous = CurrentResourceOwner;
            PG_TRY();
            {
                if (entry->owned)
                {
                    CurrentResourceOwner = entry->owner;
                    if (entry->lockmode != NoLock)
                        relation_close(entry->relation, entry->lockmode);
                    else
                        RelationClose(entry->relation);
                    entry->owned = false;
                }

                CurrentResourceOwner = previous;
            }
            PG_FINALLY();
            {
                CurrentResourceOwner = previous;
            }
            PG_END_TRY();
            *link = entry->next;
            ResourceOwner owner = entry->private_owner ? entry->owner : NULL;
            free(entry);
            if (owner != NULL)
                ankus_relation_delete_owner(owner);
        }

        static void
        ankus_relation_open(AnkusRequest *request, AnkusResult *result, ResourceOwner caller_owner)
        {
            bool wrapping = request->scalar_operation == 3 || request->scalar_operation == 4;
            LOCKMODE lockmode = request->limit;
            Oid oid = request->function_oid;
            if (lockmode < NoLock || lockmode > AccessExclusiveLock)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid relation lock mode")));
            if (request->scalar_operation == 2)
            {
                if (request->parameter_count != 1)
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid relation name request")));
                Datum input = ankus_write_typed_buffer(&request->parameters[0].value, TEXTOID);
                if (request->read_only)
                {
                    LOCAL_FCINFO(info, 1);
                    InitFunctionCallInfoData(*info, NULL, 1, InvalidOid, NULL, NULL);
                    info->args[0].value = input;
                    info->args[0].isnull = false;
                    Datum resolved = to_regclass(info);
                    if (info->isnull)
                        return;

                    oid = DatumGetObjectId(resolved);
                }
                else
                {
                    oid = DatumGetObjectId(DirectFunctionCall1(regclassin,
                        CStringGetDatum(TextDatumGetCString(input))));
                }
            }

            if (wrapping && request->callback == 0)
                ereport(ERROR, (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED), errmsg("a relation pointer cannot be null")));
            if (!ankus_relation_callback_registered)
            {
                RegisterResourceReleaseCallback(ankus_relation_resource_release, NULL);
                ankus_relation_callback_registered = true;
            }

            if (ankus_next_relation == PG_INT64_MAX)
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("relation identity space exhausted")));
            AnkusRelation *entry = calloc(1, sizeof(AnkusRelation));
            if (entry == NULL)
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("unable to retain relation identity")));
            ResourceOwner previous = CurrentResourceOwner;
            entry->identity = ankus_next_relation++;
            /* Raw adoption becomes owned only after the guard commits successfully. */
            entry->owned = !wrapping;
            PG_TRY();
            {
                if (wrapping)
                {
                    entry->relation = (Relation) request->callback;
                    entry->owner = caller_owner;
                }
                else
                {
                    entry->owner = ResourceOwnerCreate(previous, "Ankus relation");
                    entry->private_owner = true;
                    entry->lockmode = lockmode;
                    CurrentResourceOwner = entry->owner;
                    entry->relation = lockmode == NoLock ? RelationIdGetRelation(oid) : relation_open(oid, lockmode);
                    if (!RelationIsValid(entry->relation))
                        ereport(ERROR, (errcode(ERRCODE_UNDEFINED_TABLE), errmsg("relation with OID %u does not exist", oid)));
                    CurrentResourceOwner = previous;
                    /* The guard's internal subtransaction must not release this pin.
                     * The caller's statement, portal, or transaction still bounds its lifetime. */
                    ResourceOwnerNewParent(entry->owner, caller_owner);
                }
            }
            PG_CATCH();
            {
                CurrentResourceOwner = previous;
                if (entry->private_owner)
                    ankus_relation_delete_owner(entry->owner);
                free(entry);
                PG_RE_THROW();
            }
            PG_END_TRY();
            entry->next = ankus_relations;
            ankus_relations = entry;
            result->cursor_id = entry->identity;
        }

        static void
        ankus_relation_finish(AnkusRequest *request, AnkusResult *result)
        {
            if (request->scalar_operation != 4 && request->scalar_operation != 5)
                return;
            AnkusRelation *entry = ankus_relation_find(request->scalar_operation == 4 ? result->cursor_id : request->cursor_id);
            if (entry == NULL)
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("relation reference has expired")));
            entry->owned = true;
        }

        static void
        ankus_relation_operation(AnkusRequest *request, AnkusResult *result, ResourceOwner caller_owner)
        {
            /* 0 closes a reference, and 1 through 4 open one under the caller's resource owner. */
            int operation = request->scalar_operation;
            if (operation == 0)
            {
                ankus_relation_close(request->cursor_id);
                return;
            }

            if (operation >= 1 && operation <= 4)
            {
                ankus_relation_open(request, result, caller_owner);
                return;
            }

            AnkusRelation *entry = ankus_relation_find(request->cursor_id);
            if (entry == NULL)
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("relation reference has expired")));
            Relation relation = entry->relation;
            switch (operation)
            {
                case 5:
                    /* Validate now; transfer the close obligation only after successful guard completion. */
                    break;
                /* 6 through 15 read the relation's identity, statistics, descriptor and indexes. */
                case 6:
                    result->text.integral = RelationGetRelid(relation);
                    break;
                case 7:
                    ankus_result_value(CStringGetTextDatum(RelationGetRelationName(relation)), TEXTOID, &result->text);
                    break;
                case 8:
                    result->text.integral = RelationGetNamespace(relation);
                    break;
                case 9:
                    ankus_result_value(CStringGetTextDatum(get_namespace_name(RelationGetNamespace(relation))), TEXTOID, &result->text);
                    break;
                case 10:
                    result->text.integral = relation->rd_rel->relkind;
                    break;
                case 11:
                    ankus_result_value(Float4GetDatum(relation->rd_rel->reltuples), FLOAT4OID, &result->text);
                    result->text.is_null = relation->rd_rel->reltuples == 0;
                    break;
                case 12:
                {
                    AnkusValue input = {0};
                    AnkusInputBuffer owned = {0};
                    PG_TRY();
                    {
                        TupleDesc descriptor = RelationGetDescr(relation);
                        ankus_tuple_transport(descriptor, descriptor->tdtypeid, NULL, NULL, &input, &owned);
                        result->text = input;
                        result->text.data = NULL;
                        ankus_copy_owned(&result->text, input.data, input.length);
                    }
                    PG_FINALLY();
                    {
                        ankus_free_input(&owned);
                    }
                    PG_END_TRY();
                    break;
                }
                case 13:
                {
                    List *indexes = RelationGetIndexList(relation);
                    Datum *oids = palloc(sizeof(Datum) * Max(list_length(indexes), 1));
                    int count = 0;
                    ListCell *cell;
                    foreach(cell, indexes)
                    {
                        Oid oid = lfirst_oid(cell);
                        if (OidIsValid(oid))
                            oids[count++] = ObjectIdGetDatum(oid);
                    }

                    ankus_result_value(PointerGetDatum(construct_array(oids, count, OIDOID, sizeof(Oid), true, TYPALIGN_INT)),
                        OIDARRAYOID, &result->text);
                    break;
                }
                case 14:
                    result->text.integral = relation->rd_index == NULL ? InvalidOid : relation->rd_index->indrelid;
                    break;
                case 15:
                    result->text.integral = (intptr_t) relation;
                    break;
                /* 16 through 22 add scan work to the relation's statistics. */
                case 16:
                    pgstat_count_heap_scan(relation);
                    break;
                case 17:
                    pgstat_count_index_scan(relation);
                    break;
                case 18:
                    pgstat_count_heap_getnext(relation);
                    break;
                case 19:
                    pgstat_count_heap_fetch(relation);
                    break;
                case 20:
                    if (request->parameter_count != 1)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid relation statistics request")));
                    pgstat_count_index_tuples(relation, request->parameters[0].value.integral);
                    break;
                case 21:
                    pgstat_count_buffer_read(relation);
                    break;
                case 22:
                    pgstat_count_buffer_hit(relation);
                    break;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid relation operation")));
            }
        }

        """;
}
