using System;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Holds a checked PostgreSQL relation cache reference with explicit native ownership and locking.
/// </summary>
/// <remarks>
/// Use on the owning backend thread and dispose before its PostgreSQL resource owner ends.
/// PostgreSQL also reclaims references during statement, portal, or transaction cleanup; later checked access then fails.
/// Metadata is read from the live relation. Copied names and tuple descriptors remain usable after disposal.
/// Garbage collection never calls PostgreSQL. A relation may describe any pg_class object, including an index or sequence.
/// </remarks>
public sealed class PgRelation : IDisposable
{
    private readonly nint _backend;
    private readonly bool _owned;

    /// <summary>
    /// Reserves the managed wrapper before acquiring a native reference.
    /// </summary>
    internal PgRelation(nint backend, bool owned)
    {
        _backend = backend;
        _owned = owned;
    }

    /// <summary>
    /// Gets or sets the checked native identity; zero means this wrapper has been consumed or disposed.
    /// </summary>
    internal long Identity
    {
        get;
        set;
    }

    /// <summary>
    /// Gets the exact relation OID, distinct from its row type OID.
    /// </summary>
    public uint Oid => Read<uint>(6);

    /// <summary>
    /// Gets a copy of the relation's current unqualified name.
    /// </summary>
    public string Name => Read<string>(7);

    /// <summary>
    /// Gets the current namespace OID.
    /// </summary>
    public uint NamespaceOid => Read<uint>(8);

    /// <summary>
    /// Gets a copy of the current namespace name.
    /// </summary>
    public string NamespaceName => Read<string>(9);

    /// <summary>
    /// Gets the exact PostgreSQL relkind character, including kinds without a convenience predicate.
    /// </summary>
    public char Kind => (char)Read<sbyte>(10);

    /// <summary>
    /// Gets the native reltuples estimate, mapping exactly zero to null as pgrx does and preserving minus one.
    /// </summary>
    public float? EstimatedTupleCount => Read<float?>(11);

    /// <summary>
    /// Gets whether this is an ordinary table, including an ordinary table used as a partition.
    /// </summary>
    public bool IsTable => Kind == 'r';

    /// <summary>
    /// Gets whether this is a materialized view.
    /// </summary>
    public bool IsMaterializedView => Kind == 'm';

    /// <summary>
    /// Gets whether this is an ordinary index. A partitioned index has kind I instead.
    /// </summary>
    public bool IsIndex => Kind == 'i';

    /// <summary>
    /// Gets whether this is a view.
    /// </summary>
    public bool IsView => Kind == 'v';

    /// <summary>
    /// Gets whether this is a sequence.
    /// </summary>
    public bool IsSequence => Kind == 'S';

    /// <summary>
    /// Gets whether this describes a standalone composite type.
    /// </summary>
    public bool IsCompositeType => Kind == 'c';

    /// <summary>
    /// Gets whether this is a foreign table.
    /// </summary>
    public bool IsForeignTable => Kind == 'f';

    /// <summary>
    /// Gets whether this is a partitioned table.
    /// </summary>
    public bool IsPartitionedTable => Kind == 'p';

    /// <summary>
    /// Gets whether this is a TOAST table.
    /// </summary>
    public bool IsToast => Kind == 't';

    /// <summary>
    /// Gets an owned copy of the actual relation descriptor, including physical dropped slots and index columns.
    /// A descriptor without a native row type retains a zero type OID and cannot construct typed values.
    /// </summary>
    public PgTupleDescriptor TupleDescriptor
    {
        get
        {
            CheckAccess();
            return NativeBackend.RelationDescriptor(Identity);
        }
    }

    /// <summary>
    /// Opens a relation by its exact OID and acquires the requested lock. Disposal releases this lock acquisition.
    /// Automatic resource cleanup follows PostgreSQL's transaction lock rules.
    /// </summary>
    /// <param name="oid">The relation OID; nonexistent OIDs raise a PostgreSQL error.</param>
    /// <param name="lockMode">The required lock, defaulting to AccessShare. None is not accepted.</param>
    /// <returns>An independently disposable relation reference.</returns>
    public static PgRelation Open(uint oid, PgLockMode lockMode = PgLockMode.AccessShare)
    {
        ValidateLock(lockMode);
        return NativeBackend.OpenRelation(1, oid, null, lockMode)!;
    }

    /// <summary>
    /// Resolves a relation using native regclass syntax and search-path rules, then opens and locks it.
    /// </summary>
    /// <param name="name">The PostgreSQL relation name or numeric OID syntax.</param>
    /// <param name="lockMode">The required lock, defaulting to AccessShare.</param>
    /// <returns>An independently disposable reference. Missing names raise a PostgreSQL error.</returns>
    public static PgRelation Open(string name, PgLockMode lockMode = PgLockMode.AccessShare)
    {
        ValidateLock(lockMode);
        NativeBackend.ValidateLookupName(name, nameof(name));
        return NativeBackend.OpenRelation(2, 0, name, lockMode)!;
    }

