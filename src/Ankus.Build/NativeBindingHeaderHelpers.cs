using System.Text;

namespace Ankus.Build;

/// <summary>
/// Supplies addressable selected-header contracts for native helpers absent from a major's pgrx foreign inventory.
/// </summary>
internal static class NativeBindingHeaderHelpers
{
    /// <summary>
    /// Gets native storage used by helpers whose public prototypes expose only an untyped page address.
    /// </summary>
    internal static IReadOnlyList<string> RequiredTypes { get; } = Array.AsReadOnly<string>(["PageHeaderData"]);

    /// <summary>
    /// Lists the supported major's native helpers without replacing existing foreign declarations.
    /// </summary>
    /// <param name="major">The selected PostgreSQL major.</param>
    /// <returns>Native declarations and explicit macro wrappers whose types are measured by the compiler.</returns>
    internal static IReadOnlyList<NativeBindingHeaderHelper> Read(int major)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 13);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(major, 19);
        var helpers = new List<NativeBindingHeaderHelper>
        {
            Direct("pg_add_s32_overflow"),
            Macro("GETSTRUCT", "char *", "HeapTuple value", "return GETSTRUCT(value);"),
            Macro("TYPEALIGN", "uintptr_t", "uintptr_t alignment, uintptr_t value", "return TYPEALIGN(alignment, value);"),
            Macro("MAXALIGN", "uintptr_t", "uintptr_t value", "return MAXALIGN(value);"),
            Direct("GetMemoryChunkContext"),
            Macro("MemoryContextIsValid", "bool", "MemoryContext value", "return MemoryContextIsValid(value);"),
            Direct("MemoryContextSwitchTo"),
            Macro("TransactionIdIsNormal", "bool", "TransactionId value", "return TransactionIdIsNormal(value);"),
            Direct("TransactionIdPrecedes"),
            Direct("TransactionIdPrecedesOrEquals"),
            Direct("TransactionIdFollows"),
            Direct("TransactionIdFollowsOrEquals"),
            Macro("type_is_array", "bool", "Oid value", "return type_is_array(value);"),
            Macro("BufferIsLocal", "bool", "Buffer value", "return BufferIsLocal(value);"),
            major < 16
                ? Macro("BufferIsValid", "bool", "Buffer value", "return BufferIsValid(value);")
                : Direct("BufferIsValid"),
            Macro("ItemIdGetOffset", "unsigned int", "const ItemIdData *value", "return ItemIdGetOffset(value);"),
            // PostgreSQL removed these simple macros in newer majors; pgrx retains their predicates.
            Macro("PageIsValid", "bool", major < 18 ? "Page value" : "const PageData *value", "return value != NULL;"),
            Macro("PageSizeIsValid", "bool", "Size value", "return value == BLCKSZ;"),
            Macro("SizeOfPageHeaderData", "Size", "void", "return SizeOfPageHeaderData;"),
            Direct("PageValidateSpecialPointer"),
            Macro("HeapTupleHeaderGetNatts", "uint16", "const HeapTupleHeaderData *value", "return HeapTupleHeaderGetNatts(value);"),
            major < 15
                ? Macro("heap_getattr", "Datum", "HeapTuple value, int attribute, TupleDesc descriptor, bool *is_null", "return heap_getattr(value, attribute, descriptor, is_null);")
                : Direct("heap_getattr"),
        };
        if (major < 19)
        {
            helpers.AddRange([
                Macro("SpinLockInit", "void", "volatile slock_t *value", "SpinLockInit(value);"),
                Macro("SpinLockAcquire", "void", "volatile slock_t *value", "SpinLockAcquire(value);"),
                Macro("SpinLockRelease", "void", "volatile slock_t *value", "SpinLockRelease(value);"),
                Macro("SpinLockFree", "bool", "slock_t *value", "return SpinLockFree(value);"),
            ]);
        }
        else
        {
            helpers.AddRange([Direct("SpinLockInit"), Direct("SpinLockAcquire"), Direct("SpinLockRelease")]);
        }

        helpers.AddRange(major < 16 ?
        [
            Macro("BufferGetBlock", "Block", "Buffer value", "return BufferGetBlock(value);"),
            Macro("BufferGetPage", "Page", "Buffer value", "return BufferGetPage(value);"),
            // The macro only reads its argument inside AssertMacro, which disappears in release servers.
            Macro("BufferGetPageSize", "Size", "Buffer value", "(void)value; return BufferGetPageSize(value);"),
            Macro("PageIsEmpty", "bool", "Page value", "return PageIsEmpty(value);"),
            Macro("PageIsNew", "bool", "Page value", "return PageIsNew(value);"),
            Macro("PageGetItemId", "ItemId", "Page value, OffsetNumber offset", "return PageGetItemId(value, offset);"),
            Macro("PageGetContents", "char *", "Page value", "return PageGetContents(value);"),
            Macro("PageGetPageSize", "Size", "Page value", "return PageGetPageSize(value);"),
            Macro("PageGetPageLayoutVersion", "uint8", "Page value", "return PageGetPageLayoutVersion(value);"),
            Macro("PageSetPageSizeAndVersion", "void", "Page value, Size size, uint8 version", "PageSetPageSizeAndVersion(value, size, version);"),
            Macro("PageGetSpecialSize", "uint16", "Page value", "return PageGetSpecialSize(value);"),
            Macro("PageGetItem", "Item", "Page value, ItemId item", "return PageGetItem(value, item);"),
            Macro("PageGetMaxOffsetNumber", "OffsetNumber", "Page value", "return PageGetMaxOffsetNumber(value);"),
        ] :
        [
            Direct("BufferGetBlock"), Direct("BufferGetPage"), Direct("BufferGetPageSize"),
            Direct("PageIsEmpty"), Direct("PageIsNew"), Direct("PageGetItemId"), Direct("PageGetContents"),
            Direct("PageGetPageSize"), Direct("PageGetPageLayoutVersion"), Direct("PageSetPageSizeAndVersion"),
            Direct("PageGetSpecialSize"), Direct("PageGetItem"), Direct("PageGetMaxOffsetNumber"),
        ]);
        helpers.Add(major is 16 or 17 ? Direct("PageGetSpecialPointer")
            : Macro("PageGetSpecialPointer", "char *", "Page value", "return PageGetSpecialPointer(value);"));
        helpers.AddRange(major < 18 ?
        [
            Macro("HeapTupleHeaderIsHeapOnly", "bool", "const HeapTupleHeaderData *value", "return HeapTupleHeaderIsHeapOnly(value);"),
            Macro("HeapTupleHeaderIsHotUpdated", "bool", "const HeapTupleHeaderData *value", "return HeapTupleHeaderIsHotUpdated(value);"),
            Macro("HeapTupleHeaderXminInvalid", "bool", "const HeapTupleHeaderData *value", "return HeapTupleHeaderXminInvalid(value);"),
            Macro("HeapTupleHeaderXminFrozen", "bool", "const HeapTupleHeaderData *value", "return HeapTupleHeaderXminFrozen(value);"),
            Macro("HeapTupleHeaderGetRawCommandId", "CommandId", "const HeapTupleHeaderData *value", "return HeapTupleHeaderGetRawCommandId(value);"),
            Macro("HeapTupleHeaderGetRawXmin", "TransactionId", "const HeapTupleHeaderData *value", "return HeapTupleHeaderGetRawXmin(value);"),
            Macro("HeapTupleHeaderGetXmin", "TransactionId", "const HeapTupleHeaderData *value", "return HeapTupleHeaderGetXmin(value);"),
            Macro("HeapTupleNoNulls", "bool", "const HeapTupleData *value", "return HeapTupleNoNulls(value);"),
        ] :
        [
            Direct("HeapTupleHeaderIsHeapOnly"), Direct("HeapTupleHeaderIsHotUpdated"), Direct("HeapTupleHeaderXminInvalid"),
            Direct("HeapTupleHeaderXminFrozen"), Direct("HeapTupleHeaderGetRawCommandId"), Direct("HeapTupleHeaderGetRawXmin"),
            Direct("HeapTupleHeaderGetXmin"), Direct("HeapTupleNoNulls"),
        ]);
        return helpers.AsReadOnly();
    }

    /// <summary>
    /// Selects missing helper requests while keeping generated foreign declarations authoritative.
    /// </summary>
    /// <param name="inventory">The pinned generated foreign inventory.</param>
    /// <returns>Required supplemental native functions, ordered by their public names.</returns>
    internal static IReadOnlyList<NativeHeaderRequest> Requests(NativeBindingRawCatalog inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var requests = new List<NativeHeaderRequest>();
        foreach (NativeBindingHeaderHelper helper in Read(inventory.PostgresMajor))
        {
            if (inventory.Globals.ContainsKey(helper.Name))
            {
                throw new FormatException($"Native helper '{helper.Name}' conflicts with a global declaration.");
            }

            if (!inventory.Functions.ContainsKey(helper.Name))
            {
                requests.Add(new(helper.Name, helper.NativeName, true));
            }
        }

        return Array.AsReadOnly(requests.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// Makes each macro addressable before every compiler observation and actual native body compilation.
    /// </summary>
    /// <param name="major">The selected PostgreSQL major.</param>
    /// <returns>Internal C wrappers compiled against that major's original headers.</returns>
    internal static string Source(int major)
    {
        var source = new StringBuilder("\n#ifndef ANKUS_NATIVE_HEADER_HELPERS\n#define ANKUS_NATIVE_HEADER_HELPERS\n");
        foreach (NativeBindingHeaderHelper helper in Read(major))
        {
            source.Append(helper.Source);
        }

        source.AppendLine("#endif");
        return source.ToString();
    }

    /// <summary>
    /// Enables only macro implementations that the current native translation unit needs.
    /// </summary>
    /// <param name="major">The selected PostgreSQL major.</param>
    /// <param name="symbols">The native functions being compiled or verified.</param>
    /// <returns>Preprocessor selections placed before the selected headers.</returns>
    internal static string Definitions(int major, IEnumerable<NativeHeaderSymbol> symbols)
    {
        HashSet<string> selected = [.. symbols.Where(static symbol => symbol.IsFunction).Select(static symbol => symbol.NativeName)];
        var source = new StringBuilder();
        foreach (NativeBindingHeaderHelper helper in Read(major).Where(helper => helper.Source is not null && selected.Contains(helper.NativeName)))
        {
            source.Append("#define ANKUS_DEFINE_HEADER_").Append(helper.Name).AppendLine();
        }

        return source.ToString();
    }

    private static NativeBindingHeaderHelper Direct(string name) => new(name, name, null);

    private static NativeBindingHeaderHelper Macro(string name, string result, string parameters, string body)
    {
        string native = "ankus_header_" + name;
        return new(name, native, $"extern {result} {native}({parameters});\n" +
            $"#if defined(ANKUS_DEFINE_HEADER_{name})\n" +
            "#if !defined(_WIN32)\n__attribute__((visibility(\"hidden\")))\n#endif\n" +
            $"{result} {native}({parameters})\n{{\n    {body}\n}}\n#endif\n");
    }
}

/// <summary>
/// Retains a helper's public name, addressable native declaration and optional macro implementation.
/// </summary>
/// <param name="Name">The public PostgreSQL helper name.</param>
/// <param name="NativeName">The native function or internal wrapper measured by Clang.</param>
/// <param name="Source">An internal wrapper, or null when the selected headers provide the declaration.</param>
internal sealed record NativeBindingHeaderHelper(string Name, string NativeName, string? Source);
