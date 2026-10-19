namespace Ankus.Generators;

/// <summary>
/// Emits exact field-wise conversion between tid datums and managed item-pointer values.
/// </summary>
internal static class NativeItemPointerTypes
{
    /// <summary>
    /// Gets conversions that take their layout from the selected PostgreSQL headers.
    /// </summary>
    internal const string Source = """
        #include "storage/itemptr.h"

        static void
        ankus_read_item_pointer(Datum datum, AnkusValue *value)
        {
            ItemPointer pointer = (ItemPointer) DatumGetPointer(datum);
            value->integral = ItemPointerGetBlockNumberNoCheck(pointer);
            value->auxiliary1 = ItemPointerGetOffsetNumberNoCheck(pointer);
        }

        static Datum
        ankus_write_item_pointer(const AnkusValue *value)
        {
            if (value->integral < 0 || value->integral > PG_UINT32_MAX ||
                value->auxiliary1 < 0 || value->auxiliary1 > PG_UINT16_MAX)
            {
                ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE),
                    errmsg("the item-pointer fields exceed PostgreSQL's native range")));
            }

            ItemPointer pointer = (ItemPointer) palloc(sizeof(ItemPointerData));
            ItemPointerSet(pointer, (BlockNumber) value->integral, (OffsetNumber) value->auxiliary1);
            return PointerGetDatum(pointer);
        }

        """;
}
