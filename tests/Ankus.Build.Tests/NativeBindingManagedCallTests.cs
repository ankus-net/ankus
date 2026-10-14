namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Methods retain shared types from unselected roots and validate the complete graph even for an empty selection.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsRetainCompleteValidation()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-managed-call-selection-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(MixedCallHeaders, s_mixedCallRequests, directory);
            const string Harness = """
                public static class BindingAssertions
                {
                    public static long[] Run() => [System.Runtime.CompilerServices.Unsafe.SizeOf<Ankus.Postgres.State>()];
                }
                namespace Ankus.Postgres
                {
                    public static partial class NativeMethods
                    {
                        private static partial nint GetNativeBody_native_second() => 0;
                    }
                }
                """;
            Assert.AreSequenceEqual<long>([16], GeneratedBindingCompilation.Run(NativeBindingRecordCSharp.Generate(records, ["native_second"]), Harness, context.CancellationToken));
            foreach (string[] names in new string[][]
            {
                ["missing"], ["native_first", "native_first"], ["native_global"], ["native_variadic"], ["native_unprototyped"],
            })
            {
                Assert.ThrowsExactly<FormatException>(() => NativeBindingRecordCSharp.Generate(records, names));
            }

            Dictionary<string, int> roots = records.Graph.Roots.ToDictionary();
            roots["native_global"] = records.Graph.Types.Count;
            NativeHeaderRecords invalid = records with
            {
                Graph = records.Graph with
                {
                    Roots = roots
                }
            };
            Assert.ThrowsExactly<FormatException>(() => NativeBindingRecordCSharp.Generate(invalid, ["native_second"]));
            Assert.ThrowsExactly<FormatException>(() => NativeBindingRecordCSharp.Generate(invalid, []));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Frame arithmetic accepts the final representable byte and rejects overflow instead of narrowing allocation lengths.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallFramesRejectUnrepresentableStorage()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-managed-call-frame-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync("extern void consume(char value);", [new("consume", "consume", true)], directory);
            NativeBindingCall call = NativeBindingCallModel.Select(records, ["consume"])[0];
            int index = call.Parameters[0].StorageType;
            foreach ((int width, long maximum, long allowance) in new (int, long, long)[] { (4, uint.MaxValue, 11), (8, long.MaxValue, 23) })
            {
                NativeRecordType[] types = [.. records.Graph.Types];
                types[index] = types[index] with
                {
                    Size = maximum - allowance,
                    Alignment = 1
                };
                NativeRecordGraph graph = records.Graph with
                {
                    Target = records.Graph.Target with
                    {
                        PointerSize = width
                    },
                    Types = types
                };
                NativeBindingCallFrame frame = NativeBindingCallFrameLayout.Create(graph, call);
                Assert.AreEqual(maximum, frame.AllocationSize);
                Assert.AreSequenceEqual<long>([2 * width], frame.Arguments);
                Assert.AreEqual(width, frame.Alignment);
                types[index] = types[index] with
                {
                    Size = maximum - allowance + 1
                };
                FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallFrameLayout.Create(graph, call));
                Assert.Contains("address space", error.Message);
            }
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Native names, linker aliases and declaration metadata cannot reuse a differently bound companion identity.
    /// </summary>
    /// <param name="mutation">The native declaration metadata changed without changing its record graph.</param>
    [TestMethod]
    [DataRow("native")]
    [DataRow("linkage")]
    [DataRow("storage")]
    [DataRow("attributes")]
    [DataRow("parameters")]
    public async Task ManagedCallIdentityPreservesNativeSymbols(string mutation)
    {
        const string Headers = "extern int first(int value); extern int second(int value);";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-call-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, [new("call", "first", true)], directory);
            NativeBindingSource original = NativeBindingRecordCSharp.Generate(records, ["call"]);
            NativeHeaderSymbol symbol = records.Headers.Symbols["call"];
            NativeHeaderSymbol changed = mutation switch
            {
                "native" => symbol with { NativeName = "second" },
                "linkage" => symbol with { LinkageName = "alternate_export" },
                "storage" => symbol with { StorageClass = "static" },
                "attributes" => symbol with { Attributes = ["ColdAttr"] },
                _ => symbol with { ParameterNames = ["other"] },
            };
            NativeHeaderRecords remapped = records with
            {
                Headers = records.Headers with
                {
                    Symbols = new Dictionary<string, NativeHeaderSymbol>(StringComparer.Ordinal)
                    {
                        ["call"] = changed
                    }
                }
            };
            NativeBindingSource actual = NativeBindingRecordCSharp.Generate(remapped, ["call"]);
            Assert.AreNotEqual(original.AbiIdentity, actual.AbiIdentity);
            Assert.AreNotEqual(original.AssemblyName, actual.AssemblyName);
            Assert.AreEqual(original, NativeBindingRecordCSharp.Generate(records, ["call"]));
            Assert.AreEqual(NativeBindingRecordCSharp.Generate(records.Graph), NativeBindingRecordCSharp.Generate(remapped.Graph));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Dictionary and selection insertion order cannot change a companion with the same complete native contract.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallIdentityIgnoresDictionaryOrder()
    {
        const string Headers = "extern int first(int value); extern long second(long value); extern int current;";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-call-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers,
                [new("first", "first", true), new("second", "second", true), new("current", "current", false)], directory);
            NativeHeaderRecords reordered = records with
            {
                Headers = records.Headers with
                {
                    Symbols = records.Headers.Symbols.Reverse().ToDictionary(StringComparer.Ordinal)
                },
                Graph = records.Graph with
                {
                    Roots = records.Graph.Roots.Reverse().ToDictionary(StringComparer.Ordinal)
                },
            };
            Assert.AreEqual(NativeBindingRecordCSharp.Generate(records, ["first", "second"]),
                NativeBindingRecordCSharp.Generate(reordered, ["second", "first"]));
            NativeBindingSource full = NativeBindingRecordCSharp.Generate(records, ["first", "second"]);
            Assert.AreNotEqual(full.AbiIdentity, NativeBindingRecordCSharp.Generate(records, ["first"]).AbiIdentity);
            Assert.AreNotEqual(full.AbiIdentity, NativeBindingRecordCSharp.Generate(records, []).AbiIdentity);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Actual generated methods compile with conflicting C names and reject missing scopes before accessor or native entry.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsValidateActiveBinding()
    {
        const string Headers = "struct NativeMethods { int value; }; extern struct NativeMethods native_call(struct NativeMethods allocation, int storage, _Bool arguments, void *alignment);";
        const string Harness = """
            using System;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    int rejected = 0;
                    try { NativeMethods.Native_NativeMethods(default, 37, true, null); }
                    catch (InvalidOperationException error) when (error.Message.Contains("active backend callback", StringComparison.Ordinal)) { rejected++; }
                    return [rejected, NativeMethods.Accessors];
                }
            }
            namespace Ankus.Postgres
            {
                public static partial class NativeMethods
                {
                    public static int Accessors;
                    private static partial nint GetNativeBody_NativeMethods() { Accessors++; return 0; }
                }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), $"ankus-managed-call-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, [new("NativeMethods", "native_call", true)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, ["NativeMethods"]);
            Assert.AreSequenceEqual<long>([1, 0], GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
