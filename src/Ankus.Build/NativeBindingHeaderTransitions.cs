using System.Text.RegularExpressions;

namespace Ankus.Build;

/// <summary>
/// Lists PostgreSQL helpers that some supported majors declare as functions and others define only as function-like
/// macros, so each helper is callable on every major that defines it.
/// </summary>
/// <remarks>
/// Each wrapper takes the prototype of the nearest major that declares the function with the macro's arity. The table
/// covers every such helper in the server headers of PostgreSQL 13.23, 14.24, 15.19, 16.15, 17.11, 18.6 and 19 RC1, and
/// every wrapper compiles against them with and without assertions.
/// </remarks>
internal static partial class NativeBindingHeaderTransitions
{
    private static readonly NativeBindingHeaderTransition[] s_transitions =
    [
        new("BITMAPLEN", 13, 17, "int", "int NATTS"),
        new("BackupHistoryFileName", 13, 15, "void", "char *fname, TimeLineID tli, XLogSegNo logSegNo, XLogRecPtr startpoint, int wal_segsz_bytes"),
        new("BackupHistoryFilePath", 13, 15, "void", "char *path, TimeLineID tli, XLogSegNo logSegNo, XLogRecPtr startpoint, int wal_segsz_bytes"),
        new("BlockIdEquals", 13, 15, "bool", "const BlockIdData *blockId1, const BlockIdData *blockId2"),
        new("BlockIdGetBlockNumber", 13, 15, "BlockNumber", "const BlockIdData *blockId"),
        new("BlockIdSet", 13, 15, "void", "BlockIdData *blockId, BlockNumber blockNumber"),
        new("BlockNumberIsValid", 13, 15, "bool", "BlockNumber blockNumber"),
        new("BoolGetDatum", 13, 15, "Datum", "bool X"),
        new("BoxPGetDatum", 13, 15, "Datum", "const BOX *X"),
        new("BufMappingPartitionLock", 13, 15, "LWLock *", "uint32 hashcode"),
        new("BufMappingPartitionLockByIndex", 13, 15, "LWLock *", "uint32 index"),
        new("BufTableHashPartition", 13, 15, "uint32", "uint32 hashcode"),
        new("BufferDescriptorGetBuffer", 13, 15, "Buffer", "const BufferDesc *bdesc"),
        new("BufferDescriptorGetContentLock", 13, 15, "LWLock *", "const BufferDesc *bdesc"),
        new("BufferDescriptorGetIOCV", 14, 15, "ConditionVariable *", "const BufferDesc *bdesc"),
        new("CStringGetDatum", 13, 15, "Datum", "const char *X"),
        new("CharGetDatum", 13, 15, "Datum", "char X"),
        new("CirclePGetDatum", 13, 15, "Datum", "const CIRCLE *X"),
        new("CommandIdGetDatum", 13, 15, "Datum", "CommandId X"),
        new("DateADTGetDatum", 13, 15, "Datum", "DateADT X"),
        new("DatumGetBool", 13, 15, "bool", "Datum X"),
        new("DatumGetBoxP", 13, 15, "BOX *", "Datum X"),
        new("DatumGetCString", 13, 15, "char *", "Datum X"),
        new("DatumGetChar", 13, 15, "char", "Datum X"),
        new("DatumGetCircleP", 13, 15, "CIRCLE *", "Datum X"),
        new("DatumGetCommandId", 13, 15, "CommandId", "Datum X"),
        new("DatumGetDateADT", 13, 15, "DateADT", "Datum X"),
        new("DatumGetGinTernaryValue", 13, 15, "GinTernaryValue", "Datum X"),
        new("DatumGetInt16", 13, 15, "int16", "Datum X"),
        new("DatumGetInt32", 13, 15, "int32", "Datum X"),
        new("DatumGetInt64", 13, 15, "int64", "Datum X"),
        new("DatumGetIntervalP", 13, 15, "Interval *", "Datum X"),
        new("DatumGetJsonbP", 13, 15, "Jsonb *", "Datum d"),
        new("DatumGetJsonbPCopy", 13, 15, "Jsonb *", "Datum d"),
        new("DatumGetLineP", 13, 15, "LINE *", "Datum X"),
        new("DatumGetLsegP", 13, 15, "LSEG *", "Datum X"),
        new("DatumGetName", 13, 15, "Name", "Datum X"),
        new("DatumGetNumeric", 13, 15, "Numeric", "Datum X"),
        new("DatumGetNumericCopy", 13, 15, "Numeric", "Datum X"),
        new("DatumGetObjectId", 13, 15, "Oid", "Datum X"),
        new("DatumGetPathP", 13, 15, "PATH *", "Datum X"),
        new("DatumGetPathPCopy", 13, 15, "PATH *", "Datum X"),
        new("DatumGetPointP", 13, 15, "Point *", "Datum X"),
        new("DatumGetPointer", 13, 15, "Pointer", "Datum X"),
        new("DatumGetPolygonP", 13, 15, "POLYGON *", "Datum X"),
        new("DatumGetPolygonPCopy", 13, 15, "POLYGON *", "Datum X"),
        new("DatumGetRangeTypeP", 13, 15, "RangeType *", "Datum X"),
        new("DatumGetRangeTypePCopy", 13, 15, "RangeType *", "Datum X"),
        new("DatumGetTSQuery", 13, 15, "TSQuery", "Datum X"),
        new("DatumGetTSQueryCopy", 13, 15, "TSQuery", "Datum X"),
        new("DatumGetTSQuerySign", 13, 15, "TSQuerySign", "Datum X"),
        new("DatumGetTSVector", 13, 15, "TSVector", "Datum X"),
        new("DatumGetTSVectorCopy", 13, 15, "TSVector", "Datum X"),
        new("DatumGetTimeADT", 13, 15, "TimeADT", "Datum X"),
        new("DatumGetTimeTzADTP", 13, 15, "TimeTzADT *", "Datum X"),
        new("DatumGetTimestamp", 13, 15, "Timestamp", "Datum X"),
        new("DatumGetTimestampTz", 13, 15, "TimestampTz", "Datum X"),
        new("DatumGetTransactionId", 13, 15, "TransactionId", "Datum X"),
        new("DatumGetUInt16", 13, 15, "uint16", "Datum X"),
        new("DatumGetUInt32", 13, 15, "uint32", "Datum X"),
        new("DatumGetUInt64", 13, 15, "uint64", "Datum X"),
        new("DatumGetUInt8", 13, 15, "uint8", "Datum X"),
        new("EOHPGetRODatum", 13, 15, "Datum", "const struct ExpandedObjectHeader *eohptr"),
        new("EOHPGetRWDatum", 13, 15, "Datum", "const struct ExpandedObjectHeader *eohptr"),
        new("EmitWarningsOnPlaceholders", 15, 19, "void", "const char *className"),
        new("ExpandedRecordGetDatum", 13, 15, "Datum", "const ExpandedRecordHeader *erh"),
        new("ExpandedRecordGetRODatum", 13, 15, "Datum", "const ExpandedRecordHeader *erh"),
        new("FPeq", 13, 13, "bool", "double A, double B"),
        new("FPge", 13, 13, "bool", "double A, double B"),
        new("FPgt", 13, 13, "bool", "double A, double B"),
        new("FPle", 13, 13, "bool", "double A, double B"),
        new("FPlt", 13, 13, "bool", "double A, double B"),
        new("FPne", 13, 13, "bool", "double A, double B"),
        new("GetBufferDescriptor", 13, 15, "BufferDesc *", "uint32 id"),
        new("GetLocalBufferDescriptor", 13, 15, "BufferDesc *", "uint32 id"),
        new("GinPageIsRecyclable", 13, 13, "bool", "Page page"),
        new("GinTernaryValueGetDatum", 13, 15, "Datum", "GinTernaryValue X"),
        new("HEAP_LOCKED_UPGRADED", 13, 17, "bool", "uint16 infomask"),
        new("HEAP_XMAX_IS_EXCL_LOCKED", 13, 17, "bool", "int16 infomask"),
        new("HEAP_XMAX_IS_KEYSHR_LOCKED", 13, 17, "bool", "int16 infomask"),
        new("HEAP_XMAX_IS_LOCKED_ONLY", 13, 17, "bool", "uint16 infomask"),
        new("HEAP_XMAX_IS_SHR_LOCKED", 13, 17, "bool", "int16 infomask"),
        new("HeapKeyTest", 13, 15, "bool", "HeapTuple tuple, TupleDesc tupdesc, int nkeys, ScanKey keys", "bool result; HeapKeyTest(tuple, tupdesc, nkeys, keys, result); return result;"),
        new("HeapTupleAllFixed", 13, 17, "bool", "const HeapTupleData *tuple"),
        new("HeapTupleClearHeapOnly", 13, 17, "void", "const HeapTupleData *tuple"),
        new("HeapTupleClearHotUpdated", 13, 17, "void", "const HeapTupleData *tuple"),
        new("HeapTupleGetDatum", 13, 15, "Datum", "const HeapTupleData *tuple"),
        new("HeapTupleHasExternal", 13, 17, "bool", "const HeapTupleData *tuple"),
        new("HeapTupleHasNulls", 13, 17, "bool", "const HeapTupleData *tuple"),
        new("HeapTupleHasVarWidth", 13, 17, "bool", "const HeapTupleData *tuple"),
        new("HeapTupleHeaderClearHeapOnly", 13, 17, "void", "HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderClearHotUpdated", 13, 17, "void", "HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderClearMatch", 13, 17, "void", "MinimalTupleData *tup"),
        new("HeapTupleHeaderGetDatumLength", 13, 17, "uint32", "const HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderGetRawXmax", 13, 17, "TransactionId", "const HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderGetSpeculativeToken", 13, 17, "BlockNumber", "const HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderGetTypMod", 13, 17, "int32", "const HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderGetTypeId", 13, 17, "Oid", "const HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderGetUpdateXid", 13, 17, "TransactionId", "const HeapTupleHeaderData *tup", "return HeapTupleHeaderGetUpdateXid((HeapTupleHeader) tup);"),
        new("HeapTupleHeaderGetXvac", 13, 17, "TransactionId", "const HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderHasMatch", 13, 17, "bool", "const MinimalTupleData *tup"),
        new("HeapTupleHeaderIndicatesMovedPartitions", 13, 17, "bool", "const HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderIsSpeculative", 13, 17, "bool", "const HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderSetCmax", 13, 17, "void", "HeapTupleHeaderData *tup, CommandId cid, bool iscombo"),
        new("HeapTupleHeaderSetCmin", 13, 17, "void", "HeapTupleHeaderData *tup, CommandId cid"),
        new("HeapTupleHeaderSetDatumLength", 13, 17, "void", "HeapTupleHeaderData *tup, uint32 len"),
        new("HeapTupleHeaderSetHeapOnly", 13, 17, "void", "HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderSetHotUpdated", 13, 17, "void", "HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderSetMatch", 13, 17, "void", "MinimalTupleData *tup"),
        new("HeapTupleHeaderSetMovedPartitions", 13, 17, "void", "HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderSetSpeculativeToken", 13, 17, "void", "HeapTupleHeaderData *tup, BlockNumber token"),
        new("HeapTupleHeaderSetTypMod", 13, 17, "void", "HeapTupleHeaderData *tup, int32 typmod"),
        new("HeapTupleHeaderSetTypeId", 13, 17, "void", "HeapTupleHeaderData *tup, Oid datum_typeid"),
        new("HeapTupleHeaderSetXmax", 13, 17, "void", "HeapTupleHeaderData *tup, TransactionId xid"),
        new("HeapTupleHeaderSetXmin", 13, 17, "void", "HeapTupleHeaderData *tup, TransactionId xid"),
        new("HeapTupleHeaderSetXminCommitted", 13, 17, "void", "HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderSetXminFrozen", 13, 17, "void", "HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderSetXminInvalid", 13, 17, "void", "HeapTupleHeaderData *tup"),
        new("HeapTupleHeaderSetXvac", 13, 17, "void", "HeapTupleHeaderData *tup, TransactionId xid"),
        new("HeapTupleHeaderXminCommitted", 13, 17, "bool", "const HeapTupleHeaderData *tup"),
        new("HeapTupleIsHeapOnly", 13, 17, "bool", "const HeapTupleData *tuple"),
        new("HeapTupleIsHotUpdated", 13, 17, "bool", "const HeapTupleData *tuple"),
        new("HeapTupleSetHeapOnly", 13, 17, "void", "const HeapTupleData *tuple"),
        new("HeapTupleSetHotUpdated", 13, 17, "void", "const HeapTupleData *tuple"),
        new("IndexInfoFindDataOffset", 13, 15, "Size", "unsigned short t_info"),
        new("IndexTupleHasNulls", 13, 17, "bool", "const IndexTupleData *itup"),
        new("IndexTupleHasVarwidths", 13, 17, "bool", "const IndexTupleData *itup"),
        new("IndexTupleSize", 13, 17, "Size", "const IndexTupleData *itup"),
        new("Int16GetDatum", 13, 15, "Datum", "int16 X"),
        new("Int32GetDatum", 13, 15, "Datum", "int32 X"),
        new("Int64GetDatum", 13, 15, "Datum", "int64 X"),
        new("Int8GetDatum", 13, 15, "Datum", "int8 X"),
        new("IntervalPGetDatum", 13, 15, "Datum", "const Interval *X"),
        new("IsBackupHistoryFileName", 13, 15, "bool", "const char *fname"),
        new("IsPartialXLogFileName", 13, 15, "bool", "const char *fname"),
        new("IsTLHistoryFileName", 13, 15, "bool", "const char *fname"),
        new("IsXLogFileName", 13, 15, "bool", "const char *fname"),
        new("ItemPointerCopy", 13, 15, "void", "const ItemPointerData *fromPointer, ItemPointerData *toPointer"),
        new("ItemPointerGetBlockNumber", 13, 15, "BlockNumber", "const ItemPointerData *pointer"),
        new("ItemPointerGetBlockNumberNoCheck", 13, 15, "BlockNumber", "const ItemPointerData *pointer"),
        new("ItemPointerGetOffsetNumber", 13, 15, "OffsetNumber", "const ItemPointerData *pointer"),
        new("ItemPointerGetOffsetNumberNoCheck", 13, 15, "OffsetNumber", "const ItemPointerData *pointer"),
        new("ItemPointerIndicatesMovedPartitions", 13, 15, "bool", "const ItemPointerData *pointer"),
        new("ItemPointerIsValid", 13, 15, "bool", "const ItemPointerData *pointer"),
        new("ItemPointerSet", 13, 15, "void", "ItemPointerData *pointer, BlockNumber blockNumber, OffsetNumber offNum"),
        new("ItemPointerSetBlockNumber", 13, 15, "void", "ItemPointerData *pointer, BlockNumber blockNumber"),
        new("ItemPointerSetInvalid", 13, 15, "void", "ItemPointerData *pointer"),
        new("ItemPointerSetMovedPartitions", 13, 15, "void", "ItemPointerData *pointer"),
        new("ItemPointerSetOffsetNumber", 13, 15, "void", "ItemPointerData *pointer, OffsetNumber offsetNumber"),
        new("JsonbPGetDatum", 13, 15, "Datum", "const Jsonb *p"),
        new("LinePGetDatum", 13, 15, "Datum", "const LINE *X"),
        new("LsegPGetDatum", 13, 15, "Datum", "const LSEG *X"),
        new("MultiXactIdGetDatum", 13, 15, "Datum", "MultiXactId X"),
        new("NameGetDatum", 13, 15, "Datum", "const NameData *X"),
        new("NumericGetDatum", 13, 15, "Datum", "Numeric X"),
        new("ObjectIdGetDatum", 13, 15, "Datum", "Oid X"),
        new("PageClearAllVisible", 13, 15, "void", "Page page"),
        new("PageClearFull", 13, 15, "void", "Page page"),
        new("PageClearHasFreeLinePointers", 13, 15, "void", "Page page"),
        new("PageGetLSN", 13, 15, "XLogRecPtr", "Page page"),
        new("PageHasFreeLinePointers", 13, 15, "bool", "Page page"),
        new("PageIsAllVisible", 13, 15, "bool", "Page page"),
        new("PageIsFull", 13, 15, "bool", "Page page"),
        new("PageIsVerified", 14, 17, "bool", "Page page, BlockNumber blkno"),
        new("PageSetAllVisible", 13, 15, "void", "Page page"),
        new("PageSetFull", 13, 15, "void", "Page page"),
        new("PageSetHasFreeLinePointers", 13, 15, "void", "Page page"),
        new("PageSetLSN", 13, 15, "void", "Page page, XLogRecPtr lsn"),
        new("PageXLogRecPtrGet", 13, 15, "XLogRecPtr", "PageXLogRecPtr val"),
        new("PageXLogRecPtrSet", 13, 18, "void", "volatile PageXLogRecPtr *ptr, XLogRecPtr lsn", "PageXLogRecPtrSet(*ptr, lsn);"),
        new("PathPGetDatum", 13, 15, "Datum", "const PATH *X"),
        new("PointPGetDatum", 13, 15, "Datum", "const Point *X"),
        new("PointerGetDatum", 13, 15, "Datum", "const void *X"),
        new("PointerGetDatum", 19, 19, "Datum", "const void *X"),
        new("PolygonPGetDatum", 13, 15, "Datum", "const POLYGON *X"),
        new("RangeTypePGetDatum", 13, 15, "Datum", "const RangeType *X"),
        new("RelationCloseSmgr", 13, 15, "void", "Relation relation"),
        new("SET_VARSIZE", 13, 18, "void", "void *PTR, Size len"),
        new("SET_VARSIZE_COMPRESSED", 13, 18, "void", "void *PTR, Size len"),
        new("SET_VARSIZE_SHORT", 13, 18, "void", "void *PTR, Size len"),
        new("SET_VARTAG_EXTERNAL", 13, 18, "void", "void *PTR, vartag_external tag"),
        new("StatusFilePath", 13, 15, "void", "char *path, const char *xlog, const char *suffix"),
        new("TLHistoryFileName", 13, 15, "void", "char *fname, TimeLineID tli"),
        new("TLHistoryFilePath", 13, 15, "void", "char *path, TimeLineID tli"),
        new("TSQueryGetDatum", 13, 15, "Datum", "const TSQueryData *X"),
        new("TSQuerySignGetDatum", 13, 15, "Datum", "TSQuerySign X"),
        new("TSVectorGetDatum", 13, 15, "Datum", "const TSVectorData *X"),
        new("TimeADTGetDatum", 13, 15, "Datum", "TimeADT X"),
        new("TimeTzADTPGetDatum", 13, 15, "Datum", "const TimeTzADT *X"),
        new("TimestampGetDatum", 13, 15, "Datum", "Timestamp X"),
        new("TimestampTzGetDatum", 13, 15, "Datum", "TimestampTz X"),
        new("TransactionIdGetDatum", 13, 15, "Datum", "TransactionId X"),
        new("TupleDescAttr", 13, 17, "FormData_pg_attribute *", "TupleDesc tupdesc, int i"),
        new("UInt16GetDatum", 13, 15, "Datum", "uint16 X"),
        new("UInt32GetDatum", 13, 15, "Datum", "uint32 X"),
        new("UInt64GetDatum", 13, 15, "Datum", "uint64 X"),
        new("UInt8GetDatum", 13, 15, "Datum", "uint8 X"),
        new("UnlockBufHdr", 13, 15, "void", "BufferDesc *desc, uint32 buf_state"),
        new("VARATT_CAN_MAKE_SHORT", 13, 18, "bool", "const void *PTR"),
        new("VARATT_CONVERTED_SHORT_SIZE", 13, 18, "Size", "const void *PTR"),
        new("VARATT_EXTERNAL_GET_COMPRESS_METHOD", 14, 18, "uint32", "varatt_external toast_pointer"),
        new("VARATT_EXTERNAL_GET_EXTSIZE", 14, 18, "Size", "varatt_external toast_pointer"),
        new("VARATT_EXTERNAL_IS_COMPRESSED", 13, 13, "bool", "varatt_external toast_pointer"),
        new("VARATT_EXTERNAL_IS_COMPRESSED", 14, 18, "bool", "varatt_external toast_pointer", "return VARATT_EXTERNAL_GET_EXTSIZE(toast_pointer) < (Size) (toast_pointer.va_rawsize - VARHDRSZ);"),
        new("VARATT_IS_COMPRESSED", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_EXTENDED", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_EXTERNAL", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_EXTERNAL_EXPANDED", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_EXTERNAL_EXPANDED_RO", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_EXTERNAL_EXPANDED_RW", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_EXTERNAL_INDIRECT", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_EXTERNAL_NON_EXPANDED", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_EXTERNAL_ONDISK", 13, 18, "bool", "const void *PTR"),
        new("VARATT_IS_SHORT", 13, 18, "bool", "const void *PTR"),
        new("VARDATA", 13, 18, "char *", "const void *PTR"),
        new("VARDATA_ANY", 13, 18, "char *", "const void *PTR"),
        new("VARDATA_COMPRESSED_GET_COMPRESS_METHOD", 14, 18, "uint32", "const void *PTR"),
        new("VARDATA_COMPRESSED_GET_EXTSIZE", 14, 18, "Size", "const void *PTR"),
        new("VARDATA_EXTERNAL", 13, 18, "char *", "const void *PTR"),
        new("VARDATA_SHORT", 13, 18, "char *", "const void *PTR"),
        new("VARSIZE", 13, 18, "Size", "const void *PTR"),
        new("VARSIZE_ANY", 13, 18, "Size", "const void *PTR"),
        new("VARSIZE_ANY_EXHDR", 13, 18, "Size", "const void *PTR"),
        new("VARSIZE_EXTERNAL", 13, 18, "Size", "const void *PTR"),
        new("VARSIZE_SHORT", 13, 18, "Size", "const void *PTR"),
        new("VARTAG_EXTERNAL", 13, 18, "vartag_external", "const void *PTR"),
        new("VARTAG_IS_EXPANDED", 13, 18, "bool", "vartag_external tag"),
        new("VARTAG_SIZE", 13, 18, "Size", "vartag_external tag"),
        new("WalSndWakeupProcessRequests", 13, 15, "void", "void"),
        new("XLogFileName", 13, 15, "void", "char *fname, TimeLineID tli, XLogSegNo logSegNo, int wal_segsz_bytes"),
        new("XLogFileNameById", 13, 15, "void", "char *fname, TimeLineID tli, uint32 log, uint32 seg"),
        new("XLogFilePath", 13, 15, "void", "char *path, TimeLineID tli, XLogSegNo logSegNo, int wal_segsz_bytes"),
        new("XLogFromFileName", 13, 15, "void", "const char *fname, TimeLineID *tli, XLogSegNo *logSegNo, int wal_segsz_bytes"),
        new("att_isnull", 13, 15, "bool", "int ATT, const bits8 *BITS"),
        new("bms_is_empty", 16, 19, "bool", "const Bitmapset *a"),
        new("dsa_create", 17, 19, "dsa_area *", "int tranche_id"),
        new("dsa_create_in_place", 17, 19, "dsa_area *", "void *place, size_t size, int tranche_id, dsm_segment *segment"),
        new("dshash_find_or_insert", 19, 19, "void *", "dshash_table *hash_table, const void *key, bool *found"),
        new("expression_tree_mutator", 16, 19, "Node *", "Node *node, Node *(*mutator) (), void *context"),
        new("fastgetattr", 13, 14, "Datum", "HeapTuple tup, int attnum, TupleDesc tupleDesc, bool *isnull"),
        new("fetch_att", 13, 15, "Datum", "const void *T, bool attbyval, int attlen"),
        new("geqo_randint", 13, 14, "int", "PlannerInfo *root, int upper, int lower"),
        new("index_getattr", 13, 15, "Datum", "IndexTuple tup, int attnum, TupleDesc tupleDesc, bool *isnull"),
        new("list_make_int_cell", 13, 18, "ListCell", "int v"),
        new("list_make_oid_cell", 13, 18, "ListCell", "Oid v"),
        new("list_make_ptr_cell", 13, 18, "ListCell", "void *v"),
        new("list_make_xid_cell", 16, 18, "ListCell", "TransactionId v"),
        new("newNode", 13, 16, "Node *", "size_t size, NodeTag tag"),
        new("query_or_expression_tree_mutator", 16, 19, "Node *", "Node *node, Node *(*mutator) (), void *context, int flags"),
        new("query_tree_mutator", 16, 19, "Query *", "Query *query, Node *(*mutator) (), void *context, int flags"),
        new("range_table_mutator", 16, 19, "List *", "List *rtable, Node *(*mutator) (), void *context, int flags"),
        new("store_att_byval", 13, 15, "void", "void *T, Datum newdatum, int attlen"),
    ];

