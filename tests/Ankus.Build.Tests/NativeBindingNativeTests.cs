using System.Diagnostics;

namespace Ankus.Build.Tests;

/// <summary>
/// Compiles a native probe against independent C declarations, including anonymous embedded values.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed partial class NativeBindingNativeTests(TestContext context)
{
    /// <summary>
    /// The emitted program measures anonymous unions, array elements and flexible tail padding through actual C paths.
    /// </summary>
    [TestMethod]
    public async Task CompiledProbeObservesAnonymousValuesAndFlexiblePadding()
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
            pub struct Node { pub type_: NodeTag, }
            pub struct Leaf {
                pub type_: NodeTag,
                pub meta: Metadata,
                pub values: [u16; 3usize],
                pub tail: __IncompleteArrayField<u8>,
            }
            pub struct Metadata { pub payload: Payload, }
            pub union Payload {
                pub number: u32,
                pub pair: [u16; 2usize],
            }
            """;
        const string Headers = """
            #include <stdint.h>
            #include <stdio.h>
            #define PG_VERSION_NUM 180006
            typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Leaf {
                NodeTag type;
                struct { union { uint32_t number; uint16_t pair[2]; } payload; } meta;
                uint16_t values[3];
                unsigned char tail[];
            } Leaf;
            """;
        string directory = Path.Join(Path.GetTempPath(), $"ankus-native-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeBindingCatalog catalog = NativeBindingParser.Parse(Declarations, 18);
            string source = Path.Join(directory, "probe.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "probe.exe" : "probe");
            await File.WriteAllTextAsync(source, NativeBindingProbe.GenerateSource(catalog, Headers), context.CancellationToken);
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), source]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", source, "-o", executable];
            await RunAsync(compiler, arguments, directory);
            string observations = await RunAsync(executable, [], directory);
            NativeBindingLayout layout = NativeBindingProbe.Read(catalog, observations);
            Assert.AreEqual(180006, layout.PostgresVersion);
            Assert.AreEqual(IntPtr.Size, layout.PointerSize);
            Assert.AreEqual(OperatingSystem.IsWindows() ? 4 : IntPtr.Size, layout.LongSize);
            Assert.AreEqual(BitConverter.IsLittleEndian, layout.IsLittleEndian);
            Assert.AreSequenceEqual<string>(["Leaf", "Metadata", "Node", "Payload"], layout.Types.Keys);
            Assert.AreEqual(16, layout.Types["Leaf"].Size);
            Assert.AreEqual(4, layout.Types["Leaf"].Alignment);
            Assert.AreEqual(new NativeBindingFieldLayout(4, 4, 4, 4), layout.Types["Leaf"].Fields["meta"]);
            Assert.AreEqual(new NativeBindingFieldLayout(8, 6, 2, 2), layout.Types["Leaf"].Fields["values"]);
            Assert.AreEqual(new NativeBindingFieldLayout(14, 0, 1, 1), layout.Types["Leaf"].Fields["tail"]);
            Assert.AreEqual(new NativeBindingFieldLayout(0, 4, 4, 4), layout.Types["Metadata"].Fields["payload"]);
            Assert.AreEqual(4, layout.Types["Payload"].Size);
            Assert.AreEqual(new NativeBindingFieldLayout(0, 4, 4, 4), layout.Types["Payload"].Fields["number"]);
            Assert.AreEqual(new NativeBindingFieldLayout(0, 4, 2, 2), layout.Types["Payload"].Fields["pair"]);
            Assert.AreEqual(new NativeBindingFieldLayout(0, 4, 4, 4), layout.Types["Node"].Fields["type_"]);
            NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(catalog, Headers);
            NativeHeaderRecords records = await CollectCallRecordsAsync(roots.Source + "\nextern Leaf native_leaf(Leaf value);",
                [.. roots.Requests, new("native_leaf", "native_leaf", true)], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, catalog, layout, ["native_leaf"]);
            const string Harness = """
                using System;
                using Ankus;
                using Ankus.Postgres;
                public static class BindingAssertions
                {
                    public static unsafe long[] Run()
                    {
                        Leaf leaf = default;
                        leaf.type = NodeTag.T_Leaf;
                        leaf.values[0] = 11;
                        leaf.values[2] = 33;
                        leaf.meta.payload.pair[0] = 11;
                        leaf.meta.payload.pair[1] = 22;
                        AlignedLeaf aligned = new() { Prefix = 1, Value = leaf };
                        byte* storage = stackalloc byte[19];
                        Span<byte> tail = Leaf.Dangerous_tail((Leaf*)storage, 3);
                        tail[0] = 90;
                        tail[2] = 92;
                        int negative = 0;
                        int absent = 0;
                        try { Leaf.Dangerous_tail((Leaf*)storage, -1); }
                        catch (ArgumentOutOfRangeException) { negative = 1; }
                        try { Leaf.Dangerous_tail(null, 0); }
                        catch (ArgumentNullException) { absent = 1; }
                        Func<Leaf, Leaf> method = NativeMethods.native_leaf;
                        int guarded = 0;
                        try { method(leaf); }
                        catch (InvalidOperationException error) when (error.Message.Contains("active backend callback", StringComparison.Ordinal)) { guarded = 1; }
                        return [sizeof(Leaf), sizeof(Metadata), sizeof(Payload),
                            (byte*)&leaf.meta - (byte*)&leaf, (byte*)&leaf.values - (byte*)&leaf,
                            leaf.values[0], leaf.values[2], leaf.meta.payload.number,
                            storage[14], storage[16], negative, absent,
                            Leaf.Dangerous_tail((Leaf*)storage, 0).Length,
                            Size<Leaf>(), Alignment<Leaf>(), Major<Leaf>(),
                            Accepts<Leaf>(7), Accepts<Leaf>(0), Accepts<Node>(uint.MaxValue),
                            SameIdentity<Leaf>(), (byte*)&aligned.Value - (byte*)&aligned, guarded, NativeMethods.Accessors];
                    }
                    private struct AlignedLeaf { public byte Prefix; public Leaf Value; }
                    private static int Size<T>() where T : unmanaged, IPgNativeType => T.NativeSize;
                    private static int Alignment<T>() where T : unmanaged, IPgNativeType => T.NativeAlignment;
                    private static int Major<T>() where T : unmanaged, IPgNativeType => T.PostgresMajor;
                    private static int Accepts<T>(uint tag) where T : unmanaged, IPgNativeNode => T.AcceptsTag(tag) ? 1 : 0;
                    private static int SameIdentity<T>() where T : unmanaged, IPgNativeType => T.AbiIdentity == NativeBinding.Identity ? 1 : 0;
                }
                namespace Ankus.Postgres
                {
                    public static partial class NativeMethods
                    {
                        public static int Accessors;
                        private static partial nint GetNativeBody_native_leaf() { Accessors++; return 0; }
                    }
                }
                """;
            long[] observed = GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken);
            Assert.AreSequenceEqual<long>(
                [16, 4, 4, 4, 8, 11, 33, BitConverter.IsLittleEndian ? 0x0016000B : 0x000B0016,
                    90, 92, 1, 1, 0, 16, 4, 18, 1, 0, 1, 1, 4, 1, 0], observed);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    private async Task<string> RunAsync(string executable, string[] arguments, string directory, bool expectSuccess = true)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(context.CancellationToken);
        try
        {
            await process.WaitForExitAsync(context.CancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);

            throw;
        }

        string standardOutput = await output;
        string standardError = await error;
        if (expectSuccess)
        {
            Assert.AreEqual(0, process.ExitCode, $"{executable}: {standardOutput}{standardError}");
        }
        else
        {
            Assert.AreNotEqual(0, process.ExitCode, "The native compiler accepted an incompatible prototype.");
        }

        return expectSuccess ? standardOutput : standardOutput + standardError;
    }
}
