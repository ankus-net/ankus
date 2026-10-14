using System.Globalization;
using System.Text.Json.Serialization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises relation references, real locks, live metadata and typed regclass transport in a published extension.
/// </summary>
[PgSchema("relations")]
public static class RelationFunctions
{
    private static PgRelation? s_retained;
    private static int s_iteratorDisposals;

    /// <summary>
    /// Copies all relation identity, estimate and kind predicates for independent catalog comparison.
    /// </summary>
    [PgFunction]
    public static string?[] Describe(PgRelation relation)
        => [relation.Oid.ToString(CultureInfo.InvariantCulture), relation.Name,
            relation.NamespaceOid.ToString(CultureInfo.InvariantCulture), relation.NamespaceName,
            relation.Kind.ToString(), relation.EstimatedTupleCount?.ToString("R", CultureInfo.InvariantCulture),
            string.Concat(new[] { relation.IsTable, relation.IsMaterializedView, relation.IsIndex, relation.IsView,
                relation.IsSequence, relation.IsCompositeType, relation.IsForeignTable, relation.IsPartitionedTable, relation.IsToast }
                .Select(static value => value ? '1' : '0'))];

    /// <summary>
    /// Uses native name lookup and an explicitly selected relation lock.
    /// </summary>
    [PgFunction]
    public static string?[]? DescribeName(string name, int mode)
    {
        using PgRelation? relation = PgRelation.TryOpen(name, (PgLockMode)mode);
        return relation is null ? null : Describe(relation);
    }

    /// <summary>
    /// Opens a relation inside an SPI session, and another inside a nested session, then uses both after the sessions
    /// close, as a relation opened inside pgrx's <c>Spi::connect</c> stays open.
    /// </summary>
    /// <param name="oid">The relation to open.</param>
    /// <returns>Both names, the kind, and this backend's locks on the relation before and after closing both.</returns>
    [PgFunction]
    public static string SessionRelationsOutliveTheirSessions(uint oid)
    {
        PgRelation? nested = null;
        PgRelation outer = Spi.Connect(session =>
        {
            _ = session.ExecuteScalar<int>("SELECT 1");
            nested = Spi.Connect(inner =>
            {
                _ = inner.ExecuteScalar<int>("SELECT 2");
                return PgRelation.Open(oid, PgLockMode.RowExclusive);
            });
            return PgRelation.Open(oid);
        });
        string held = Locks(oid);
        string description = outer.Name + "|" + nested!.Name + "|" + outer.Kind;
        outer.Dispose();
        nested.Dispose();
        return description + "|" + held + "|" + Locks(oid);

        static string Locks(uint relation) => Spi.ExecuteScalar<string>(
            "SELECT coalesce(string_agg(mode, ',' ORDER BY mode), '') FROM pg_locks WHERE relation = $1 AND pid = pg_backend_pid()",
            SpiParameter.Create(relation));
    }

    /// <summary>
    /// Returns an input reference as regclass, transferring the generated callback's ownership.
    /// </summary>
    [PgFunction]
    public static PgRelation? Identity(PgRelation? relation) => relation;

    /// <summary>
    /// Returns a vector of relation values and SQL NULL elements.
    /// </summary>
    [PgFunction]
    public static PgRelation?[] Vector(PgRelation?[] values) => values;

    /// <summary>
    /// Returns the exact multidimensional regclass array shape and NULL elements.
    /// </summary>
    [PgFunction]
    public static PgArray<PgRelation?> Shaped(PgArray<PgRelation?> values) => values;

    /// <summary>
    /// Copies the actual native descriptor and reads it after releasing the relation.
    /// </summary>
    [PgFunction]
    public static PgJsonb Descriptor(uint oid)
    {
        PgTupleDescriptor descriptor;
        using (PgRelation relation = PgRelation.Open(oid))
        {
            descriptor = relation.TupleDescriptor;
        }

        return PgJsonb.Serialize(descriptor, RelationJsonContext.Default.PgTupleDescriptor);
    }

    /// <summary>
    /// Enumerates independent index references and verifies their heap and clone identity.
    /// </summary>
    [PgFunction]
    public static uint[] Indices(uint oid)
    {
        using PgRelation relation = PgRelation.Open(oid);
        IReadOnlyList<PgRelation> indexes = relation.GetIndices();
        try
        {
            uint[] result = new uint[indexes.Count];
            for (int index = 0; index < result.Length; index++)
            {
                PgRelation item = indexes[index];
                using PgRelation clone = item.Clone();
                using PgRelation heap = item.DangerousOpenHeap()!;
                if (heap.Oid != oid || clone.Oid != item.Oid || item.TupleDescriptor.Attributes.Count == 0)
                {
                    throw new InvalidOperationException("Index reference identity mismatch.");
                }

                result[index] = item.Oid;
            }

            return result;
        }
        finally
        {
            Disposal.DisposeAll(indexes);
        }
    }

