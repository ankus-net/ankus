namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Native object qualification follows aliases, arrays and records while stopping at pointer boundaries.
    /// </summary>
    [TestMethod]
    public async Task NativeGlobalSelectionsPreserveObjectQualification()
    {
        const string Headers = """
            typedef const int ReadOnlyNumber;
            typedef int Numbers[2][3];
            typedef struct { int count; const long identity; } ImmutableFields;
            typedef struct { volatile unsigned long long version; int count; } ObservedFields;
            typedef struct { const int *address; int count; } PointerFields;
            typedef union { const long identity; unsigned char bytes[sizeof(long)]; } ImmutableUnion;
            struct Opaque;
            extern int native_value;
            extern ReadOnlyNumber native_constant;
            extern volatile int native_notification;
            extern const int *native_address;
            extern int * const native_fixed_address;
            extern Numbers native_array;
            extern const Numbers native_constant_array;
            extern volatile Numbers native_observed_array;
            extern ImmutableFields native_immutable;
            extern ObservedFields native_observed;
            extern PointerFields native_pointers;
            extern ImmutableUnion native_union;
            extern int native_unbounded[];
            extern struct Opaque native_opaque;
            extern int (*native_callback)(int);
            extern _Thread_local int native_thread_value;
            """;
        string[] names = ["native_value", "native_constant", "native_notification", "native_address", "native_fixed_address",
            "native_array", "native_constant_array", "native_observed_array", "native_immutable", "native_observed",
            "native_pointers", "native_union", "native_unbounded", "native_opaque", "native_callback", "native_thread_value"];
        string directory = Path.Join(Path.GetTempPath(), $"ankus-global-model-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRequest[] requests = [.. names.Select(static name => new NativeHeaderRequest(name, name, false))];
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, requests, directory);
            IReadOnlyList<NativeBindingGlobalContract> selected = NativeBindingGlobalModel.Select(records, names);
            Assert.AreSequenceEqual(names.Order(StringComparer.Ordinal), selected.Select(static value => value.Name));
            Assert.AreSequenceEqual(selected, NativeBindingGlobalModel.Select(records, [.. names.Reverse()]));
            Assert.ThrowsExactly<NotSupportedException>(() => ((IList<NativeBindingGlobalContract>)selected).Clear());
            Assert.AreSequenceEqual<string>(["native_address", "native_array", "native_callback", "native_notification", "native_observed",
                "native_observed_array", "native_pointers", "native_thread_value", "native_value"],
                selected.Where(static value => value.CanWrite).Select(static value => value.Name));
            Assert.AreSequenceEqual<string>(["native_notification", "native_observed", "native_observed_array"],
                selected.Where(static value => value.RequiresTypedAccess).Select(static value => value.Name));
            Assert.AreSequenceEqual<string>(["native_opaque", "native_unbounded"],
                selected.Where(static value => !value.IsComplete).Select(static value => value.Name));
            Assert.IsTrue(selected.Single(static value => value.Name == "native_thread_value").Symbol.IsThreadLocal);
            foreach (NativeBindingGlobalContract global in selected)
            {
                Assert.AreSame(records.Headers.Symbols[global.Name], global.Symbol);
                Assert.AreEqual(records.Graph.Roots[global.Name], global.StorageType);
            }

            Assert.IsEmpty(NativeBindingGlobalModel.Select(records, []));
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, [], names);
            Assert.AreEqual(binding, NativeBindingRecordCSharp.Generate(records, [], [.. names.Reverse()]));
            Assert.AreNotEqual(binding.AbiIdentity, NativeBindingRecordCSharp.Generate(records, []).AbiIdentity);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A global access cannot bypass complete graph validation or select a missing, duplicated or function declaration.
    /// </summary>
    [TestMethod]
    public async Task NativeGlobalSelectionsRejectInvalidContractsAndRecover()
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-global-validation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(MixedCallHeaders, s_mixedCallRequests, directory);
            IReadOnlyList<NativeBindingGlobalContract> expected = NativeBindingGlobalModel.Select(records, ["native_global"]);
            foreach (string[] names in new string[][]
            {
                ["missing"], ["native_global", "native_global"], ["native_first"], ["native_variadic"], ["native_unprototyped"], ["not-a-name"],
            })
            {
                Assert.ThrowsExactly<FormatException>(() => NativeBindingGlobalModel.Select(records, names));
            }

            Dictionary<string, int> changedRoots = records.Graph.Roots.ToDictionary();
            changedRoots["native_first"] = records.Graph.Types.Count;
            NativeHeaderRecords invalid = records with
            {
                Graph = records.Graph with
                {
                    Roots = changedRoots
                }
            };
            Assert.ThrowsExactly<FormatException>(() => NativeBindingGlobalModel.Select(invalid, ["native_global"]));
            Assert.ThrowsExactly<FormatException>(() => NativeBindingGlobalModel.Select(invalid, []));
            Assert.ThrowsExactly<ArgumentNullException>(() => NativeBindingGlobalModel.Select(null!, []));
            Assert.ThrowsExactly<ArgumentNullException>(() => NativeBindingGlobalModel.Select(records, null!));
            Assert.AreSequenceEqual(expected, NativeBindingGlobalModel.Select(records, ["native_global"]));
            Assert.HasCount(5, records.Graph.Roots);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
