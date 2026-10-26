namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// On Windows, an extern global that the server does not export is unavailable, an exported one without
    /// <c>PGDLLIMPORT</c> links through its import slot, and every other declaration and every other target keeps its
    /// own name (#116).
    /// </summary>
    [TestMethod]
    public async Task WindowsGlobalsLinkThroughTheirExports()
    {
        const string Headers = """
            extern int marked;
            extern int marked_missing;
            extern int exported;
            extern int missing;
            extern _Thread_local int local_value;
            int defined_here;
            extern int function_value(int value);
            """;
        string[] names = ["marked", "marked_missing", "exported", "missing", "local_value", "defined_here"];
        NativeHeaderRequest[] requests = [.. names.Select(static name => new NativeHeaderRequest(name, name, false)),
            new("function_value", "function_value", true)];
        string directory = Path.Join(Path.GetTempPath(), $"ankus-windows-globals-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, requests, directory);
            var symbols = new Dictionary<string, NativeHeaderSymbol>(records.Headers.Symbols, StringComparer.Ordinal);
            symbols["marked"] = symbols["marked"] with { Attributes = ["DLLImportAttr"] };
            symbols["marked_missing"] = symbols["marked_missing"] with { Attributes = ["DLLImportAttr"] };
            HashSet<string> imports = ["__imp_marked", "__imp_exported", "__imp_local_value", "__imp_function_value", "function_value"];
            NativeHeaderTarget windows = records.Headers.Target with { RuntimeIdentifier = "win-x64" };
            IReadOnlyDictionary<string, NativeHeaderSymbol> linked = NativeBindingWindowsImports.Apply(symbols, windows, imports);
            Assert.AreSequenceEqual<string>(["defined_here", "exported", "function_value", "local_value", "marked", "marked_missing", "missing"], [.. linked.Keys]);
            Assert.AreSequenceEqual<NativeHeaderLinkage>([NativeHeaderLinkage.Direct, NativeHeaderLinkage.ImportSlot, NativeHeaderLinkage.Direct,
                NativeHeaderLinkage.Unavailable, NativeHeaderLinkage.Direct, NativeHeaderLinkage.Unavailable, NativeHeaderLinkage.Unavailable],
                [.. linked.Values.Select(static symbol => symbol.Linkage)]);
            Assert.AreSame<IReadOnlyDictionary<string, NativeHeaderSymbol>>(symbols, NativeBindingWindowsImports.Apply(symbols, records.Headers.Target with { RuntimeIdentifier = "linux-x64" }, imports));

            // 32-bit Windows decorates C names with an underscore, which the slot's name and its identifier both carry.
            NativeHeaderTarget x86 = records.Headers.Target with { RuntimeIdentifier = "win-x86" };
            HashSet<string> decorated = ["__imp__exported"];
            Assert.AreEqual(NativeHeaderLinkage.ImportSlot, NativeBindingWindowsImports.Apply(symbols, x86, decorated)["exported"].Linkage);
            Assert.AreEqual(NativeHeaderLinkage.Unavailable, NativeBindingWindowsImports.Apply(symbols, x86, imports)["exported"].Linkage);
            Assert.AreEqual("_imp__exported", NativeBindingWindowsImports.SlotIdentifier("exported", x86));
            Assert.AreEqual("__imp_exported", NativeBindingWindowsImports.SlotIdentifier("exported", windows));

            NativeHeaderRecords windowsRecords = records with { Headers = records.Headers with { Symbols = linked } };
            string source = NativeBindingGlobalSource.Bodies(windowsRecords, [new("exported", NativeBindingGlobalOperation.Read)]);
            Assert.Contains("extern __typeof__(exported) *__imp_exported;", source);
            Assert.Contains("memcpy(result, &(*__imp_exported), sizeof((*__imp_exported)));", source);
            string[] unavailable = ["missing", "marked_missing", "local_value"];
            foreach (string name in unavailable)
            {
                FormatException error = Assert.ThrowsExactly<FormatException>(() =>
                    NativeBindingGlobalSource.Bodies(windowsRecords, [new(name, NativeBindingGlobalOperation.Address)]));
                Assert.AreEqual($"Native global {name} does not permit Address access.", error.Message);
            }
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Managed globals read and write the variable behind an import slot without referring to its bare name, and the
    /// members of an unexported variable throw <see cref="PlatformNotSupportedException"/> and are marked unsupported
    /// on Windows without importing any native body (#116).
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task ManagedGlobalsReachWindowsImportSlots()
    {
        // Nothing defines imported_value or imported_constant, so the library fails to load if a body refers to either.
        const string Headers = """
            extern int imported_value;
            extern const int imported_constant;
            extern int unexported_value;
            static int imported_storage = -17;
            static const int constant_storage = 29;
            int *__imp_imported_value = &imported_storage;
            const int *__imp_imported_constant = &constant_storage;
            int imported_sum(void) { return imported_storage + constant_storage; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    int before = NativeGlobals.imported_value;
                    NativeGlobals.imported_value = 41;
                    int* address = NativeGlobals.DangerousAddressOf_imported_value();
                    int failures = 0;
                    const string Reason = "PostgreSQL 18 does not export unexported_value from postgres.exe, so an extension cannot reach it on Windows. " +
                        "Read a configuration setting with GetConfigOption instead.";
                    try { _ = NativeGlobals.unexported_value; }
                    catch (PlatformNotSupportedException error) when (error.Message == Reason) { failures++; }
                    try { NativeGlobals.unexported_value = 1; }
                    catch (PlatformNotSupportedException error) when (error.Message == Reason) { failures++; }
                    try { _ = NativeGlobals.DangerousAddressOf_unexported_value(); }
                    catch (PlatformNotSupportedException error) when (error.Message == Reason) { failures++; }
                    System.Reflection.MemberInfo[] members =
                        [typeof(NativeGlobals).GetProperty("unexported_value")!, typeof(NativeGlobals).GetMethod("DangerousAddressOf_unexported_value")!];
                    int marked = 0;
                    foreach (System.Reflection.MemberInfo member in members)
                    {
                        if (System.Attribute.GetCustomAttribute(member, typeof(System.Runtime.Versioning.UnsupportedOSPlatformAttribute)) is
                            System.Runtime.Versioning.UnsupportedOSPlatformAttribute { PlatformName: "windows", Message: Reason })
                        {
                            marked++;
                        }
                    }

                    return [before, *address, NativeGlobals.imported_value, NativeGlobals.imported_constant, NativeMethods.imported_sum(), failures, marked];
                }
            }
            """;
        HashSet<string> imports = ["__imp_imported_value", "__imp_imported_constant"];
        Assert.AreSequenceEqual<long>([-17, 41, 41, 29, 70, 3, 2], await ExecuteManagedCallsAsync(Headers, ["imported_sum"], Harness,
            globalNames: ["imported_value", "imported_constant", "unexported_value"],
            link: records => records with
            {
                Headers = records.Headers with
                {
                    Symbols = NativeBindingWindowsImports.Apply(records.Headers.Symbols, records.Headers.Target with { RuntimeIdentifier = "win-x64" }, imports),
                },
            }));
    }
}
