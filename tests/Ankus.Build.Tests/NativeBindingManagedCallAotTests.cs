using System.Globalization;
using System.Xml.Linq;
using Ankus.CompilerServices;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Native AOT resolves real LibraryImport accessors, trims unused methods and executes the exact selected bodies.
    /// </summary>
    [TestMethod]
    public async Task PublishedManagedCallsUseNativeAccessors()
    {
        const string Headers = """
            typedef struct Payload { unsigned char bytes[8192]; } Payload;
            int observed;
            volatile int ticks = 3;
            int calls;
            void consume(Payload value) { ++calls; observed = value.bytes[0] * 3 + value.bytes[8191]; }
            int checksum(void) { return observed + calls * 1000; }
            typedef int (*Arithmetic)(int);
            int add_five(int value) { return value + 5; }
            Arithmetic get_callback(void) { return add_five; }
            typedef int (*UnavailableCallback)(double);
            extern UnavailableCallback unavailable_callback;
            extern int unavailable(int value);
            extern int unavailable_global;
            """;
        const string Imports = """
            extern void *ankus_native_body_consume(void);
            extern void *ankus_native_body_checksum(void);
            void *first(void) { return ankus_native_body_consume(); }
            void *second(void) { return ankus_native_body_checksum(); }
            extern void *ankus_native_global_body_address_observed(void);
            extern void *ankus_native_global_body_read_ticks(void);
            extern void *ankus_native_global_body_write_ticks(void);
            void *third(void) { return ankus_native_global_body_read_ticks(); }
            void *fourth(void) { return ankus_native_global_body_write_ticks(); }
            void *fifth(void) { return ankus_native_global_body_address_observed(); }
            extern void *ankus_native_body_get_callback(void);
            void *sixth(void) { return ankus_native_body_get_callback(); }
            extern void *ankus_native_indirect_body___SIGNATURE__(void);
            void *seventh(void) { return ankus_native_indirect_body___SIGNATURE__(); }
            """;
        const string Main = """
            public static class Program
            {
                public static unsafe void Main()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    Payload payload = default;
                    payload.bytes[0] = 7;
                    payload.bytes[8191] = 201;
                    int rejected = 0;
                    scope.RejectCall = true;
                    try { NativeMethods.consume(payload); }
                    catch (PgException error) when (error.SqlState == "22023" && error.Message == "native call café"
                        && error.Detail == "detail naïve" && error.Hint == "hint déjà") { rejected++; }
                    scope.RejectCall = false;
                    NativeMethods.consume(payload);
                    int result = NativeMethods.checksum();
                    int original = NativeGlobals.observed;
                    NativeGlobals.observed = -41;
                    int* address = NativeGlobals.DangerousAddressOf_observed();
                    int indirect = NativeMethods.get_callback().Invoke(37);
                    int ticks = NativeGlobals.ticks;
                    NativeGlobals.ticks = ticks + 1;
                    Console.WriteLine($"{rejected},{result},{payload.bytes[0]},{payload.bytes[8191]},{original},{*address},{NativeMethods.checksum()},{indirect},{ticks},{NativeGlobals.ticks},{scope.Validations},{scope.Invocations}");
                }
            }
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-managed-call-aot-").FullName;
        try
        {
            string[] names = ["consume", "checksum", "get_callback", "unavailable"];
            string[] globals = ["observed", "ticks", "calls", "unavailable_global", "unavailable_callback"];
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers,
                [.. names.Select(static name => new NativeHeaderRequest(name, name, true)),
                    .. globals.Select(static name => new NativeHeaderRequest(name, name, false))], directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, names, globals);
            int signature = NativeBindingIndirectModel.Describe(records.Graph).Single(static call => call.Name == "Arithmetic").FunctionType;
            byte[] imported = await CompileNativeObjectAsync(Imports.Replace("__SIGNATURE__", signature.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            string native = NativeBindingCallImports.Generate(records, "#define PG_VERSION_NUM 180006\n" + Headers, imported);
            string file = Path.Join(directory, "calls.c");
            string nativeObject = Path.Join(directory, OperatingSystem.IsWindows() ? "calls.obj" : "calls.o");
            await File.WriteAllTextAsync(file, native, context.CancellationToken);
            string compiler = OperatingSystem.IsWindows() ? "cl.exe" : await HostCompilers.ClangAsync();
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/c", "/Fo" + nativeObject, file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", "-fPIC", "-c", file, "-o", nativeObject];
            await RunAsync(compiler, arguments, directory);
            await File.WriteAllTextAsync(Path.Join(directory, "Bindings.cs"), binding.Source, context.CancellationToken);
            await File.WriteAllTextAsync(Path.Join(directory, "Program.cs"),
                NativeBindingManagedCallHarness.Source.Replace("__EXPECTED_IDENTITY__", binding.AbiIdentity, StringComparison.Ordinal) + "\n" + Main,
                context.CancellationToken);
            var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net10.0"),
                    new XElement("PublishAot", "true"), new XElement("AllowUnsafeBlocks", "true"), new XElement("Nullable", "enable"),
                    new XElement("TreatWarningsAsErrors", "true"), new XElement("MSTestAnalysisMode", "All")),
                new XElement("ItemGroup",
                    new XElement("Reference", new XAttribute("Include", "Ankus.Runtime"), new XElement("HintPath", typeof(NativeRawCall).Assembly.Location)),
                    new XElement("Reference", new XAttribute("Include", "System.Formats.Cbor"),
                        new XElement("HintPath", typeof(System.Formats.Cbor.CborReader).Assembly.Location)),
                    new XElement("DirectPInvoke", new XAttribute("Include", "Ankus.NativeBodies")),
                    new XElement("NativeLibrary", new XAttribute("Include", nativeObject))));
            string projectFile = Path.Join(directory, "Calls.csproj");
            await File.WriteAllTextAsync(projectFile, project.ToString(), context.CancellationToken);
            await TestDotnetSdk.ConfigureAsync(directory, context.CancellationToken);
            Assert.AreEqual(TestDotnetSdk.Version, (await RunAsync("dotnet", ["--version"], directory)).Trim());
            string output = Path.Join(directory, "published");
            await RunAsync("dotnet", ["publish", projectFile, "-c", "Release", "-o", output], directory);
            string executable = Path.Join(output, OperatingSystem.IsWindows() ? "Calls.exe" : "Calls");
            Assert.AreEqual("1,1222,7,201,222,-41,959,42,3,4,12,12\n", (await RunAsync(executable, [], directory)).ReplaceLineEndings("\n"));
            string objectName = OperatingSystem.IsWindows() ? "Calls.obj" : "Calls.o";
            string compiled = Assert.ContainsSingle(Directory.EnumerateFiles(Path.Join(directory, "obj"), objectName, SearchOption.AllDirectories));
            byte[] image = await File.ReadAllBytesAsync(compiled, context.CancellationToken);
            NativeObjectImports actual = NativeObjectSymbols.Read(image, NativeBindingCallImports.Prefix);
            Assert.AreSequenceEqual<string>(["ankus_native_body_checksum", "ankus_native_body_consume", "ankus_native_body_get_callback"], actual.Symbols);
            // A plain global is copied in place through its address; a volatile one keeps its guarded read and write bodies.
            Assert.AreSequenceEqual<NativeBindingGlobalAccess>([new("observed", NativeBindingGlobalOperation.Address),
                new("ticks", NativeBindingGlobalOperation.Read), new("ticks", NativeBindingGlobalOperation.Write)], NativeBindingGlobalImports.Select(image));
            Assert.AreSequenceEqual<int>([signature], NativeBindingIndirectImports.Select(image));
            Assert.AreEqual(native, NativeBindingCallImports.Generate(records, "#define PG_VERSION_NUM 180006\n" + Headers, image));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
