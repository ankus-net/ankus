namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Forward typedefs resolve complete storage and preserve native calls after later tag declarations.
    /// </summary>
    [TestMethod]
    public async Task NativeCallsResolveForwardTagDefinitions()
    {
        const string Headers = """
            struct Entry;
            union Payload;
            typedef struct Entry *Handle;
            typedef union Payload Payload;
            struct Entry
            {
                int value;
            };

            union Payload
            {
                int value;
                double other;
            };

            struct Entry;
            union Payload;
            int native_forward(Handle entry, Payload *payload)
            {
                return entry->value + payload->value;
            }
            """;
        const string Main = """
            #include <stdio.h>
            int main(void)
            {
                struct Entry entry = { 19 };
                Payload payload = { 23 };
                Handle entry_address = &entry;
                Payload *payload_address = &payload;
                int result = -1;
                AnkusNativeCallArgument arguments[] = {{ &entry_address, sizeof(entry_address) }, { &payload_address, sizeof(payload_address) }};
                if (ankus_native_call_native_forward(arguments, 2, &result, sizeof(result)) != ANKUS_CALL_OK || result != 42)
                {
                    return 1;
                }

                puts("forward tag definitions retained");
                return 0;
            }
            """;
        Assert.AreEqual("forward tag definitions retained\n", await ExecuteNativeCallsAsync(Headers, ["native_forward"], Main));
    }

    /// <summary>
    /// Qualified multidimensional typedefs and function parameters retain C adjustment when compiled and executed.
    /// </summary>
    [TestMethod]
    public async Task NativeCallContractsPreserveCompilerTypeSemantics()
    {
        const string Headers = """
            typedef const int Matrix[2][3];
            int native_apply(Matrix values, int callback(int)) { return callback(values[0][1] + values[1][2]); }
            int triple(int value) { return value * 3; }
            """;
        const string Main = """
            #include <stdio.h>
            int main(void)
            {
                Matrix values = {{ 2, 5, 7 }, { 11, 13, 17 }};
                const int (*address)[3] = values;
                int (*callback)(int) = triple;
                int result = -1;
                AnkusNativeCallArgument arguments[] = {{ &address, sizeof(address) }, { &callback, sizeof(callback) }};
                if (ankus_native_call_native_apply(arguments, 2, &result, sizeof(result)) != ANKUS_CALL_OK) return 1;
                if (result != 66 || address != values || callback != triple) return 2;
                if (values[0][1] != 5 || values[1][2] != 17) return 3;
                puts("qualified arrays and adjusted callbacks retained");
                return 0;
            }
            """;
        Assert.AreEqual("qualified arrays and adjusted callbacks retained\n", await ExecuteNativeCallsAsync(Headers, ["native_apply"], Main));
    }

    /// <summary>
    /// Type identity and prototype metadata constrain calls independently of their object sizes.
    /// </summary>
    /// <param name="mutation">The incompatible declaration or measured metadata to replace.</param>
    /// <param name="reason">The rejected part of the native contract.</param>
    [TestMethod]
    [DataRow("typedef", "typedef identity")]
    [DataRow("convention", "calling convention")]
    [DataRow("variadic", "function prototype")]
    [DataRow("prototype", "function prototype")]
    [DataRow("declaration", "declaration kind")]
    [DataRow("complete", "tag identity or completeness")]
    [DataRow("nesting", "type nesting or alias cycle")]
    public async Task NativeCallContractsRejectChangedIdentity(string mutation, string reason)
    {
        const string Headers = "typedef int Number; struct Entry { int value; }; extern int native_value(Number value, struct Entry *entry); extern int native_empty(void);";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-signature-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers,
                [new("native_value", "native_value", true), new("native_empty", "native_empty", true)], directory);
            string expected = NativeBindingCallSource.Generate(records, Headers);
            NativeRecordType[] types = [.. records.Graph.Types];
            Dictionary<string, NativeHeaderSymbol> symbols = records.Headers.Symbols.ToDictionary();
            NativeHeaderSymbol symbol = symbols["native_value"];
            NativeHeaderFunction function = Assert.IsInstanceOfType<NativeHeaderFunction>(symbol.Type);
            int root = records.Graph.Roots[mutation == "prototype" ? "native_empty" : "native_value"];
            int canonical = types[root].Canonical;
            switch (mutation)
            {
                case "typedef":
                    NativeHeaderAlias alias = Assert.IsInstanceOfType<NativeHeaderAlias>(function.Parameters[0]);
                    symbols["native_value"] = symbol with
                    {
                        Type = function with
                        {
                            Parameters = [alias with { Name = "DifferentNumber" }, function.Parameters[1]]
                        }
                    };
                    break;
                case "convention":
                    types[canonical] = types[canonical] with
                    {
                        Function = types[canonical].Function! with
                        {
                            CallingConvention = 2
                        }
                    };
                    break;
                case "variadic":
                    types[canonical] = types[canonical] with
                    {
                        Function = types[canonical].Function! with
                        {
                            IsVariadic = true
                        }
                    };
                    break;
                case "prototype":
                    types[canonical] = types[canonical] with
                    {
                        Function = types[canonical].Function! with
                        {
                            HasPrototype = false
                        }
                    };
                    break;
                case "declaration":
                    symbols["native_value"] = symbol with
                    {
                        IsFunction = false
                    };
                    break;
                case "complete":
                    NativeHeaderPointer address = Assert.IsInstanceOfType<NativeHeaderPointer>(function.Parameters[1]);
                    NativeHeaderRecord entry = Assert.IsInstanceOfType<NativeHeaderRecord>(address.Element);
                    symbols["native_value"] = symbol with
                    {
                        Type = function with
                        {
                            Parameters = [function.Parameters[0], address with { Element = entry with { IsComplete = false } }]
                        }
                    };
                    break;
                case "nesting":
                    NativeHeaderType nested = function;
                    for (int depth = 0; depth < 128; depth++)
                    {
                        nested = new NativeHeaderQualified(nested, NativeHeaderQualifiers.None);
                    }

                    symbols["native_value"] = symbol with
                    {
                        Type = nested
                    };
                    break;
                default:
                    Assert.Fail("Unknown signature mutation.");
                    break;
            }

            NativeHeaderRecords changed = records with
            {
                Graph = records.Graph with
                {
                    Types = types
                },
                Headers = records.Headers with
                {
                    Symbols = symbols
                }
            };
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(changed, Headers));
            Assert.Contains(mutation == "prototype" ? "native_empty" : "native_value", error.Message);
            Assert.Contains(reason, error.Message);
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(records, Headers));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Changing a measured integer to equally sized floating-point or unsigned storage cannot produce a native body.
    /// </summary>
    /// <param name="replacement">The incompatible scalar representation with unchanged size and alignment.</param>
    [TestMethod]
    [DataRow("float")]
    [DataRow("unsigned int")]
    public async Task NativeCallContractsRejectSameSizeTypeChanges(string replacement)
    {
        const string Headers = "extern int native_value(int value);";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-signature-value-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, [new("native_value", "native_value", true)], directory);
            string expected = NativeBindingCallSource.Generate(records, Headers);
            NativeRecordType[] types = [.. records.Graph.Types.Select(type => type.Kind == "scalar" && type.Name == "int"
                ? type with { Name = replacement, Spelling = replacement } : type)];
            NativeHeaderRecords changed = records with
            {
                Graph = records.Graph with
                {
                    Types = types
                }
            };
            Assert.AreSequenceEqual(records.Graph.Types.Select(static type => (type.Size, type.Alignment)),
                changed.Graph.Types.Select(static type => (type.Size, type.Alignment)));
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(changed, Headers));
            Assert.Contains("native_value.result", error.Message);
            Assert.Contains("scalar identity", error.Message);
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(records, Headers));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Nested pointer, array, callback, tag and written qualifier differences reject before emitting source.
    /// </summary>
    /// <param name="declarations">Native tag declarations shared by the two prototypes.</param>
    /// <param name="left">The measured parameter declaration.</param>
    /// <param name="right">An incompatible parameter retained by the independent header observation.</param>
    /// <param name="reason">The expected incompatible part of the contract.</param>
    [TestMethod]
    [DataRow("", "int *value", "float *value", "scalar identity")]
    [DataRow("", "int **value", "const int **value", "type qualifiers")]
    [DataRow("", "int (*value)[2]", "int (*value)[3]", "array extent")]
    [DataRow("", "int value[2]", "int value[3]", "array extent")]
    [DataRow("", "int (*value)(int)", "int (*value)(float)", "scalar identity")]
    [DataRow("", "int (*value)(int)", "float (*value)(int)", "scalar identity")]
    [DataRow("", "int value", "volatile int value", "type qualifiers")]
    [DataRow("struct First { int value; }; struct Second { float value; };", "struct First value", "struct Second value", "tag identity")]
    [DataRow("struct First { int value; }; union Second { int value; };", "struct First value", "union Second value", "tag identity")]
    [DataRow("enum First { One = 1 }; enum Second { Two = 2 };", "enum First value", "enum Second value", "tag identity")]
    public async Task NativeCallContractsRejectDifferentParameterTypes(string declarations, string left, string right, string reason)
    {
        string headers = declarations + $"\nextern void native_value({left});\nextern void native_other({right});";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-signature-parameter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(headers,
                [new("native_value", "native_value", true), new("native_other", "native_other", true)], directory);
            string expected = NativeBindingCallSource.Generate(records, headers);
            Dictionary<string, NativeHeaderSymbol> symbols = records.Headers.Symbols.ToDictionary();
            symbols["native_value"] = symbols["native_value"] with
            {
                Type = symbols["native_other"].Type
            };
            NativeHeaderRecords changed = records with
            {
                Headers = records.Headers with
                {
                    Symbols = symbols
                }
            };
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(changed, headers));
            Assert.Contains("native_value.argument0", error.Message);
            Assert.Contains(reason, error.Message);
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(records, headers));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Neither the written function nor its canonical prototype can substitute another native argument representation.
    /// </summary>
    /// <param name="canonical">Whether to alter the canonical prototype instead of the written one.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeCallContractsValidateWrittenAndCanonicalTypes(bool canonical)
    {
        const string Headers = "typedef const int Number; extern int native_value(Number value); extern float native_other(float value); extern void native_keep(Number value);";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-signature-forms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers,
                [new("native_value", "native_value", true), new("native_other", "native_other", true), new("native_keep", "native_keep", true)], directory);
            string expected = NativeBindingCallSource.Generate(records, Headers);
            NativeRecordType[] types = [.. records.Graph.Types];
            int root = records.Graph.Roots["native_value"];
            int resolved = types[root].Canonical;
            Assert.AreNotEqual(root, resolved);
            int changedIndex = canonical ? resolved : root;
            int different = types[records.Graph.Roots["native_other"]].Function!.Parameters[0];
            types[changedIndex] = types[changedIndex] with
            {
                Function = types[changedIndex].Function! with
                {
                    Parameters = [different]
                }
            };
            NativeHeaderRecords changed = records with
            {
                Graph = records.Graph with
                {
                    Types = types
                }
            };
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(changed, Headers));
            Assert.Contains("native_value.argument0", error.Message);
            Assert.Contains("scalar identity", error.Message);
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(records, Headers));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Distinct anonymous records stay distinct when a typedef's canonical edge is corrupted without changing its written declaration.
    /// </summary>
    [TestMethod]
    public async Task NativeCallContractsPreserveAnonymousIdentity()
    {
        const string Headers = """
            typedef struct { int first; } First;
            typedef struct { int second; } Second;
            extern First native_first(First value);
            extern Second native_second(Second value);
            """;
        string directory = Path.Join(Path.GetTempPath(), $"ankus-signature-anonymous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers,
                [new("native_first", "native_first", true), new("native_second", "native_second", true)], directory);
            string expected = NativeBindingCallSource.Generate(records, Headers);
            NativeRecordType[] types = [.. records.Graph.Types];
            int first = Array.FindIndex(types, static type => type.Kind == "alias" && type.Name == "First");
            int second = Array.FindIndex(types, static type => type.Kind == "alias" && type.Name == "Second");
            Assert.IsGreaterThanOrEqualTo(0, first);
            Assert.IsGreaterThanOrEqualTo(0, second);
            Assert.AreNotEqual(types[first].Canonical, types[second].Canonical);
            Assert.AreEqual(types[first].Size, types[second].Size);
            types[first] = types[first] with
            {
                Canonical = types[second].Canonical
            };
            NativeHeaderRecords changed = records with
            {
                Graph = records.Graph with
                {
                    Types = types
                }
            };
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(changed, Headers));
            Assert.Contains("anonymous typedef identity", error.Message);
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(records, Headers));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Unselected signatures still constrain the complete graph, including an empty native-body selection.
    /// </summary>
    /// <param name="empty">Whether no native bodies are selected.</param>
    /// <param name="isFunction">Whether the unselected declaration is a function instead of a global.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task NativeCallContractsValidateUnselectedSymbols(bool empty, bool isFunction)
    {
        string headers = "extern int native_value(int value); extern float native_unused" + (isFunction ? "(float value);" : ";");
        string directory = Path.Join(Path.GetTempPath(), $"ankus-signature-unselected-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(headers,
                [new("native_value", "native_value", true), new("native_unused", "native_unused", isFunction)], directory);
            string[] selected = empty ? [] : ["native_value"];
            string expected = NativeBindingCallSource.Generate(records, headers, selected);
            NativeRecordType[] types = [.. records.Graph.Types.Select(static type => type.Kind == "scalar" && type.Name == "float"
                ? type with { Name = "unsigned int", Spelling = "unsigned int" } : type)];
            NativeHeaderRecords changed = records with
            {
                Graph = records.Graph with
                {
                    Types = types
                }
            };
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingCallSource.Generate(changed, headers, selected));
            Assert.Contains(isFunction ? "native_unused.result" : "native_unused'", error.Message);
            Assert.Contains("scalar identity", error.Message);
            Assert.AreEqual(expected, NativeBindingCallSource.Generate(records, headers, selected));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