    /// <summary>
    /// Gets every wrapped helper with the majors that define it only as a macro.
    /// </summary>
    internal static IReadOnlyList<NativeBindingHeaderTransition> All { get; } = Array.AsReadOnly(s_transitions);

    /// <summary>
    /// Lists the wrappers the selected major needs.
    /// </summary>
    /// <param name="major">The selected PostgreSQL major.</param>
    /// <returns>Helpers that the major defines only as macros, in name order.</returns>
    internal static IEnumerable<NativeBindingHeaderTransition> Read(int major)
        => s_transitions.Where(transition => major >= transition.First && major <= transition.Last);

    /// <summary>
    /// Calls the macro with the wrapper's parameters in order.
    /// </summary>
    /// <param name="transition">The helper being wrapped.</param>
    /// <returns>The wrapper body.</returns>
    internal static string Body(NativeBindingHeaderTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
        if (transition.Body is not null)
        {
            return transition.Body;
        }

        string arguments = transition.Parameters == "void" ? string.Empty : string.Join(", ", Split(transition.Parameters).Select(Name));
        string call = transition.Name + "(" + arguments + ")";
        return transition.Result == "void" ? call + ";" : "return " + call + ";";
    }

    private static IEnumerable<string> Split(string parameters)
    {
        int depth = 0;
        int start = 0;
        for (int index = 0; index < parameters.Length; index++)
        {
            char value = parameters[index];
            if (value == '(')
            {
                depth++;
            }
            else if (value == ')')
            {
                depth--;
            }
            else if (value == ',' && depth == 0)
            {
                yield return parameters[start..index].Trim();
                start = index + 1;
            }
        }

        yield return parameters[start..].Trim();
    }

    private static string Name(string parameter)
    {
        Match pointer = FunctionPointerName().Match(parameter);
        return pointer.Success ? pointer.Groups["name"].Value : TrailingName().Match(parameter).Groups["name"].Value;
    }

    [GeneratedRegex(@"\(\s*\*\s*(?<name>\w+)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex FunctionPointerName();

    [GeneratedRegex(@"(?<name>\w+)\s*(\[[^\]]*\])?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingName();
}

/// <summary>
/// Retains one helper's wrapped majors and the C prototype its wrapper declares.
/// </summary>
/// <param name="Name">The PostgreSQL helper name.</param>
/// <param name="First">The first major that defines the helper only as a macro.</param>
/// <param name="Last">The last major in that contiguous range.</param>
/// <param name="Result">The C result type.</param>
/// <param name="Parameters">The C parameter list, or <c>void</c>.</param>
/// <param name="Body">An explicit wrapper body when calling the macro with the parameters does not match the function.</param>
internal sealed record NativeBindingHeaderTransition(string Name, int First, int Last, string Result, string Parameters, string? Body = null);
