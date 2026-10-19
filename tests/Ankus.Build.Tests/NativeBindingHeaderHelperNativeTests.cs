namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// The older macro retains its configured block size and assertion-only argument checks in both server build modes.
    /// </summary>
    /// <param name="major">The selected major with a macro-based page-size helper.</param>
    /// <param name="assertions">Whether PostgreSQL assertion expressions evaluate their arguments.</param>
    [TestMethod]
    [DataRow(13, false)]
    [DataRow(13, true)]
    [DataRow(14, false)]
    [DataRow(14, true)]
    [DataRow(15, false)]
    [DataRow(15, true)]
    public async Task NativeBufferPageSizePreservesAssertionModes(int major, bool assertions)
    {
        const string Headers = """
            #include <stdbool.h>
            #include <stddef.h>
            #include <stdlib.h>
            typedef int Buffer;
            typedef size_t Size;
            #define BLCKSZ 16384
            int validations;
            bool BufferIsValid(Buffer value) { ++validations; return value != 0; }
            #if ASSERTIONS
            #define AssertMacro(condition) ((void)((condition) || (abort(), 0)))
            #else
            #define AssertMacro(condition) ((void)true)
            #endif
            #define BufferGetPageSize(buffer) (AssertMacro(BufferIsValid(buffer)), (Size)BLCKSZ)
            """;
        NativeBindingHeaderHelper helper = NativeBindingHeaderHelpers.Read(major).Single(static value => value.Name == "BufferGetPageSize");
        NativeHeaderRequest request = NativeBindingHeaderAvailability.Request(NativeBindingRawParser.Parse("", major), "BufferGetPageSize");
        string headers = "#define ASSERTIONS " + (assertions ? "1" : "0") + "\n" + Headers + "\n" + helper.Source;
        const string Main = """
            #include <stdio.h>
            #define REQUIRE(expression) do { if (!(expression)) { fprintf(stderr, "line %d: %s\n", __LINE__, #expression); return 1; } } while (0)
            int main(void)
            {
                Buffer values[] = {-8, -1, 1, 16};
                for (size_t i = 0; i < sizeof(values) / sizeof(values[0]); ++i)
                {
                    Size result = 41;
                    AnkusNativeCallArgument arguments[] = {{ &values[i], sizeof(values[i]) }};
                    REQUIRE(ankus_native_call_BufferGetPageSize(arguments, 1, &result, sizeof(result)) == ANKUS_CALL_OK);
                    REQUIRE(result == 16384);
                    REQUIRE(ankus_native_call_BufferGetPageSize(arguments, 0, &result, sizeof(result)) == ANKUS_CALL_COUNT);
                    REQUIRE(result == 16384);
                }
            #if ASSERTIONS
                REQUIRE(validations == 4);
            #else
                REQUIRE(ankus_header_BufferGetPageSize(0) == 16384);
                REQUIRE(validations == 0);
            #endif
                puts("configured page size and assertion semantics preserved");
                return 0;
            }
            """;
        Assert.AreEqual("configured page size and assertion semantics preserved\n",
            await ExecuteNativeCallsAsync(headers, [request], null, Main, major: major));
    }

    /// <summary>
    /// Buffer validity retains local, invalid and shared values across the PostgreSQL 16 macro transition.
    /// </summary>
    /// <param name="major">The selected PostgreSQL major.</param>
    [TestMethod]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(15)]
    [DataRow(16)]
    [DataRow(17)]
    [DataRow(18)]
    [DataRow(19)]
    public async Task NativeBufferValidityPreservesMacroAndInlineContracts(int major)
    {
        const string Headers = """
            #include <stdbool.h>
            #include <assert.h>
            typedef int Buffer;
            #define InvalidBuffer 0
            #define NBuffers 16
            #define NLocBuffer 8
            #if PG_VERSION_NUM < 160000
            #define BufferIsValid(value) (assert((value) <= NBuffers && (value) >= -NLocBuffer), (value) != InvalidBuffer)
            #else
            static inline bool BufferIsValid(Buffer value)
            {
                assert(value <= NBuffers && value >= -NLocBuffer);
                return value != InvalidBuffer;
            }
            bool (*buffer_validity_contract)(Buffer) = BufferIsValid;
            #endif
            """;
        NativeBindingHeaderHelper helper = NativeBindingHeaderHelpers.Read(major).Single(static value => value.Name == "BufferIsValid");
        NativeHeaderRequest request = NativeBindingHeaderAvailability.Request(NativeBindingRawParser.Parse("", major), "BufferIsValid");
        const string Main = """
            #include <stdio.h>
            #define REQUIRE(expression) do { if (!(expression)) { fprintf(stderr, "line %d: %s\n", __LINE__, #expression); return 1; } } while (0)
            int main(void)
            {
                Buffer values[] = {-8, -1, 0, 1, 16};
                bool expected[] = {true, true, false, true, true};
                for (size_t i = 0; i < sizeof(values) / sizeof(values[0]); ++i)
                {
                    bool result = !expected[i];
                    AnkusNativeCallArgument arguments[] = {{ &values[i], sizeof(values[i]) }};
                    REQUIRE(ankus_native_call_BufferIsValid(arguments, 1, &result, sizeof(result)) == ANKUS_CALL_OK);
                    REQUIRE(result == expected[i]);
            #if PG_VERSION_NUM >= 160000
                    REQUIRE(buffer_validity_contract(values[i]) == expected[i]);
            #endif
                    REQUIRE(ankus_native_call_BufferIsValid(arguments, 0, &result, sizeof(result)) == ANKUS_CALL_COUNT);
                    REQUIRE(result == expected[i]);
                }
                puts("local, invalid and shared buffer identities preserved");
                return 0;
            }
            """;
        Assert.AreEqual("local, invalid and shared buffer identities preserved\n",
            await ExecuteNativeCallsAsync(Headers + "\n" + helper.Source, [request], null, Main, major: major));
    }

    /// <summary>
    /// Addressable macros preserve boundary values, single evaluation, native addresses, masks, and lock qualifiers.
    /// </summary>
    [TestMethod]
    public async Task NativeHeaderMacrosPreserveValuesAndPointerContracts()
    {
        const string Headers = """
            #include <stdint.h>
            #include <stddef.h>
            #include <stdbool.h>
            typedef uint8_t uint8;
            typedef uint16_t uint16;
            typedef uint32_t TransactionId;
            typedef uint32_t CommandId;
            typedef uint32_t Oid;
            typedef size_t Size;
            typedef int Buffer;
            typedef unsigned char slock_t;
            typedef struct MemoryContextData *MemoryContext;
            typedef struct HeapTupleHeaderData { uint8 t_hoff; uint16 t_infomask2; } HeapTupleHeaderData;
            typedef struct HeapTupleData { HeapTupleHeaderData *t_data; } HeapTupleData;
            typedef HeapTupleData *HeapTuple;
            typedef struct ItemIdData { unsigned int lp_off:15, lp_flags:2, lp_len:15; } ItemIdData;
            typedef ItemIdData *ItemId;
            typedef struct PageData { unsigned char bytes[8192]; } PageData;
            typedef PageData *Page;
            typedef struct PageHeaderData { uint16 lower, upper, special; ItemIdData pd_linp[1]; } PageHeaderData;
            #define BLCKSZ 8192
            #define GETSTRUCT(value) ((char *) ((value)->t_data) + (value)->t_data->t_hoff)
            #define TYPEALIGN(alignment, value) (((uintptr_t)(value) + ((alignment) - 1)) & ~((uintptr_t)((alignment) - 1)))
            #define MAXALIGN(value) TYPEALIGN(8, (value))
            #define TransactionIdIsNormal(value) ((value) >= 3)
            #define BufferIsLocal(value) ((value) < 0)
            #define ItemIdGetOffset(value) ((value)->lp_off)
            #define SizeOfPageHeaderData offsetof(PageHeaderData, pd_linp)
            #define HeapTupleHeaderGetNatts(value) ((value)->t_infomask2 & 0x07ff)
            #define SpinLockInit(value) (*(value) = 0)
            #define SpinLockAcquire(value) (*(value) = 1)
            #define SpinLockRelease(value) (*(value) = 0)
            #define SpinLockFree(value) (*(value) == 0)
            """;
        string[] names = ["GETSTRUCT", "TYPEALIGN", "MAXALIGN", "TransactionIdIsNormal", "BufferIsLocal", "ItemIdGetOffset",
            "PageIsValid", "PageSizeIsValid", "SizeOfPageHeaderData", "HeapTupleHeaderGetNatts", "SpinLockInit", "SpinLockAcquire", "SpinLockRelease", "SpinLockFree"];
        NativeBindingRawCatalog inventory = NativeBindingRawParser.Parse("", 18);
        NativeHeaderRequest[] requests = [.. names.Select(name => NativeBindingHeaderAvailability.Request(inventory, name))];
        string headers = Headers + "\n" + NativeBindingHeaderHelpers.Source(18);
        const string Main = """
            #include <stdio.h>
            #define REQUIRE(expression) do { if (!(expression)) { fprintf(stderr, "line %d: %s\n", __LINE__, #expression); return 1; } } while (0)
            static int alignment_reads, value_reads, tuple_reads;
            static uintptr_t alignment_value(void) { ++alignment_reads; return 8; }
            static uintptr_t length_value(void) { ++value_reads; return 9; }
            static HeapTuple tuple_value(HeapTuple tuple) { ++tuple_reads; return tuple; }
            int main(void)
            {
                uintptr_t inputs[] = {0, 1, 7, 8, 9, UINTPTR_MAX - 7, UINTPTR_MAX};
                uintptr_t expected[] = {0, 8, 8, 8, 16, UINTPTR_MAX - 7, 0};
                for (size_t i = 0; i < sizeof(inputs) / sizeof(inputs[0]); ++i)
                {
                    REQUIRE(ankus_header_TYPEALIGN(8, inputs[i]) == expected[i]);
                    REQUIRE(ankus_header_MAXALIGN(inputs[i]) == expected[i]);
                }
                REQUIRE(ankus_header_TYPEALIGN(alignment_value(), length_value()) == 16);
                REQUIRE(alignment_reads == 1 && value_reads == 1);
                REQUIRE(ankus_header_TYPEALIGN(1, 9) == 9);
                REQUIRE(ankus_header_TYPEALIGN(2, 9) == 10);
                REQUIRE(ankus_header_TYPEALIGN(16, 16) == 16);
                REQUIRE(ankus_header_TYPEALIGN(16, 17) == 32);
                REQUIRE(ankus_header_TYPEALIGN(32, 17) == 32);
                uintptr_t alignment = 8, input = 9, result = 123;
                AnkusNativeCallArgument arguments[] = {{ &alignment, sizeof(alignment) }, { &input, sizeof(input) }};
                REQUIRE(ankus_native_call_TYPEALIGN(arguments, 2, &result, sizeof(result)) == ANKUS_CALL_OK);
                REQUIRE(result == 16);
                REQUIRE(ankus_native_call_TYPEALIGN(arguments, 1, &result, sizeof(result)) == ANKUS_CALL_COUNT);
                REQUIRE(result == 16);
                _Alignas(HeapTupleHeaderData) unsigned char bytes[sizeof(HeapTupleHeaderData) + 17] = {0};
                HeapTupleHeaderData *header = (HeapTupleHeaderData *)bytes;
                header->t_hoff = (uint8)(sizeof(HeapTupleHeaderData) + 3);
                HeapTupleData tuple = { header };
                REQUIRE(ankus_header_GETSTRUCT(tuple_value(&tuple)) == (char *)bytes + sizeof(HeapTupleHeaderData) + 3);
                REQUIRE(tuple_reads == 1);
                HeapTuple tuple_address = &tuple;
                char *data = NULL;
                arguments[0] = (AnkusNativeCallArgument){ &tuple_address, sizeof(tuple_address) };
                REQUIRE(ankus_native_call_GETSTRUCT(arguments, 1, &data, sizeof(data)) == ANKUS_CALL_OK);
                REQUIRE(data == (char *)bytes + header->t_hoff);
                uint16 masks[] = {0, 1, 0x07ff, 0xffff};
                uint16 counts[] = {0, 1, 0x07ff, 0x07ff};
                for (size_t i = 0; i < sizeof(masks) / sizeof(masks[0]); ++i)
                {
                    header->t_infomask2 = masks[i];
                    REQUIRE(ankus_header_HeapTupleHeaderGetNatts(header) == counts[i]);
                }
                ItemIdData item = {0};
                REQUIRE(ankus_header_ItemIdGetOffset(&item) == 0);
                item.lp_off = 0x7fff;
                item.lp_flags = 3;
                item.lp_len = 0x7ffe;
                REQUIRE(ankus_header_ItemIdGetOffset(&item) == 0x7fff);
                REQUIRE(item.lp_flags == 3 && item.lp_len == 0x7ffe);
                PageData page = {{0}};
                REQUIRE(!ankus_header_PageIsValid(NULL));
                REQUIRE(ankus_header_PageIsValid(&page));
                REQUIRE(!ankus_header_PageSizeIsValid(0));
                REQUIRE(!ankus_header_PageSizeIsValid(BLCKSZ - 1));
                REQUIRE(ankus_header_PageSizeIsValid(BLCKSZ));
                REQUIRE(!ankus_header_PageSizeIsValid(BLCKSZ + 1));
                REQUIRE(!ankus_header_PageSizeIsValid(UINT64_C(0x100002000)));
                REQUIRE(ankus_header_SizeOfPageHeaderData() == offsetof(PageHeaderData, pd_linp));
                REQUIRE(!ankus_header_TransactionIdIsNormal(0) && !ankus_header_TransactionIdIsNormal(1));
                REQUIRE(!ankus_header_TransactionIdIsNormal(2) && ankus_header_TransactionIdIsNormal(3));
                REQUIRE(ankus_header_TransactionIdIsNormal(UINT32_MAX));
                REQUIRE(ankus_header_BufferIsLocal(-1) && !ankus_header_BufferIsLocal(0) && !ankus_header_BufferIsLocal(1));
                slock_t lock = 99;
                ankus_header_SpinLockInit(&lock);
                REQUIRE(lock == 0 && ankus_header_SpinLockFree(&lock));
                ankus_header_SpinLockAcquire(&lock);
                REQUIRE(lock == 1 && !ankus_header_SpinLockFree(&lock));
                ankus_header_SpinLockRelease(&lock);
                REQUIRE(lock == 0 && ankus_header_SpinLockFree(&lock));
                puts("header helper values and lifetimes retained");
                return 0;
            }
            """;
        Assert.AreEqual("header helper values and lifetimes retained\n", await ExecuteNativeCallsAsync(headers, requests, null, Main, nativeCompiler: true));
        string incompatible = headers.Replace("volatile slock_t", "slock_t", StringComparison.Ordinal);
        string directory = Directory.CreateTempSubdirectory("ankus-header-helper-qualifiers-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(headers, requests, directory);
            string file = Path.Join(directory, "incompatible.c");
            string source = NativeBindingCallSource.Generate(records, "#define PG_VERSION_NUM 180006\n" + incompatible);
            await File.WriteAllTextAsync(file, source, context.CancellationToken);
            string compiler = await HostCompilers.ClangAsync();
            string[] options = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", file];
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, options, Path.Join(directory, "checks.txt"), directory,
                    context.CancellationToken, inspectBodies: true));
            Assert.Contains("incompatible reconstructed native type", failure.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
