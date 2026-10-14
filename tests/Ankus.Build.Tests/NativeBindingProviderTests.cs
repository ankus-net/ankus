namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Used symbols require a real provider while unused absent declarations do not prevent object or archive linking.
    /// </summary>
    /// <param name="archive">Whether the native provider is resolved from a static archive.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeCallImportsResolveExplicitNativeProviders(bool archive)
    {
        const string Headers = "extern int provided_value(int value); extern int absent_value(int value);";
        const string Consumer = """
            typedef __SIZE_TYPE__ Size;
            typedef struct { const void *data; Size size; } Argument;
            typedef int (*Body)(const Argument *, Size, void *, Size);
            extern Body ankus_native_body_provided_value(void);
            int dispatch(int value, int *result)
            {
                Argument argument = { &value, sizeof(value) };
                return ankus_native_body_provided_value()(&argument, 1, result, sizeof(*result));
            }
            """;
        const string Main = """
            #include <stdio.h>
            extern int dispatch(int value, int *result);
            extern int provider_calls;
            int main(void)
            {
                int positive = 0, negative = 0;
                if (dispatch(14, &positive) != 0 || dispatch(-11, &negative) != 0) return 1;
                printf("%d|%d|%d\n", positive, negative, provider_calls);
                return 0;
            }
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-native-provider-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers,
                [new("provided_value", "provided_value", true), new("absent_value", "absent_value", true)], directory);
            byte[] consumer = await CompileNativeObjectAsync(Consumer);
            string source = NativeBindingCallImports.Generate(records, "#define PG_VERSION_NUM 180006\n" + Headers, consumer);
            string native = Path.Join(directory, "bodies.obj");
            string caller = Path.Join(directory, "caller.obj");
            string provider = Path.Join(directory, "provider with spaces.obj");
            string main = Path.Join(directory, "main.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "main.exe" : "main");
            await File.WriteAllBytesAsync(native, await CompileNativeObjectAsync(source), context.CancellationToken);
            await File.WriteAllBytesAsync(caller, consumer, context.CancellationToken);
            await File.WriteAllBytesAsync(provider, await CompileNativeObjectAsync(
                "int provider_calls; int provided_value(int value) { ++provider_calls; return value * 3 + 1; }"), context.CancellationToken);
            await File.WriteAllTextAsync(main, Main, context.CancellationToken);
            string compiler = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/WX", "/O2", "/Fe" + executable, "/Fo" + Path.ChangeExtension(main, ".obj"), main, caller, native]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", main, caller, native, "-o", executable];
            string failed = await RunAsync(compiler, arguments, directory, expectSuccess: false);
            Assert.Contains("provided_value", failed);
            if (archive)
            {
                string library = Path.Join(directory, OperatingSystem.IsWindows() ? "provider with spaces.lib" : "libprovider with spaces.a");
                string archiver = OperatingSystem.IsWindows() ? "lib.exe" : "ar";
                string[] options = OperatingSystem.IsWindows() ? ["/nologo", "/OUT:" + library, provider] : ["rcs", library, provider];
                await RunAsync(archiver, options, directory);
                provider = library;
            }

            await RunAsync(compiler, [.. arguments, provider], directory);
            Assert.AreEqual("43|-32|2\n", (await RunAsync(executable, [], directory)).ReplaceLineEndings("\n"));
            Assert.AreSequenceEqual<string>(["absent_value", "provided_value"], records.Graph.Roots.Keys);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
