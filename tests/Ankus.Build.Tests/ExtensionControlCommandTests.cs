using Ankus.PgConfig;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies primary metadata generation without executing the consumer or invoking a native compiler.
/// </summary>
/// <param name="context">The current cancellation context.</param>
[TestClass]
public sealed class ExtensionControlCommandTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus control metadata ").FullName;

    /// <summary>
    /// Removes the test's assembly and control files.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Authored and generated controls share publication rules while retaining installation SQL exactly.
    /// </summary>
    [TestMethod]
    public void PrimaryPackageRetainsIdentityAndSql()
    {
        const string Sql = "SELECT 'café 🐘', 'MODULE_PATHNAME';\r\n";
        IReadOnlyDictionary<string, string> original = ExtensionPackage.Create("query_probe", "1.2", "Query.so", Sql, true);
        IReadOnlyDictionary<string, string> absent = ExtensionPackage.Create("query_probe", "1.2", "Query.so", Sql, true, null, 18);
        Assert.HasCount(2, absent);
        Assert.AreEqual(original["query_probe.control"], absent["query_probe.control"]);
        Assert.AreEqual(Sql, absent["query_probe--1.2.sql"]);
        IReadOnlyDictionary<string, string> authored = ExtensionPackage.Create("query_probe", "1.2", "Query.so", Sql, true,
            "comment='a = b # value'\nschema='fixed schema'\ntrusted=yes\ndirectory=''", 18);
        IReadOnlyDictionary<string, string> control = ExtensionControlFile.Parse(authored["query_probe.control"]);
        Assert.AreEqual("1.2", control["default_version"]);
        Assert.AreEqual("Query.so", control["module_pathname"]);
        Assert.AreEqual("UTF8", control["encoding"]);
        Assert.AreEqual("a = b # value", control["comment"]);
        Assert.AreEqual("fixed schema", control["schema"]);
        Assert.AreEqual("false", control["relocatable"]);
        Assert.AreEqual("true", control["trusted"]);
        Assert.AreEqual("", control["directory"]);
        Assert.AreEqual(Sql, authored["query_probe--1.2.sql"]);
        Assert.ThrowsExactly<FormatException>(() => ExtensionPackage.Create("query_probe", "1.2", "Query.so", Sql, true,
            "default_version='wrong'", 18));
        Assert.ThrowsExactly<FormatException>(() => ExtensionPackage.Create("query_probe", "1.2", "Query.so", Sql, true,
            "no_relocate='helper'", 15));
    }

    /// <summary>
    /// Compiled metadata is inspected without running the module initializer, loading native code or touching source controls.
    /// </summary>
    /// <param name="relocatable">The generated relocation contract.</param>
    /// <param name="major">The first, an interior or the last supported PostgreSQL major.</param>
    [TestMethod]
    [DataRow(true, 13)]
    [DataRow(true, 18)]
    [DataRow(false, 18)]
    [DataRow(false, 19)]
    public async Task ControlCommandReadsManagedMetadataOnly(bool relocatable, int major)
    {
        string[] arguments = Prepare(relocatable);
        arguments[2] = major.ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] assembly = await File.ReadAllBytesAsync(arguments[0], context.CancellationToken);
        const string Author = "comment='first'\ncomment='  final = ''quoted'' # comment  '\nrequires=''";
        await File.WriteAllTextAsync(arguments[6], Author, context.CancellationToken);
        await ExtensionControlCommand.RunAsync(arguments);
        IReadOnlyDictionary<string, string> control = ExtensionControlFile.Read(arguments[1]);
        Assert.AreEqual("  final = 'quoted' # comment  ", control["comment"]);
        Assert.AreEqual("", control["requires"]);
        Assert.AreEqual(relocatable ? "true" : "false", control["relocatable"]);
        Assert.AreEqual("Query.so", control["module_pathname"]);
        Assert.AreEqual("1.2", control["default_version"]);
        Assert.AreEqual(Author, await File.ReadAllTextAsync(arguments[6], context.CancellationToken));
        Assert.AreSequenceEqual(assembly, await File.ReadAllBytesAsync(arguments[0], context.CancellationToken));
        Assert.AreEqual(arguments[1], Assert.ContainsSingle(Directory.GetFiles(Path.GetDirectoryName(arguments[1])!)));
        arguments[6] = "";
        await ExtensionControlCommand.RunAsync(arguments);
        Assert.HasCount(4, ExtensionControlFile.Read(arguments[1]));
    }

    /// <summary>
    /// Invalid metadata and author input preserve prior output and never leave a temporary replacement.
    /// </summary>
    /// <param name="failure">The invalid input partition.</param>
    [TestMethod]
    [DataRow("control")]
    [DataRow("missing-control")]
    [DataRow("assembly")]
    [DataRow("identity")]
    public async Task ControlCommandFailurePreservesPreviousOutput(string failure)
    {
        string[] arguments = Prepare(false);
        Directory.CreateDirectory(Path.GetDirectoryName(arguments[1])!);
        await File.WriteAllTextAsync(arguments[1], "prior output", context.CancellationToken);
        switch (failure)
        {
            case "control":
                await File.WriteAllTextAsync(arguments[6], "relocatable=true", context.CancellationToken);
                await Assert.ThrowsExactlyAsync<FormatException>(() => ExtensionControlCommand.RunAsync(arguments));
                break;
            case "missing-control":
                File.Delete(arguments[6]);
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => ExtensionControlCommand.RunAsync(arguments));
                break;
            case "assembly":
                await File.WriteAllTextAsync(arguments[0], "invalid managed image", context.CancellationToken);
                await Assert.ThrowsExactlyAsync<BadImageFormatException>(() => ExtensionControlCommand.RunAsync(arguments));
                break;
            default:
                arguments[3] = "../outside";
                await Assert.ThrowsExactlyAsync<ArgumentException>(() => ExtensionControlCommand.RunAsync(arguments));
                break;
        }

        Assert.AreEqual("prior output", await File.ReadAllTextAsync(arguments[1], context.CancellationToken));
        Assert.AreEqual(arguments[1], Assert.ContainsSingle(Directory.GetFiles(Path.GetDirectoryName(arguments[1])!)));
    }

    /// <summary>
    /// The output may not replace either source input, even when valid control generation would succeed.
    /// </summary>
    /// <param name="input">The assembly or author control argument.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(6)]
    public async Task ControlCommandRejectsInputReplacement(int input)
    {
        string[] arguments = Prepare(true);
        arguments[input] = Path.GetRelativePath(Environment.CurrentDirectory, arguments[input]);
        string absolute = Path.GetFullPath(arguments[input]);
        arguments[1] = OperatingSystem.IsWindows() ? absolute.ToUpperInvariant() : absolute;
        byte[] expected = await File.ReadAllBytesAsync(arguments[input], context.CancellationToken);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ExtensionControlCommand.RunAsync(arguments));
        Assert.AreSequenceEqual(expected, await File.ReadAllBytesAsync(arguments[input], context.CancellationToken));
    }

    /// <summary>
    /// Versioned libraries omit module_pathname, reject an authored one, and require a library named for the version.
    /// </summary>
    [TestMethod]
    public async Task VersionedLibraryControlOmitsModulePathname()
    {
        string[] arguments = Prepare(true);
        arguments[5] = "Query-1.2.so";
        await ExtensionControlCommand.RunAsync([.. arguments, "true"]);
        IReadOnlyDictionary<string, string> control = ExtensionControlFile.Read(arguments[1]);
        Assert.IsFalse(control.ContainsKey("module_pathname"));
        Assert.AreEqual("1.2", control["default_version"]);
        Assert.AreEqual("author", control["comment"]);
        Assert.AreEqual("Query-1.2.so", ExtensionControlFile.Parse(ExtensionPackage.Create("query_probe", "1.2", "Query-1.2.so", "SELECT 1;",
            true)["query_probe.control"])["module_pathname"]);

        await File.WriteAllTextAsync(arguments[6], "module_pathname='Query-1.2.so'", context.CancellationToken);
        FormatException authored = await Assert.ThrowsExactlyAsync<FormatException>(() => ExtensionControlCommand.RunAsync([.. arguments, "true"]));
        Assert.Contains("versioned native library", authored.Message);
        await ExtensionControlCommand.RunAsync([.. arguments, "false"]);
        Assert.AreEqual("Query-1.2.so", ExtensionControlFile.Read(arguments[1])["module_pathname"]);

        arguments[5] = "Query.so";
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ExtensionControlCommand.RunAsync([.. arguments, "true"]));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ExtensionControlCommand.RunAsync([.. arguments, "yes"]));
    }

    /// <summary>
    /// Malformed command lines and unsupported majors fail without filesystem writes.
    /// </summary>
    [TestMethod]
    public async Task ControlCommandRejectsInvalidArguments()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ExtensionControlCommand.RunAsync([]));
        foreach (string major in new[] { "12", "20" })
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
                ExtensionControlCommand.RunAsync(["missing", Path.Join(_root, "output"), major, "probe", "1", "Probe.so", ""]));
        }

        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    private string[] Prepare(bool relocatable)
    {
        string source = $$"""
            using System;
            using System.Reflection;
            using System.Runtime.CompilerServices;
            [assembly: AssemblyMetadata("Ankus.NativeSource", "invalid C: must not compile")]
            [assembly: AssemblyMetadata("Ankus.Sql", "SELECT 'MODULE_PATHNAME';")]
            [assembly: AssemblyMetadata("Ankus.Exports", "unused_export")]
            [assembly: AssemblyMetadata("Ankus.Relocatable", "{{(relocatable ? "true" : "false")}}")]
            internal static class Consumer
            {
                [ModuleInitializer]
                internal static void Initialize() => throw new InvalidOperationException("Consumer must not execute");
            }
            """;
        CSharpCompilation compilation = CSharpCompilation.Create("ControlMetadataProbe",
            [CSharpSyntaxTree.ParseText(source, cancellationToken: context.CancellationToken)],
            CompilationReferences.Platform,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        string assembly = Path.Join(_root, "Query.dll");
        using (FileStream stream = File.Create(assembly))
        {
            EmitResult emitted = compilation.Emit(stream, cancellationToken: context.CancellationToken);
            Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        string authored = Path.Join(_root, "author.control");
        File.WriteAllText(authored, "comment='author'");
        return [assembly, Path.Join(_root, "output", "query.control"), "18", "query_probe", "1.2", "Query.so", authored];
    }
}