    /// <summary>
    /// Resolves and opens a relation, returning null when native to_regclass returns SQL NULL.
    /// </summary>
    /// <param name="name">The PostgreSQL relation name or numeric OID syntax.</param>
    /// <param name="lockMode">The required lock, defaulting to AccessShare.</param>
    /// <returns>The owned reference or null. Lock, permission, and concurrent-deletion errors still propagate.</returns>
    public static PgRelation? TryOpen(string name, PgLockMode lockMode = PgLockMode.AccessShare)
    {
        ValidateLock(lockMode);
        NativeBackend.ValidateLookupName(name, nameof(name));
        return NativeBackend.OpenRelation(2, 0, name, lockMode, missingOk: true);
    }

    /// <summary>
    /// Opens without acquiring or later releasing a lock. The caller must already hold at least AccessShare.
    /// </summary>
    /// <param name="oid">The relation OID protected by the caller's lock.</param>
    /// <returns>An owned cache reference whose disposal only closes that reference.</returns>
    public static PgRelation DangerousOpenWithoutLock(uint oid) => NativeBackend.OpenRelation(1, oid, null, PgLockMode.None)!;

    /// <summary>
    /// Resolves a name and opens without acquiring a lock. The caller must already protect the resolved relation.
    /// </summary>
    /// <param name="name">The PostgreSQL relation name or numeric OID syntax.</param>
    /// <returns>An owned cache reference, or null when native to_regclass returns SQL NULL.</returns>
    public static PgRelation? DangerousOpenWithoutLock(string name)
    {
        NativeBackend.ValidateLookupName(name, nameof(name));
        return NativeBackend.OpenRelation(2, 0, name, PgLockMode.None, missingOk: true);
    }

    /// <summary>
    /// Borrows a caller-proven live Relation pointer without incrementing or closing its native reference.
    /// </summary>
    /// <param name="address">A selected-version Relation pointer, or null.</param>
    /// <returns>A checked view, or null for a null pointer.</returns>
    /// <remarks>
    /// The caller must preserve the external reference and required locks until disposal or ownership transfer.
    /// Its resource owner must be the current PostgreSQL owner. Checks cannot detect an earlier external close.
    /// </remarks>
    public static unsafe PgRelation? DangerousBorrow(void* address)
        => address == null ? null : NativeBackend.OpenRelation(3, 0, null, PgLockMode.None, pointer: (nint)address);

    /// <summary>
    /// Adopts one caller-owned Relation reference without incrementing it; disposal calls RelationClose without unlocking.
    /// </summary>
    /// <param name="address">A selected-version Relation pointer, or null.</param>
    /// <returns>The exclusive managed owner, or null for a null pointer.</returns>
    /// <remarks>
    /// The caller transfers exactly one close obligation registered with the current PostgreSQL resource owner.
    /// It must hold appropriate locks and must not independently close or adopt that same reference again.
    /// Failed adoption leaves the external close obligation with the caller.
    /// </remarks>
    public static unsafe PgRelation? DangerousAdopt(void* address)
        => address == null ? null : NativeBackend.OpenRelation(4, 0, null, PgLockMode.None, pointer: (nint)address);

    /// <summary>
    /// Reopens the same OID with an independent AccessShare lock and cache reference.
    /// </summary>
    /// <returns>An independently disposable owner.</returns>
    public PgRelation Clone() => Open(Oid);

    /// <summary>
    /// Transfers an external borrowed close obligation into an owned wrapper without incrementing the reference.
    /// </summary>
    /// <returns>This object when already owned, or a new owner that consumes the borrowed wrapper.</returns>
    /// <remarks>
    /// For a borrowed view, the caller must exclusively transfer the external close obligation and never close it again.
    /// This operation does not acquire or release an external lock.
    /// A failed transfer leaves the borrowed wrapper and external close obligation unchanged.
    /// </remarks>
    public PgRelation DangerousTakeOwnership()
    {
        CheckAccess();
        if (_owned)
        {
            NativeBackend.UpdateRelation(Identity, 5);
            return this;
        }

        var result = new PgRelation(_backend, owned: true);
        NativeBackend.UpdateRelation(Identity, 5);
        result.Identity = Identity;
        Identity = 0;
        return result;
    }

    /// <summary>
    /// Opens the index's owning heap without locking it. The caller must already hold the heap's required lock.
    /// </summary>
    /// <returns>An independent owned heap reference, or null when this relation has no native index metadata.</returns>
    public PgRelation? DangerousOpenHeap()
    {
        uint oid = Read<uint>(14);
        return oid == 0 ? null : DangerousOpenWithoutLock(oid);
    }

