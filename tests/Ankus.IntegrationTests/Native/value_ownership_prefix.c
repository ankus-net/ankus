#include "postgres.h"
#include "miscadmin.h"
#include "access/xact.h"
#include "utils/catcache.h"
#include "utils/syscache.h"
#include "utils/lsyscache.h"
#include "utils/resowner.h"
#include "storage/lwlock.h"
static bool owner_armed;
static bool owner_invalidated;
static bool owner_cancel;
static HeapTuple owner_keeper;
static HeapTuple owner_obsolete;
static ResourceOwner owner_parent;
static int owner_lookups;
static LWLock owner_error_lock;
static bool owner_error_lock_initialized;

static CatCTup *owner_entry(HeapTuple tuple)
{
    return (CatCTup *) ((char *) tuple - offsetof(CatCTup, tuple));
}

static Oid owner_lookup(Oid type)
{
    owner_lookups++;
    if (!owner_armed)
        return getBaseType(type);

    owner_armed = false;
    ResourceOwner child = CurrentResourceOwner;
    if (owner_invalidated)
    {
        CurrentResourceOwner = owner_parent;
        owner_obsolete = SearchSysCache1(TYPEOID, ObjectIdGetDatum(type));
        CurrentResourceOwner = child;
    }

    ResetCatalogCaches();
    HeapTuple acquired = SearchSysCache1(TYPEOID, ObjectIdGetDatum(type));
    if (!HeapTupleIsValid(acquired))
        elog(ERROR, "ownership probe type does not exist");
    CurrentResourceOwner = owner_parent;
    owner_keeper = SearchSysCache1(TYPEOID, ObjectIdGetDatum(type));
    CurrentResourceOwner = child;
    if (owner_keeper != acquired || owner_entry(acquired)->refcount != 2)
        elog(ERROR, "ownership probe did not retain its exact catalog entry");
    if (owner_cancel)
    {
        QueryCancelPending = true;
        InterruptPending = true;
        CHECK_FOR_INTERRUPTS();
    }

    if (!owner_error_lock_initialized)
    {
        int tranche;
#if PG_VERSION_NUM >= 190000
        tranche = LWLockNewTrancheId("Ankus value ownership fixture");
#else
        tranche = LWLockNewTrancheId();
        LWLockRegisterTranche(tranche, "Ankus value ownership fixture");
#endif
        LWLockInitialize(&owner_error_lock, tranche);
        owner_error_lock_initialized = true;
    }

    LWLockAcquire(&owner_error_lock, LW_EXCLUSIVE);
    ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("controlled catalog ownership failure"),
        errdetail("a real syscache reference is still owned"), errhint("roll back before further backend work")));
    return InvalidOid;
}

#define getBaseType owner_lookup

#ifdef __APPLE__
/* The emitted bridge registers signal routing with the Native AOT runtime, which this fixture does not link. */
void RhSetThreadCreationCallback(void (*callback)(void));

void
RhSetThreadCreationCallback(void (*callback)(void))
{
    (void) callback;
}
#endif
