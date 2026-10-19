namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Unsupported native call shapes fail explicitly instead of guessing arguments or opaque storage.
    /// </summary>
    [TestMethod]
    [DataRow("extern int native_target;", false, "requires a function declaration")]
    [DataRow("extern int native_target(int first, ...);", true, "requires a fixed prototype")]
    [DataRow("extern int native_target();", true, "requires a fixed prototype")]
    [DataRow("struct Missing; extern struct Missing native_target(void);", true, "requires a complete object representation")]
    [DataRow("struct Missing; extern int native_target(struct Missing value);", true, "requires a complete object representation")]
    public async Task NativeCallBodiesRejectUnsupportedContracts(string headers, bool isFunction, string expected)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-call-shapes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(headers, [new("native_target", "native_target", isFunction)], directory);
            FormatException failure = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(records, headers));
            Assert.Contains(expected, failure.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Canonical output is independent of selection order, and the compiler rejects stale types or storage.
    /// </summary>
    [TestMethod]
    public async Task NativeCallBodiesRetainDeterministicCheckedContracts()
    {
        const string Declarations = "extern int native_first(int value); extern void native_second(void);";
        const string Headers = "#define PG_VERSION_NUM 180006\n" + Declarations;
        string directory = Path.Join(Path.GetTempPath(), $"ankus-call-checks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Declarations,
                [new("native_first", "native_first", true), new("native_second", "native_second", true)], directory);
            string expected = NativeBindingCallSource.Generate(records, Headers);
            NativeHeaderRecords reordered = records with
            {
                Headers = records.Headers with
                {
                    Symbols = records.Headers.Symbols.Reverse().ToDictionary()
                },
                Graph = records.Graph with
                {
                    Roots = records.Graph.Roots.Reverse().ToDictionary()
                },
            };
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(reordered, Headers));
            string file = Path.Join(directory, "checks.c");
            string compiler = await HostCompilers.ClangAsync();
            string[] options = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", file];
            string diagnostics = Path.Join(directory, "checks.txt");
            await File.WriteAllTextAsync(file, expected, context.CancellationToken);
            await NativeBindingHeaderCommand.CompileAsync(compiler, options, diagnostics, directory, context.CancellationToken, inspectBodies: true);
            await File.WriteAllTextAsync(file, expected + "\nint broken_body(void) { return missing_body_value; }\n", context.CancellationToken);
            await NativeBindingHeaderCommand.CompileAsync(compiler, options, diagnostics, directory, context.CancellationToken);
            InvalidOperationException body = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, options, diagnostics, directory, context.CancellationToken, inspectBodies: true));
            Assert.Contains("missing_body_value", body.Message);
            string changedHeader = Headers.Replace("int native_first(int value)", "float native_first(int value)", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, NativeBindingCallSource.Generate(records, changedHeader), context.CancellationToken);
            InvalidOperationException signature = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, options, diagnostics, directory, context.CancellationToken, inspectBodies: true));
            Assert.Contains("incompatible reconstructed native type: native_first", signature.Message);
            NativeRecordType[] changedTypes = [.. records.Graph.Types.Select(static type =>
                type.Kind == "scalar" && type.Name == "int" ? type with { Size = 8, Alignment = 8 } : type)];
            NativeHeaderRecords changedStorage = records with
            {
                Graph = records.Graph with
                {
                    Types = changedTypes
                }
            };
            await File.WriteAllTextAsync(file, NativeBindingCallSource.Generate(changedStorage, Headers), context.CancellationToken);
            InvalidOperationException storage = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, options, diagnostics, directory, context.CancellationToken, inspectBodies: true));
            Assert.Contains("Native call storage changed: ankus_native_call_native_first", storage.Message);
            foreach (NativeHeaderRecords changedTarget in (new[]
            {
                records.Headers.Target with { PostgresVersion = 180005 },
                records.Headers.Target with { RuntimeIdentifier = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64" },
            }).Select(target => records with
                {
                    Headers = records.Headers with
                    {
                        Target = target
                    },
                    Graph = records.Graph with
                    {
                        Target = target
                    },
                }))
            {
                await File.WriteAllTextAsync(file, NativeBindingCallSource.Generate(changedTarget, Headers), context.CancellationToken);
                InvalidOperationException mismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    NativeBindingHeaderCommand.CompileAsync(compiler, options, diagnostics, directory, context.CancellationToken, inspectBodies: true));
                Assert.Contains("Native call target changed", mismatch.Message);
            }

            NativeHeaderSymbol first = records.Headers.Symbols["native_first"];
            NativeHeaderFunction function = Assert.IsInstanceOfType<NativeHeaderFunction>(first.Type);
            Dictionary<string, NativeHeaderSymbol> symbols = records.Headers.Symbols.ToDictionary();
            symbols["native_first"] = first with
            {
                Type = function with
                {
                    Parameters = []
                }
            };
            FormatException count = Assert.ThrowsExactly<FormatException>(() =>
                NativeBindingCallSource.Generate(records with
                {
                    Headers = records.Headers with
                    {
                        Symbols = symbols
                    }
                }, Headers));
            Assert.Contains("parameter count", count.Message);
            symbols["native_first"] = first with
            {
                Type = function with
                {
                    Result = new NativeHeaderScalar("void")
                }
            };
            FormatException result = Assert.ThrowsExactly<FormatException>(() =>
                NativeBindingCallSource.Generate(records with
                {
                    Headers = records.Headers with
                    {
                        Symbols = symbols
                    }
                }, Headers));
            Assert.Contains("native_first.result", result.Message);
            Assert.Contains("scalar identity", result.Message);
            int aliasIndex = records.Graph.Types.Count;
            var cyclicAlias = new NativeRecordType("alias", records.Graph.Types[records.Graph.Roots["native_first"]].Canonical,
                "Cycle", NativeHeaderQualifiers.None, null, null, "Cycle", aliasIndex, null, null, null, "typedef int Cycle(int);");
            Dictionary<string, int> cyclicRoots = records.Graph.Roots.ToDictionary();
            cyclicRoots["native_first"] = aliasIndex;
            NativeHeaderRecords cyclic = records with
            {
                Graph = records.Graph with
                {
                    Roots = cyclicRoots,
                    Types = [.. records.Graph.Types, cyclicAlias]
                },
            };
            FormatException alias = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(cyclic, Headers));
            Assert.Contains("type shape", alias.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// An empty selection emits an inert protocol with no invented native call.
    /// </summary>
    [TestMethod]
    public async Task NativeCallBodiesAcceptEmptySelection()
    {
        Assert.AreEqual("", await ExecuteNativeCallsAsync("", [], "int main(void) { return 0; }"));
    }
}