    /// <summary>
    /// Observes a selected lock in the native lock catalog before acquisition, while held, and after release.
    /// </summary>
    [PgFunction]
    public static bool[] LockCycle(uint oid, int mode)
    {
        string name = mode switch
        {
            1 => "AccessShareLock",
            2 => "RowShareLock",
            3 => "RowExclusiveLock",
            4 => "ShareUpdateExclusiveLock",
            5 => "ShareLock",
            6 => "ShareRowExclusiveLock",
            7 => "ExclusiveLock",
            8 => "AccessExclusiveLock",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        bool before = HasLock(oid, name);
        using PgRelation relation = PgRelation.Open(oid, (PgLockMode)mode);
        bool held = HasLock(oid, name);
        relation.Dispose();
        return [before, held, HasLock(oid, name)];
    }

    /// <summary>
    /// Retains a reference past its native resource owner for a separate expiry probe.
    /// </summary>
    [PgFunction]
    public static uint Retain(uint oid)
    {
        s_retained?.Dispose();
        s_retained = PgRelation.Open(oid);
        return s_retained.Oid;
    }

    /// <summary>
    /// Verifies resource-owner release invalidates the token and makes a later close harmless.
    /// </summary>
    [PgFunction]
    public static string Expired()
    {
        PgRelation relation = s_retained ?? throw new InvalidOperationException("No retained relation.");
        s_retained = null;
        using (relation)
        {
            try
            {
                return relation.Oid.ToString(CultureInfo.InvariantCulture);
            }
            catch (PgException error)
            {
                return error.SqlState;
            }
        }
    }

    /// <summary>
    /// Reads typed regclass SPI scalars and arrays, retaining only explicitly requested references.
    /// </summary>
    [PgFunction]
    public static string SpiAndRaw(PgRelation relation)
    {
        SpiResult rows = Spi.Query("SELECT $1 AS r, ARRAY[$1,NULL,$1] AS a", SpiParameter.Create(relation));
        using PgRelation scalar = rows[0].Get<PgRelation>("r");
        PgRelation?[] array = rows[0].Get<PgRelation?[]>("a");
        try
        {
            PgDatum datum = relation.ToDatum(PgMemoryContext.Current);
            using PgRelation raw = datum.Read<PgRelation>();
            using PgRelation called = PgFunctions.Call<PgRelation>("to_regclass", PgFunctionArgument.Create(relation.Name));
            if (called.Oid != relation.Oid)
            {
                throw new InvalidOperationException("Function-call regclass identity mismatch.");
            }

            return $"{scalar.Oid}|{array[0]!.Oid}|{array[1] is null}|{array[2]!.Oid}|{raw.Oid}|{datum.TypeOid}";
        }
        finally
        {
            Disposal.DisposeAll(array);
        }
    }

    /// <summary>
    /// Copies relation cells into rows and composite fields before disposing their original handles.
    /// </summary>
    [PgFunction]
    public static bool DetachedCells(uint oid)
    {
        PgHeapTuple tuple;
        SpiResult rows = Spi.Query("SELECT NULL::regclass AS r, NULL::regclass[] AS a");
        using (PgRelation original = PgRelation.Open(oid))
        {
            PgRelation?[] vector = [original, null, original];
            rows[0].Set("r", original);
            rows[0].Set("a", vector);
            tuple = PgHeapTuple.Create(("r", SpiParameter.Create(original)), ("a", SpiParameter.Create(vector)));
            tuple.Set("r", original);
            tuple.Set("a", vector);
        }

        using PgRelation scalar = rows[0].Get<PgRelation>("r");
        using PgRelation field = tuple.Get<PgRelation>("r");
        PgRelation?[] array = rows[0].Get<PgRelation?[]>("a");
        try
        {
            return scalar.Oid == oid && field.Oid == oid && array[0]!.Oid == oid && array[1] is null && array[2]!.Oid == oid &&
                Spi.ExecuteScalar<bool>("SELECT to_jsonb($1) = jsonb_build_object('r',$2,'a',ARRAY[$2,NULL,$2])",
                    SpiParameter.Create(tuple), SpiParameter.Create(scalar));
        }
        finally
        {
            Disposal.DisposeAll(array);
        }
    }

    /// <summary>
    /// Reads a generated relation argument across deferred advances and counts iterator cleanup.
    /// </summary>
    [PgFunction]
    public static IEnumerable<string> Names(PgRelation relation, int count)
    {
        try
        {
            for (int index = 0; index < count; index++)
            {
                _ = Spi.ExecuteScalar<int>("SELECT 42");
                yield return relation.Name + index.ToString(CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            s_iteratorDisposals++;
        }
    }

    /// <summary>
    /// Retains relation inputs while the generated materialization path drains the iterator.
    /// </summary>
    [PgFunction(SetMode = PgSetMode.Materialize)]
    public static IEnumerable<string> MaterializedNames(PgRelation relation, int count) => Names(relation, count);

    /// <summary>
    /// Yields owned relation results whose native references are transferred by the generated boundary.
    /// </summary>
    [PgFunction]
    public static IEnumerable<PgRelation?> Values(uint oid)
    {
        yield return PgRelation.Open(oid);
        yield return null;
        yield return PgRelation.Open(oid);
    }

    /// <summary>
    /// Yields an input repeatedly and reads it again, proving output cleanup preserves iterator-owned arguments.
    /// </summary>
    [PgFunction]
    public static IEnumerable<(PgRelation relation, PgRelation?[] many)> Repeated(PgRelation input)
    {
        yield return (input, [input, input.Clone(), null]);
        _ = input.Name;
        yield return (input, [input.Clone(), null, input]);
    }

    /// <summary>
    /// Fails during iterator advancement after acquiring relation arguments so native abort cleanup releases their leases.
    /// </summary>
    [PgFunction]
    public static IEnumerable<string> FailNames(PgRelation relation)
    {
        try
        {
            yield return relation.Name;
            _ = Spi.ExecuteScalar<int>("SELECT 1/0");
            yield return relation.Name;
        }
        finally
        {
            s_iteratorDisposals++;
        }
    }

    /// <summary>
    /// Holds a relation lock while an independent connection controls a native advisory-lock barrier.
    /// </summary>
    [PgFunction]
    public static void HoldUntilSignal(uint oid, int mode, long signal)
    {
        using PgRelation relation = PgRelation.Open(oid, (PgLockMode)mode);
        Spi.Execute("SELECT pg_advisory_lock($1)", SpiParameter.Create(signal));
        Spi.Execute("SELECT pg_advisory_unlock($1)", SpiParameter.Create(signal));
    }

    /// <summary>
    /// Reads the same retained relation after catalog invalidation updates its name and row estimate.
    /// </summary>
    [PgFunction]
    public static string?[] LiveMetadata(uint oid)
    {
        using PgRelation relation = PgRelation.Open(oid, PgLockMode.AccessExclusive);
        string original = relation.Name;
        Spi.Execute("ALTER TABLE " + Spi.QuoteQualifiedIdentifier(relation.NamespaceName, original) + " RENAME TO relation_live_after");
        Spi.Execute("UPDATE pg_catalog.pg_class SET reltuples=1.25 WHERE oid=$1", SpiParameter.Create(oid));
        return [original, relation.Name, relation.EstimatedTupleCount?.ToString("R", CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Captures native name-resolution errors without aborting the caller's transaction.
    /// </summary>
    [PgFunction]
    public static string NameError(string name)
    {
        try
        {
            using PgRelation relation = PgRelation.Open(name);
            return relation.Name;
        }
        catch (PgException error)
        {
            return error.SqlState;
        }
    }

    /// <summary>
    /// Opens multiple generated arguments so a later invalid relation tests cleanup of earlier conversions.
    /// </summary>
    [PgFunction]
    public static uint Pair(PgRelation first, PgRelation second) => first.Oid == second.Oid ? first.Oid : second.Oid;

    /// <summary>
    /// Rejects a later SPI column after opening relation results, proving provisional leases close before retry.
    /// </summary>
    [PgFunction]
    public static bool SpiPartial(uint oid)
    {
        bool caught = false;
        try
        {
            _ = Spi.ExecuteScalars<PgRelation?[], PgRelation, int>(
                "SELECT ARRAY[$1::regclass,NULL,$1::regclass],$1::regclass,'not an integer'::text", SpiParameter.Create(oid));
        }
        catch (InvalidCastException)
        {
            caught = true;
        }

        bool leaked = HasLock(oid, "AccessShareLock");
        (PgRelation relation, int answer) = Spi.ExecuteScalars<PgRelation, int>("SELECT $1::regclass,42", SpiParameter.Create(oid));
        using (relation)
        {
            return caught && !leaked && relation.Oid == oid && answer == 42;
        }
    }

    /// <summary>
    /// Handles a native subtransaction commit failure after acquisition and checks both lock cleanup and successful retry.
    /// </summary>
    [PgFunction]
    public static bool CommitFault(uint oid)
    {
        bool caught = false;
        try
        {
            using PgRelation relation = PgRelation.Open(oid, PgLockMode.RowExclusive);
        }
        catch (PgException error)
        {
            caught = error.SqlState == "P0001" && error.Message == "relation commit fault";
        }

        bool leaked = HasLock(oid, "RowExclusiveLock");
        using PgRelation retry = PgRelation.Open(oid);
        return caught && !leaked && retry.Oid == oid;
    }

    /// <summary>
    /// Rejects a native commit fault while adopting or transferring an external reference without consuming its close obligation.
    /// </summary>
    [PgFunction]
    public static unsafe bool TransferFault(long address, int mode)
    {
        using PgRelation? borrowed = mode == 0 ? null : PgRelation.DangerousBorrow((void*)(nint)address);
        try
        {
            using PgRelation owned = mode == 0 ? PgRelation.DangerousAdopt((void*)(nint)address)! : borrowed!.DangerousTakeOwnership();
            return false;
        }
        catch (PgException error)
        {
            return error.SqlState == "P0001" && error.Message == "relation commit fault";
        }
    }

    /// <summary>
    /// Reads and resets the iterator cleanup count.
    /// </summary>
    [PgFunction]
    public static int Disposals()
    {
        int count = s_iteratorDisposals;
        s_iteratorDisposals = 0;
        return count;
    }

    /// <summary>
    /// Catches a guarded native open error and checks same-callback recovery.
    /// </summary>
    [PgFunction]
    public static string Missing(uint oid)
    {
        string state;
        bool cleaned;
        try
        {
            using PgRelation relation = PgRelation.Open(oid);
            state = relation.Name;
        }
        catch (PgException error)
        {
            state = error.SqlState;
        }
        finally
        {
            cleaned = true;
        }

        return $"{state}|{cleaned}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    private static bool HasLock(uint oid, string mode)
        => Spi.ExecuteScalar<bool>("SELECT EXISTS(SELECT FROM pg_locks WHERE pid=pg_backend_pid() AND relation=$1 AND mode=$2 AND granted)",
            SpiParameter.Create(oid), SpiParameter.Create(mode));

    /// <summary>
    /// Borrows or adopts one reference supplied by a native caller whose separate keeper reference remains live.
    /// </summary>
    [PgFunction]
    public static unsafe uint Borrow(long address, int mode)
    {
        using PgRelation relation = mode == 1 ? PgRelation.DangerousAdopt((void*)(nint)address)! :
            PgRelation.DangerousBorrow((void*)(nint)address)!;
        if (mode == 2)
        {
            using PgRelation owner = relation.DangerousTakeOwnership();
            return owner.Oid;
        }

        return relation.Oid;
    }

    /// <summary>
    /// Measures all seven statistics helpers against the selected headers' actual backend-local counters.
    /// </summary>
    [PgFunction]
    public static unsafe long[] Counters(uint oid)
    {
        using PgRelation relation = PgRelation.Open(oid);
        long address = (long)relation.DangerousGetPointer();
        long[] before = Spi.ExecuteScalar<long[]>("SELECT tests.relation_stats($1)", SpiParameter.Create(address));
        long[] after = before;
        bool enabled = Spi.ExecuteScalar<bool>("SELECT current_setting('track_counts')::boolean");
        Check(relation.CountHeapScan, 0, 1);
        Check(relation.CountIndexScan, 0, 1);
        Check(relation.CountHeapGetNext, 1, 1);
        Check(relation.CountHeapFetch, 2, 1);
        Check(() => relation.CountIndexTuples(5_000_000_000), 1, 5_000_000_000);
        Check(() => relation.CountIndexTuples(-7), 1, -7);
        Check(relation.CountBufferRead, 3, 1);
        Check(relation.CountBufferHit, 4, 1);
        return [.. after.Zip(before, static (right, left) => right - left)];

        void Check(Action update, int counter, long amount)
        {
            update();
            long[] current = Spi.ExecuteScalar<long[]>("SELECT tests.relation_stats($1)", SpiParameter.Create(address));
            for (int index = 0; index < current.Length; index++)
            {
                long expected = after[index] + (enabled && index == counter ? amount : 0);
                if (current[index] != expected)
                {
                    throw new InvalidOperationException($"Statistics counter {index} changed to {current[index]} instead of {expected}.");
                }
            }

            after = current;
        }
    }
}

/// <summary>
/// Provides finite Native AOT metadata for copied relation descriptors.
/// </summary>
[JsonSerializable(typeof(PgTupleDescriptor))]
internal sealed partial class RelationJsonContext : JsonSerializerContext;

/// <summary>
/// Uses regclass transition state and relation arguments without retaining managed references between aggregate calls.
/// </summary>
[PgAggregate(Name = "last_relation")]
[PgSchema("relations")]
public sealed class LastRelationAggregate : IPgAggregate<PgRelation?, PgRelation?>
{
    /// <summary>
    /// Selects the last nonnull relation and transfers its OID to the native transition state.
    /// </summary>
    [PgFunction]
    public static PgRelation? Transition(PgAggregateContext context, PgRelation? state, PgRelation? value) => value ?? state;
}
