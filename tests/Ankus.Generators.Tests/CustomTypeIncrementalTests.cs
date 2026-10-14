using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Body edits and declaration movement reuse all type artifacts while executing the current constructor.
    /// </summary>
    /// <param name="move">Whether to move the declaration rather than change its constructor body.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomTypeArtifactsCacheIndependentImplementationEdits(bool move)
    {
        const string Source = """
            [Ankus.PgType] public sealed class Value
            {
                public int Number { get; }
                [System.Text.Json.Serialization.JsonConstructor]
                public Value(int number)
                {
                    Number = number + 1;
                }
            }
            [Ankus.PgType] public readonly record struct Other(int Number);
            """;
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string editedSource = move ? "\n\n" + Source : Source.Replace("number + 1", "number + 2", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            editedSource, path: "Moved.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        foreach (string stage in CustomTypeRenderingStages())
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "value").Reason, stage);
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "other").Reason, stage);
        }

        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.Contains("Moved.cs:", ManifestValue(second, "Ankus.Sql"));
        string[] actual = RunSerializedProbe<string[]>(editedSource, """
            return new[] { codec.Parse("{\"Number\":41}").Number.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            """);
        Assert.AreSequenceEqual([move ? "42" : "43"], actual);
    }

    /// <summary>
    /// Storage member contracts invalidate the actual codec while leaving registration and I/O rendering cached.
    /// </summary>
    /// <param name="kind">The scalar width, persisted name or reference nullability edit.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void CustomTypeSerializerTracksMemberContractsIndependently(int kind)
    {
        string source = kind switch
        {
            0 => "[Ankus.PgType] public sealed class Value { public int Number { get; set; } }",
            1 => "[Ankus.PgType] public sealed class Value { [System.Text.Json.Serialization.JsonPropertyName(\"a\")] public int Number { get; set; } }",
            _ => "[Ankus.PgType] public sealed class Value { public string? Name { get; set; } }",
        };
        string changed = kind switch
        {
            0 => source.Replace("int Number", "long Number", StringComparison.Ordinal),
            1 => source.Replace("\"a\"", "\"b\"", StringComparison.Ordinal),
            _ => source.Replace("string? Name", "string Name", StringComparison.Ordinal).Replace("get; set; }", "get; set; } = \"ok\";", StringComparison.Ordinal),
        };
        const string Other = "[Ankus.PgType] public readonly record struct Other(int Number);";
        CSharpCompilation initial = ModuleCompilation(source + Other);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            changed + Other, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomTypeArtifact(driver, "CustomTypeSerializerEmission", "value").Reason);
        foreach (string stage in CustomTypeRenderingStages().Where(static stage => stage != "CustomTypeSerializerEmission"))
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "value").Reason, stage);
        }

        foreach (string stage in CustomTypeRenderingStages())
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "other").Reason, stage);
        }

        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        string probe = kind switch
        {
            0 => "return new[] { codec.Format(codec.Parse(\"{\\\"Number\\\":2147483648}\")) };",
            1 => "return new[] { codec.Format(codec.Parse(\"{\\\"b\\\":7}\")) };",
            _ => "return new[] { codec.Format(codec.Parse(\"{\\\"Name\\\":\\\"ok\\\"}\")) };",
        };
        string[] actual = RunSerializedProbe<string[]>(changed, probe);
        Assert.AreSequenceEqual([kind switch { 0 => "{\"Number\":2147483648}", 1 => "{\"b\":7}", _ => "{\"Name\":\"ok\"}" }], actual);
    }

    /// <summary>
    /// Binary, alignment and null-input policy edits invalidate I/O without rerendering unchanged storage.
    /// </summary>
    /// <param name="kind">The binary protocol, alignment or null-input policy edit.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void CustomTypeIoTracksCatalogPolicyIndependently(int kind)
    {
        const string Source = "[Ankus.PgType(BinaryProtocol = true)] public readonly record struct Value(int Number); [Ankus.PgType] public readonly record struct Other(int Number);";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string policy = kind switch
        {
            0 => "BinaryProtocol = false",
            1 => "BinaryProtocol = true, Alignment = Ankus.PgTypeAlignment.EightBytes",
            _ => "BinaryProtocol = true, NullInputErrorMessage = \"no value\"",
        };
        string changed = Source.Replace("BinaryProtocol = true", policy, StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            changed, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomTypeArtifact(driver, "CustomTypeIoEmission", "value").Reason);
        foreach (string stage in CustomTypeRenderingStages().Where(static stage => stage != "CustomTypeIoEmission"))
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "value").Reason, stage);
        }

        foreach (string stage in CustomTypeRenderingStages())
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "other").Reason, stage);
        }

        PgTypeEmitter.Emission io = Assert.IsInstanceOfType<PgTypeEmitter.Emission>(TrackedCustomTypeArtifact(driver, "CustomTypeIoEmission", "value").Value);
        Assert.HasCount(kind == 0 ? 2 : 4, io.Native);
        Assert.AreNotEqual(InstallationBody(first), InstallationBody(second));
        if (kind == 0)
        {
            Assert.DoesNotContain("value_recv", io.Sql);
            Assert.DoesNotContain("value_send", io.Sql);
        }
        else if (kind == 1)
        {
            Assert.Contains("ALIGNMENT = double", io.Sql);
        }
        else
        {
            Assert.Contains("CALLED ON NULL INPUT", io.Sql);
            Assert.Contains("no value", io.Managed);
        }
    }

    /// <summary>
    /// Name and inherited schema edits update catalog boundaries without changing storage encoding.
    /// </summary>
    /// <param name="schema">Whether the enclosing schema changes rather than the explicit type name.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomTypeEmissionTracksCatalogIdentityIndependently(bool schema)
    {
        string source = schema ? "[Ankus.PgSchema(\"first\")] public static class Types { [Ankus.PgType] public readonly record struct Value(int Number); }"
            : "[Ankus.PgType(Name = \"first\")] public readonly record struct Value(int Number);";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source.Replace("first", "second", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        string name = schema ? "value" : "second";

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, "CustomTypeSerializerEmission", name).Reason);
        foreach (string stage in CustomTypeRenderingStages().Where(static stage => stage != "CustomTypeSerializerEmission"))
        {
            Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomTypeArtifact(driver, stage, name).Reason, stage);
        }

        Assert.Contains(schema ? "CREATE TYPE \"second\".\"value\"" : "CREATE TYPE \"second\"", InstallationBody(second));
        Assert.DoesNotContain("\"first\"", InstallationBody(second));
        Assert.AreNotEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
    }

    /// <summary>
    /// Invalid declarations report cached errors on the current tree and repair into complete usable output.
    /// </summary>
    [TestMethod]
    public void CustomTypeDiagnosticsFollowCurrentTreesAndRecover()
    {
        const string Source = "[Ankus.PgType(Alignment = (Ankus.PgTypeAlignment)3)] public readonly record struct Value(int Number);";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> firstErrors, context.CancellationToken);
        Assert.AreEqual("ANKUS419", Assert.ContainsSingle(firstErrors).Id);
        SyntaxTree current = CSharpSyntaxTree.ParseText(Source + "\n// independent edit", path: "Module.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out Compilation failed, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        Assert.AreEqual("ANKUS419", error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual(Assert.ContainsSingle(firstErrors).Location.SourceSpan, error.Location.SourceSpan);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "CustomTypeModel"));
        foreach (string stage in CustomTypeRenderingStages())
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, stage));
        }

        Assert.DoesNotContain("CREATE TYPE \"value\"", InstallationBody(failed));
        CSharpCompilation repaired = edited.ReplaceSyntaxTree(current, CSharpSyntaxTree.ParseText(
            Source.Replace("(Ankus.PgTypeAlignment)3", "Ankus.PgTypeAlignment.FourBytes", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomTypeArtifact(driver, "CustomTypeIoEmission", "value").Reason);
        Assert.Contains("CREATE TYPE \"value\"", InstallationBody(output));
    }

    /// <summary>
    /// Polymorphic tag changes affect actual wire values without invalidating unchanged catalog artifacts.
    /// </summary>
    /// <param name="text">Whether an integer tag changes to text rather than another integer.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomTypeSerializerTracksPolymorphicTagsIndependently(bool text)
    {
        const string Source = """
            [Ankus.PgType]
            [System.Text.Json.Serialization.JsonDerivedType(typeof(Variant), 42)]
            public abstract record Value;
            public sealed record Variant(int Number) : Value;
            """;
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string changed = Source.Replace("Variant), 42", text ? "Variant), \"42\"" : "Variant), 43", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            changed, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomTypeArtifact(driver, "CustomTypeSerializerEmission", "value").Reason);
        foreach (string stage in CustomTypeRenderingStages().Where(static stage => stage != "CustomTypeSerializerEmission"))
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "value").Reason, stage);
        }

        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        string[] actual = RunSerializedProbe<string[]>(changed, "return new[] { codec.Format(new Variant(7)) };");
        Assert.AreSequenceEqual([text ? "{\"$type\":\"42\",\"Number\":7}" : "{\"$type\":43,\"Number\":7}"], actual);
    }

    /// <summary>
    /// Native payload width changes registration and storage size while scalar I/O conversion stays cached.
    /// </summary>
    [TestMethod]
    public void CustomTypeNativeLayoutTracksValidatedPayloadSizeIndependently()
    {
        CSharpCompilation initial = ModuleCompilation(VarlenaOwnershipValueSource);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            VarlenaOwnershipValueSource.Replace("public int Number", "public long Number", StringComparison.Ordinal),
            path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomTypeArtifact(driver, "CustomTypeRegistrationEmission", "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomTypeArtifact(driver, "CustomTypeSerializerEmission", "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, "CustomTypeCatalogEmission", "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, "CustomTypeIoEmission", "value").Reason);
        Assert.Contains(", 8, [], static", Assert.IsInstanceOfType<string>(TrackedCustomTypeArtifact(driver, "CustomTypeRegistrationEmission", "value").Value));
        Assert.Contains("_codec = new(8,", Assert.IsInstanceOfType<string>(TrackedCustomTypeArtifact(driver, "CustomTypeSerializerEmission", "value").Value));
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
    }

    /// <summary>
    /// Extension initialization is composed into cached I/O boundaries using the current phase inventory.
    /// </summary>
    [TestMethod]
    public void CustomTypeIoComposesCurrentExtensionInitialization()
    {
        CSharpCompilation initial = ModuleCompilation("[Ankus.PgType] public readonly record struct Value(int Number);");
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "public static class Lifecycle { [Ankus.PgInitialize] public static void Initialize() { } }",
            path: "Lifecycle.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        foreach (string stage in CustomTypeRenderingStages())
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "value").Reason, stage);
        }

        PgTypeEmitter.Emission io = Assert.IsInstanceOfType<PgTypeEmitter.Emission>(TrackedCustomTypeArtifact(driver, "CustomTypeIoEmission", "value").Value);
        foreach (NativeFunctionEmission boundary in io.Native)
        {
            Assert.Contains((boundary.Header + boundary.Body).ReplaceLineEndings("\n"), ManifestValue(first, "Ankus.NativeSource"));
            Assert.Contains((boundary.Header + "    ankus_ensure_initialized();\n" + boundary.Body).ReplaceLineEndings("\n"), ManifestValue(second, "Ankus.NativeSource"));
        }

        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
    }

    /// <summary>
    /// SQL enablement and replacement policy affect composition without changing cached type rendering.
    /// </summary>
    /// <param name="replacement">Whether to replace SQL rather than disable its generation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomTypeGraphPolicyPreservesRenderedContracts(bool replacement)
    {
        const string Source = "[Ankus.PgType(GenerateSql = true)] public readonly record struct Value(int Number);";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string policy = replacement ? "Sql = \"SELECT '@INPUT_FUNCTION_NAME@';\", SqlRelocatable = true" : "GenerateSql = false";
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Source.Replace("GenerateSql = true", policy, StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        foreach (string stage in CustomTypeRenderingStages())
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, stage, "value").Reason, stage);
        }

        Assert.Contains("CREATE TYPE \"value\"", InstallationBody(first));
        Assert.AreEqual(replacement ? "SELECT '" + CustomTypeContract(driver, "value").NativeFunction("in") + "';\n" : "-- No installable objects declared.\n", InstallationBody(second));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
    }

    /// <summary>
    /// Assembly identity changes all generated codec and native identities while keeping catalog checking cached.
    /// </summary>
    [TestMethod]
    public void CustomTypeArtifactsTrackAssemblyIdentity()
    {
        CSharpCompilation initial = ModuleCompilation("[Ankus.PgType] public readonly record struct Value(int Number);");
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string previous = CustomTypeContract(driver, "value").Symbol;
        driver = RunModule(driver, initial.WithAssemblyName("RenamedExtension"), out Compilation second);
        CustomTypeModel current = CustomTypeContract(driver, "value");

        Assert.AreNotEqual(previous, current.Symbol);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomTypeArtifact(driver, "CustomTypeCatalogEmission", "value").Reason);
        foreach (string stage in CustomTypeRenderingStages().Where(static stage => stage != "CustomTypeCatalogEmission"))
        {
            Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomTypeArtifact(driver, stage, "value").Reason, stage);
        }

        Assert.DoesNotContain(previous, ManifestValue(second, "Ankus.NativeSource"));
        Assert.Contains(current.Symbol, ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreNotEqual(InstallationBody(first), InstallationBody(second));
    }

    /// <summary>
    /// Removing a declaration removes its complete artifacts and preserves every surviving generated contract.
    /// </summary>
    [TestMethod]
    public void CustomTypeRemovalDropsOnlyItsGeneratedContracts()
    {
        CSharpCompilation initial = ModuleCompilation("[Ankus.PgType] public readonly record struct Value(int Number);").AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("[Ankus.PgType] public readonly record struct Other(int Number);", path: "Other.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string previous = CustomTypeContract(driver, "value").Symbol;
        Dictionary<string, object> survivor = CustomTypeRenderingStages().ToDictionary(static stage => stage,
            stage => TrackedCustomTypeArtifact(driver, stage, "other").Value);
        driver = RunModule(driver, initial.RemoveSyntaxTrees(initial.SyntaxTrees.First()), out Compilation second);

        Assert.Contains("CREATE TYPE \"value\"", InstallationBody(first));
        Assert.DoesNotContain("\"value\"", InstallationBody(second));
        Assert.Contains("CREATE TYPE \"other\"", InstallationBody(second));
        Assert.DoesNotContain(previous, ManifestValue(second, "Ankus.NativeSource"));
        Assert.DoesNotContain(previous, string.Join("\n", second.SyntaxTrees.Select(tree => tree.GetText(context.CancellationToken).ToString())));
        foreach (string stage in CustomTypeRenderingStages())
        {
            Assert.AreEqual(survivor[stage], TrackedCustomTypeArtifact(driver, stage, "other").Value, stage);
        }
    }

    /// <summary>
    /// Lists the independently rendered registration, storage, catalog and I/O cache stages.
    /// </summary>
    private static string[] CustomTypeRenderingStages() => ["CustomTypeRegistrationEmission", "CustomTypeSerializerEmission", "CustomTypeCatalogEmission", "CustomTypeIoEmission"];

    /// <summary>
    /// Reads the exact current semantic contract from the production generator's tracked models.
    /// </summary>
    private static CustomTypeModel CustomTypeContract(GeneratorDriver driver, string name)
        => Assert.ContainsSingle(Assert.ContainsSingle(driver.GetRunResult().Results).TrackedSteps["CustomTypeModel"].SelectMany(static step => step.Outputs)
            .Where(static output => output.Reason != IncrementalStepRunReason.Removed).Select(static output => output.Value)
            .OfType<CustomTypeModel>().Where(value => value.Name == name));

    /// <summary>
    /// Reads a production cache decision using the current validated type's exact contract identity.
    /// </summary>
    private static (object Value, IncrementalStepRunReason Reason) TrackedCustomTypeArtifact(GeneratorDriver driver, string stage, string name)
    {
        GeneratorRunResult result = Assert.ContainsSingle(driver.GetRunResult().Results);
        CustomTypeModel model = CustomTypeContract(driver, name);
        IEnumerable<(object Value, IncrementalStepRunReason Reason)> artifacts = result.TrackedSteps[stage].SelectMany(static step => step.Outputs)
            .Where(static output => output.Reason != IncrementalStepRunReason.Removed);
        string identity = stage switch
        {
            "CustomTypeRegistrationEmission" => "<" + model.Managed + ">",
            "CustomTypeSerializerEmission" => "class Codec_" + model.Symbol,
            "CustomTypeCatalogEmission" => "{ { .data = (unsigned char *) \"" + string.Concat(System.Text.Encoding.UTF8.GetBytes(model.Name).Select(static value => "\\x" + value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture))) + "\"",
            _ => "ankus_managed_" + model.Symbol + "_type_",
        };
        return Assert.ContainsSingle(artifacts.Where(output => output.Value is PgTypeEmitter.Emission io
            ? io.Managed.Contains(identity, StringComparison.Ordinal)
            : output.Value is string text && text.Contains(identity, StringComparison.Ordinal)));
    }
}