    /// <summary>
    /// Eagerly opens every attached index, closing previously opened references if any later open fails.
    /// </summary>
    /// <param name="lockMode">The lock to acquire separately for each index.</param>
    /// <returns>A read-only list of independent owners; dispose every element when finished.</returns>
    public IReadOnlyList<PgRelation> GetIndices(PgLockMode lockMode = PgLockMode.AccessShare)
    {
        ValidateLock(lockMode);
        return OpenIndices(lockMode);
    }

    /// <summary>
    /// Opens every attached index without acquiring or releasing locks. The caller must already protect each index.
    /// </summary>
    /// <returns>Independent cache-reference owners; dispose every element when finished.</returns>
    public IReadOnlyList<PgRelation> DangerousGetIndicesWithoutLock() => OpenIndices(PgLockMode.None);

    private System.Collections.ObjectModel.ReadOnlyCollection<PgRelation> OpenIndices(PgLockMode lockMode)
    {
        uint[] oids = Read<uint[]>(13);
        var result = new PgRelation[oids.Length];
        int acquired = 0;
        try
        {
            for (; acquired < result.Length; acquired++)
            {
                result[acquired] = lockMode == PgLockMode.None ? DangerousOpenWithoutLock(oids[acquired]) : Open(oids[acquired], lockMode);
            }

            return Array.AsReadOnly(result);
        }
        catch (Exception primary)
        {
            NativeRelationScope.Release(result, primary);
            throw;
        }
    }

    /// <summary>
    /// Increments PostgreSQL's heap scan counter when relation statistics are enabled.
    /// </summary>
    public void CountHeapScan() => Update(16);

    /// <summary>
    /// Increments PostgreSQL's index scan counter when relation statistics are enabled.
    /// </summary>
    public void CountIndexScan() => Update(17);

    /// <summary>
    /// Counts one tuple returned by heap iteration when relation statistics are enabled.
    /// </summary>
    public void CountHeapGetNext() => Update(18);

    /// <summary>
    /// Counts one fetched heap tuple when relation statistics are enabled.
    /// </summary>
    public void CountHeapFetch() => Update(19);

    /// <summary>
    /// Adds a signed count to PostgreSQL's tuples-returned counter, preserving pgrx's native counter semantics.
    /// </summary>
    /// <param name="count">The signed tuple count.</param>
    public void CountIndexTuples(long count) => Update(20, count);

    /// <summary>
    /// Counts one buffer read when relation statistics are enabled.
    /// </summary>
    public void CountBufferRead() => Update(21);

    /// <summary>
    /// Counts one buffer hit when relation statistics are enabled.
    /// </summary>
    public void CountBufferHit() => Update(22);

    /// <summary>
    /// Copies the relation OID into an explicitly context-bound regclass datum without transferring this reference.
    /// </summary>
    /// <param name="context">The datum's lifetime anchor.</param>
    /// <returns>A checked, nonnull regclass datum with the exact relation identity.</returns>
    public PgDatum ToDatum(PgMemoryContext context) => PgDatum.DangerousCreate(Oid, 2205, context);

    /// <summary>
    /// Returns a borrowed selected-version Relation pointer after validating this handle.
    /// </summary>
    /// <returns>The native pointer, valid only while this reference and its required locks remain live.</returns>
    public unsafe void* DangerousGetPointer()
    {
        CheckAccess();
        return (void*)NativeBackend.RelationPointer(Identity);
    }

    /// <summary>
    /// Releases this wrapper and its owned reference and lock. Borrowed wrappers do not close the external reference.
    /// Repeated disposal and disposal after native resource-owner cleanup are harmless on the owning backend thread.
    /// </summary>
    public void Dispose()
    {
        if (Identity == 0)
        {
            return;
        }

        NativeBackend.CheckDisposalAccess(_backend);
        NativeBackend.CloseRelation(Identity);
        Identity = 0;
    }

    private T Read<T>(int operation)
    {
        CheckAccess();
        return NativeBackend.ReadRelation<T>(Identity, operation);
    }

    private void Update(int operation, long count = 0)
    {
        CheckAccess();
        NativeBackend.UpdateRelation(Identity, operation, count);
    }

    private void CheckAccess()
    {
        ObjectDisposedException.ThrowIf(Identity == 0, this);
        NativeBackend.CheckAccess(_backend);
    }

    private static void ValidateLock(PgLockMode mode)
    {
        if (mode is < PgLockMode.AccessShare or > PgLockMode.AccessExclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Select a PostgreSQL relation lock from AccessShare through AccessExclusive.");
        }
    }
}
