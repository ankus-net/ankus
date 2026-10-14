namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    private const string MixedCallHeaders = """
        typedef struct { unsigned long long bits; int count; } State;
        extern State native_global;
        extern int native_variadic(const char *format, ...);
        extern int native_unprototyped();
        State native_first(State value) { value.bits ^= 0xffffffffffffffffULL; value.count += 7; return value; }
        int native_second(int value) { return value * 3; }
        """;

    private static readonly NativeHeaderRequest[] s_mixedCallRequests =
    [
        new("native_global", "native_global", false),
        new("native_variadic", "native_variadic", true),
        new("native_unprototyped", "native_unprototyped", true),
        new("native_first", "native_first", true),
        new("native_second", "native_second", true),
    ];

    /// <summary>
    /// Fixed calls execute from one graph that also retains global storage and non-fixed prototypes without linking unused bodies.
    /// </summary>
    [TestMethod]
    public async Task NativeCallBodiesSelectWithinCompleteGraph()
    {
        const string Main = """
            #include <stdio.h>
            int main(void)
            {
                State input = { 0x8123456789abcdefULL, -11 }, result = {0};
                AnkusNativeCallArgument argument = { &input, sizeof(input) };
                if (ankus_native_call_native_first(&argument, 1, &result, sizeof(result)) != 0) return 1;
                if (result.bits != 0x7edcba9876543210ULL || result.count != -4) return 2;
                if (input.bits != 0x8123456789abcdefULL || input.count != -11) return 3;
                int value = 29, product = 0;
                argument = (AnkusNativeCallArgument){ &value, sizeof(value) };
                if (ankus_native_call_native_second(&argument, 1, &product, sizeof(product)) != 0) return 4;
                if (product != 87 || value != 29) return 5;
                puts("shared native graph retains exact calls");
                return 0;
            }
            """;
        Assert.AreEqual("shared native graph retains exact calls\n", await ExecuteNativeCallsAsync(MixedCallHeaders,
            s_mixedCallRequests, ["native_second", "native_first"], Main));
        Assert.AreEqual("", await ExecuteNativeCallsAsync(MixedCallHeaders, s_mixedCallRequests, [], "int main(void) { return 0; }"));
    }

    /// <summary>
    /// The native body compiler accepts exact top-level and typedef qualifiers without redundant const, retaining volatile reads and pointee writes.
    /// </summary>
    [TestMethod]
    public async Task NativeCallBodiesPreserveQualifiedArguments()
    {
        const string Headers = """
            typedef const double Real;
            typedef const int Number;
            typedef const int * const ReadOnlyAddress;
            typedef int * const WritableAddress;
            double native_qualified(const double left, Real right, volatile Number step) { return left + right + step; }
            int native_read(ReadOnlyAddress address) { return *address; }
            void native_write(WritableAddress address) { *address += 7; }
            """;
        const string Main = """
            #include <stdio.h>
            int main(void)
            {
                const double left = 1.25;
                Real right = 2.5;
                Number step = 4;
                double result = 0;
                AnkusNativeCallArgument arguments[] = {{ &left, sizeof(left) }, { &right, sizeof(right) }, { &step, sizeof(step) }};
                if (ankus_native_call_native_qualified(arguments, 3, &result, sizeof(result)) != 0 || result != 7.75) return 1;
                if (left != 1.25 || right != 2.5 || step != 4) return 2;
                const int input = 31;
                ReadOnlyAddress address = &input;
                int read = 0;
                arguments[0] = (AnkusNativeCallArgument){ &address, sizeof(address) };
                if (ankus_native_call_native_read(arguments, 1, &read, sizeof(read)) != 0 || read != 31 || input != 31) return 3;
                int destination = 9;
                WritableAddress writable = &destination;
                arguments[0] = (AnkusNativeCallArgument){ &writable, sizeof(writable) };
                if (ankus_native_call_native_write(arguments, 1, NULL, 0) != 0 || destination != 16) return 4;
                if (writable != &destination || address != &input) return 5;
                puts("native argument qualifiers retained");
                return 0;
            }
            """;
        Assert.AreEqual("native argument qualifiers retained\n", await ExecuteNativeCallsAsync(Headers,
            [new("native_qualified", "native_qualified", true), new("native_read", "native_read", true), new("native_write", "native_write", true)],
            null, Main, nativeCompiler: true));
    }

    /// <summary>
    /// Selection order is deterministic, invalid names and body kinds reject, and unselected graph errors remain enforced.
    /// </summary>
    [TestMethod]
    public async Task NativeCallBodySelectionsRetainCompleteValidation()
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-call-selection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(MixedCallHeaders, s_mixedCallRequests, directory);
            NativeBindingSource companion = NativeBindingRecordCSharp.Generate(records.Graph);
            string expected = NativeBindingCallSource.Generate(records, MixedCallHeaders, ["native_first", "native_second"]);
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(records, MixedCallHeaders, ["native_second", "native_first"]));
            foreach (string[] names in new string[][]
            {
                ["missing"], ["native_first", "native_first"], ["native_global"], ["native_variadic"], ["native_unprototyped"],
            })
            {
                Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(records, MixedCallHeaders, names));
            }

            Dictionary<string, int> changedRoots = records.Graph.Roots.ToDictionary();
            changedRoots["native_global"] = records.Graph.Types.Count;
            NativeHeaderRecords invalid = records with
            {
                Graph = records.Graph with
                {
                    Roots = changedRoots
                }
            };
            Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(invalid, MixedCallHeaders, ["native_first"]));
            Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(invalid, MixedCallHeaders, []));
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(records, MixedCallHeaders, ["native_first", "native_second"]));
            Assert.AreEqual(companion, NativeBindingRecordCSharp.Generate(records.Graph));
            Assert.HasCount(5, records.Graph.Roots);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
