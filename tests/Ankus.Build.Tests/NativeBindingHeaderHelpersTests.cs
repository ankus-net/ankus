namespace Ankus.Build.Tests;

/// <summary>
/// Verifies selected-major helper contracts and their boundaries with the pinned foreign inventory.
/// </summary>
[TestClass]
public sealed class NativeBindingHeaderHelpersTests
{
    /// <summary>
    /// Native inline transitions preserve original declarations while missing helpers receive explicit wrappers.
    /// </summary>
    /// <param name="major">The supported PostgreSQL major.</param>
    [TestMethod]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(15)]
    [DataRow(16)]
    [DataRow(17)]
    [DataRow(18)]
    [DataRow(19)]
    public void HeaderHelpersRespectSelectedMajorAndExistingDeclarations(int major)
    {
        IReadOnlyList<NativeBindingHeaderHelper> helpers = NativeBindingHeaderHelpers.Read(major);
        Assert.AreSequenceEqual<string>(["PageHeaderData"], NativeBindingHeaderHelpers.RequiredTypes);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<string>)NativeBindingHeaderHelpers.RequiredTypes).Clear());
        Dictionary<string, NativeBindingHeaderHelper> names = helpers.ToDictionary(static helper => helper.Name, StringComparer.Ordinal);
        string[] expected = ["pg_add_s32_overflow", "GETSTRUCT", "TYPEALIGN", "MAXALIGN", "GetMemoryChunkContext", "MemoryContextIsValid", "MemoryContextSwitchTo",
            "TransactionIdIsNormal", "TransactionIdPrecedes", "TransactionIdPrecedesOrEquals", "TransactionIdFollows", "TransactionIdFollowsOrEquals",
            "type_is_array", "BufferIsLocal", "BufferIsValid", "ItemIdGetOffset", "PageIsValid", "PageSizeIsValid", "SizeOfPageHeaderData",
            "PageValidateSpecialPointer", "HeapTupleHeaderGetNatts", "heap_getattr", "SpinLockInit", "SpinLockAcquire", "SpinLockRelease",
            "BufferGetBlock", "BufferGetPage", "BufferGetPageSize", "PageIsEmpty", "PageIsNew", "PageGetItemId", "PageGetContents",
            "PageGetPageSize", "PageGetPageLayoutVersion", "PageSetPageSizeAndVersion", "PageGetSpecialSize", "PageGetItem", "PageGetMaxOffsetNumber",
            "PageGetSpecialPointer", "HeapTupleHeaderIsHeapOnly", "HeapTupleHeaderIsHotUpdated", "HeapTupleHeaderXminInvalid",
            "HeapTupleHeaderXminFrozen", "HeapTupleHeaderGetRawCommandId", "HeapTupleHeaderGetRawXmin", "HeapTupleHeaderGetXmin", "HeapTupleNoNulls"];
        if (major < 19)
        {
            expected = [.. expected, "SpinLockFree"];
        }

        string[] transitions = [.. NativeBindingHeaderTransitions.Read(major).Select(static transition => transition.Name)];
        expected = [.. expected, .. transitions];

        Assert.AreSequenceEqual(expected.Order(StringComparer.Ordinal), names.Keys.Order(StringComparer.Ordinal));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<NativeBindingHeaderHelper>)helpers).Clear());
        var macros = new HashSet<string>(StringComparer.Ordinal)
        {
            "GETSTRUCT", "TYPEALIGN", "MAXALIGN", "MemoryContextIsValid", "TransactionIdIsNormal", "type_is_array",
            "BufferIsLocal", "ItemIdGetOffset", "PageIsValid", "PageSizeIsValid", "SizeOfPageHeaderData", "HeapTupleHeaderGetNatts"
        };
        if (major < 15)
        {
            macros.Add("heap_getattr");
        }

        if (major < 16)
        {
            macros.UnionWith(["BufferIsValid", "BufferGetBlock", "BufferGetPage", "BufferGetPageSize", "PageIsEmpty", "PageIsNew", "PageGetItemId", "PageGetContents",
                "PageGetPageSize", "PageGetPageLayoutVersion", "PageSetPageSizeAndVersion", "PageGetSpecialSize", "PageGetItem", "PageGetMaxOffsetNumber"]);
        }

        if (major is not (16 or 17))
        {
            macros.Add("PageGetSpecialPointer");
        }

        if (major < 18)
        {
            macros.UnionWith(["HeapTupleHeaderIsHeapOnly", "HeapTupleHeaderIsHotUpdated", "HeapTupleHeaderXminInvalid", "HeapTupleHeaderXminFrozen",
                "HeapTupleHeaderGetRawCommandId", "HeapTupleHeaderGetRawXmin", "HeapTupleHeaderGetXmin", "HeapTupleNoNulls"]);
        }

        if (major < 19)
        {
            macros.UnionWith(["SpinLockInit", "SpinLockAcquire", "SpinLockRelease", "SpinLockFree"]);
        }

        macros.UnionWith(transitions);

        foreach ((string name, NativeBindingHeaderHelper helper) in names)
        {
            if (macros.Contains(name))
            {
                Assert.AreEqual("ankus_header_" + name, helper.NativeName);
                Assert.IsNotNull(helper.Source);
            }
            else
            {
                Assert.AreEqual(name, helper.NativeName);
                Assert.IsNull(helper.Source);
            }
        }

        NativeBindingRawCatalog inventory = NativeBindingResources.ReadRawCatalog(major);
        IReadOnlyList<NativeHeaderRequest> requests = NativeBindingHeaderHelpers.Requests(inventory);
        Assert.AreSequenceEqual(requests.OrderBy(static request => request.Name, StringComparer.Ordinal), requests);
        Assert.AreSequenceEqual(expected.Except(inventory.Functions.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal),
            requests.Select(static request => request.Name));
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<NativeHeaderRequest>)requests).Clear());
        foreach (NativeHeaderRequest request in requests)
        {
            Assert.IsTrue(request.IsFunction);
            Assert.AreEqual(names[request.Name].NativeName, request.NativeName);
            Assert.AreEqual(request, NativeBindingHeaderAvailability.Request(inventory, request.Name));
        }

        if (major == 19)
        {
            Assert.ThrowsExactly<FormatException>(() => NativeBindingHeaderAvailability.Request(inventory, "SpinLockFree"));
        }
    }

    /// <summary>
    /// Supplemental selection preserves an existing renamed function and rejects a conflicting global.
    /// </summary>
    [TestMethod]
    public void HeaderHelpersPreserveForeignIdentityAndRejectGlobalCollisions()
    {
        NativeBindingRawCatalog inventory = NativeBindingRawParser.Parse("""
            extern "C" {
                #[link_name = "custom_switch"]
                pub fn MemoryContextSwitchTo(value: usize) -> usize;
            }
            """, 18);
        Assert.DoesNotContain("MemoryContextSwitchTo", NativeBindingHeaderHelpers.Requests(inventory).Select(static request => request.Name));
        Assert.AreEqual(new("MemoryContextSwitchTo", "custom_switch", true), NativeBindingHeaderAvailability.Request(inventory, "MemoryContextSwitchTo"));
        Assert.AreEqual("custom_switch", inventory.Functions["MemoryContextSwitchTo"].NativeSymbol);
        NativeBindingRawCatalog conflict = NativeBindingRawParser.Parse("extern \"C\" { pub static TYPEALIGN: usize; }", 18);
        FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingHeaderHelpers.Requests(conflict));
        Assert.Contains("TYPEALIGN", error.Message);
        Assert.Contains("global", error.Message);
        Assert.ThrowsExactly<ArgumentNullException>(() => NativeBindingHeaderHelpers.Requests(null!));
        Assert.ThrowsExactly<FormatException>(() => NativeBindingHeaderAvailability.Request(inventory, "unknown_helper"));
    }

    /// <summary>
    /// Unsupported majors cannot select helper prototypes, implementation bodies, or header source.
    /// </summary>
    /// <param name="major">A PostgreSQL major outside the supported endpoints.</param>
    [TestMethod]
    [DataRow(int.MinValue)]
    [DataRow(12)]
    [DataRow(20)]
    [DataRow(int.MaxValue)]
    public void HeaderHelpersRejectInvalidMajors(int major)
    {
        ArgumentOutOfRangeException error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NativeBindingHeaderHelpers.Read(major));
        Assert.AreEqual("major", error.ParamName);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NativeBindingHeaderHelpers.Source(major));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NativeBindingHeaderHelpers.Definitions(major, []));
    }
}
