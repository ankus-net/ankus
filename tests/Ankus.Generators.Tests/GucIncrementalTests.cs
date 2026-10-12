using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Every configuration kind reuses its actual rendering after a hook body changes or a declaration moves.
    /// </summary>
    /// <param name="kind">The configuration transport kind.</param>
    /// <param name="move">Whether the declaration moves rather than changing an implementation.</param>
    [TestMethod]
    [DataRow("boolean", false)]
    [DataRow("boolean", true)]
    [DataRow("integer", false)]
    [DataRow("integer", true)]
    [DataRow("real", false)]
    [DataRow("real", true)]
    [DataRow("string", false)]
    [DataRow("string", true)]
    [DataRow("enum", false)]
    [DataRow("enum", true)]
    public void GucEmissionCachesIndependentEdits(string kind, bool move)
    {
        string source = GucCacheSource(kind);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        GucEmission original = TrackedGucEmission(driver, "demo.value").Emission;
        string replacement = move ? "\n\n" + source : source.Replace("=> new(value);", "=> new(value, null);", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            replacement, path: "Moved.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        (GucEmission emission, IncrementalStepRunReason reason) = TrackedGucEmission(driver, "demo.value");

        Assert.AreEqual(IncrementalStepRunReason.Cached, reason);
        Assert.AreEqual(original, emission);
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreEqual(ManifestValue(first, "Ankus.Exports"), ManifestValue(second, "Ankus.Exports"));
        Assert.Contains("global::Ankus.CompilerServices.NativeGuc.", emission.Property);
        AssertGucCompilation(second, []);
    }

    /// <summary>
    /// A changed native default rerenders its descriptor and preserves the independently cached setting.
    /// </summary>
    /// <param name="kind">The native boot representation.</param>
    /// <param name="before">The initial managed default expression.</param>
    /// <param name="after">The changed managed default expression.</param>
    /// <param name="expected">The independently expected native boot initializer.</param>
    [TestMethod]
    [DataRow("boolean", "true", "false", ".boot.boolean = false")]
    [DataRow("integer", "7", "9", ".boot.integer = 9")]
    [DataRow("real", "1.5", "2.5", ".boot.real = 2.5")]
    [DataRow("string", "\"initial\"", "\"changed\"", ".boot.string = \"\\143\\150\\141\\156\\147\\145\\144\"")]
    [DataRow("enum", "Mode.First", "Mode.Last", ".boot.integer = 1")]
    public void GucEmissionTracksDependentDefaults(string kind, string before, string after, string expected)
    {
        string source = GucCacheSource(kind);
        string survivor = GucCacheSource("integer").Replace("Settings", "Other", StringComparison.Ordinal)
            .Replace("Mode", "OtherMode", StringComparison.Ordinal).Replace("demo.value", "demo.other", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(source + survivor);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        GucEmission original = TrackedGucEmission(driver, "demo.value").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source.Replace(", " + before + ",", ", " + after + ",", StringComparison.Ordinal) + survivor,
            path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        (GucEmission changed, IncrementalStepRunReason reason) = TrackedGucEmission(driver, "demo.value");

        Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedGucEmission(driver, "demo.other").Reason);
        Assert.AreNotEqual(original.Native, changed.Native);
        Assert.AreEqual(original.Property, changed.Property);
        Assert.Contains(expected, changed.Native);
        Assert.Contains(changed.Native.ReplaceLineEndings("\n"), ManifestValue(output, "Ankus.NativeSource"));
        AssertGucCompilation(output, []);
    }

    /// <summary>
    /// Native metadata edits rerender only the affected setting while preserving independent source fragments.
    /// </summary>
    /// <param name="before">The initial descriptor contract.</param>
    /// <param name="after">The changed descriptor contract.</param>
    /// <param name="expected">The independently expected native descriptor field.</param>
    [TestMethod]
    [DataRow("Minimum = 0", "Minimum = 1", ".minimum.integer = 1")]
    [DataRow("Maximum = 100", "Maximum = 99", ".maximum.integer = 99")]
    [DataRow("PgGucContext)6", "PgGucContext)5", ".context = 5")]
    [DataRow("PgGucOptions)0", "PgGucOptions)1", ".flags = 1U")]
    [DataRow("PgGucUnit)0", "PgGucUnit)1", ".unit = 1")]
    [DataRow("LongDescription = \"Long\"", "LongDescription = \"é\"", ".long_utf8 = \"\\303\\251\"")]
    public void GucEmissionTracksDependentMetadata(string before, string after, string expected)
    {
        string source = GucCacheSource("integer").Replace("Check = nameof(Check)",
            "Minimum = 0, Maximum = 100, Context = (Ankus.PgGucContext)6, Flags = (Ankus.PgGucOptions)0, " +
            "Unit = (Ankus.PgGucUnit)0, LongDescription = \"Long\", Check = nameof(Check)", StringComparison.Ordinal);
        string survivor = GucCacheSource("integer").Replace("Settings", "Other", StringComparison.Ordinal)
            .Replace("Mode", "OtherMode", StringComparison.Ordinal).Replace("demo.value", "demo.other", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(source + survivor);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        GucEmission original = TrackedGucEmission(driver, "demo.value").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source.Replace(before, after, StringComparison.Ordinal) + survivor, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        (GucEmission changed, IncrementalStepRunReason reason) = TrackedGucEmission(driver, "demo.value");

        Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedGucEmission(driver, "demo.other").Reason);
        Assert.AreEqual(original.Property, changed.Property);
        Assert.Contains(expected, changed.Native);
        Assert.Contains(changed.Native.ReplaceLineEndings("\n"), ManifestValue(output, "Ankus.NativeSource"));
        AssertGucCompilation(output, []);
    }

    /// <summary>
    /// Referenced constants and enum label changes invalidate a dependent setting without editing its property syntax.
    /// </summary>
    /// <param name="kind">The indirect semantic dependency to change.</param>
    [TestMethod]
    [DataRow("constant")]
    [DataRow("enum")]
    public void GucEmissionTracksReferencedSemanticContracts(string kind)
    {
        string source = GucCacheSource(kind == "enum" ? "enum" : "integer")
            .Replace("public enum Mode { First = 10, Last = 20 }", "", StringComparison.Ordinal);
        string dependency = kind == "enum" ? "public enum Mode { First = 10, Last = 20 }" : "public static class Options { public const int Boot = 7; }";
        string replacement = kind == "enum" ? "public enum Mode { [Ankus.PgGucLabel(\"renamed\", Hidden = true)] First = 10, Last = 20 }" :
            "public static class Options { public const int Boot = 9; }";
        if (kind == "constant")
        {
            source = source.Replace(", 7,", ", Options.Boot,", StringComparison.Ordinal);
        }

        CSharpCompilation initial = ModuleCompilation(source).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            dependency, path: "Dependency.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        GucEmission original = TrackedGucEmission(driver, "demo.value").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            replacement, path: "Dependency.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        (GucEmission changed, IncrementalStepRunReason reason) = TrackedGucEmission(driver, "demo.value");

        Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
        Assert.Contains(".boot.integer = " + (kind == "enum" ? "0" : "9"), changed.Native);
        Assert.AreNotEqual(original.Native, changed.Native);
        if (kind == "enum")
        {
            Assert.Contains("{ \"\\162\\145\\156\\141\\155\\145\\144\", 0, true }", changed.Native);
            Assert.Contains("0 => global::Mode.@First, 1 => global::Mode.@Last", changed.Managed);
            Assert.Contains("0 => global::Mode.@First, 1 => global::Mode.@Last", changed.Property);
        }

        AssertGucCompilation(output, []);
    }

    /// <summary>
    /// Native defaults and bounds preserve the sign of zero across incremental edits in both directions.
    /// </summary>
    /// <param name="slot">The descriptor value whose sign changes.</param>
    /// <param name="negativeFirst">Whether the initial value has its sign bit set.</param>
    [TestMethod]
    [DataRow("Default", false)]
    [DataRow("Default", true)]
    [DataRow("Minimum", false)]
    [DataRow("Minimum", true)]
    [DataRow("Maximum", false)]
    [DataRow("Maximum", true)]
    public void GucEmissionDistinguishesSignedZeroDefaultsAndBounds(string slot, bool negativeFirst)
    {
        string template = GucCacheSource("real").Replace(", 1.5,", ", " + (slot == "Default" ? "__ZERO__" : "0.0") + ",", StringComparison.Ordinal)
            .Replace("Check = nameof(Check)", "Minimum = " + (slot == "Minimum" ? "__ZERO__" : "-1.0") +
                ", Maximum = " + (slot == "Maximum" ? "__ZERO__" : "1.0") + ", Check = nameof(Check)", StringComparison.Ordinal);
        string before = negativeFirst ? "-0.0" : "0.0";
        string after = negativeFirst ? "0.0" : "-0.0";
        string field = slot switch
        {
            "Default" => "boot",
            "Minimum" => "minimum",
            _ => "maximum",
        };
        CSharpCompilation initial = ModuleCompilation(template.Replace("__ZERO__", before, StringComparison.Ordinal));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        GucEmission previous = TrackedGucEmission(driver, "demo.value").Emission;
        Assert.Contains("." + field + ".real = " + before, previous.Native);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            template.Replace("__ZERO__", after, StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        (GucEmission changed, IncrementalStepRunReason reason) = TrackedGucEmission(driver, "demo.value");

        Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
        Assert.Contains("." + field + ".real = " + after, changed.Native);
        Assert.AreNotEqual(previous.Native, changed.Native);
        AssertGucCompilation(output, []);
    }

    /// <summary>
    /// Hook renames invalidate managed dispatch while retaining the current setting identity and getter.
    /// </summary>
    [TestMethod]
    public void GucEmissionTracksInvocationRenames()
    {
        string source = GucCacheSource("integer");
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        GucEmission previous = TrackedGucEmission(driver, "demo.value").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source.Replace("nameof(Check)", "nameof(CheckChanged)", StringComparison.Ordinal)
                .Replace(" Check(", " CheckChanged(", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        (GucEmission changed, IncrementalStepRunReason reason) = TrackedGucEmission(driver, "demo.value");

        Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
        Assert.AreEqual(previous.Symbol, changed.Symbol);
        Assert.AreEqual(previous.Property, changed.Property);
        Assert.Contains("global::Settings.@CheckChanged(value,", changed.Managed);
        Assert.DoesNotContain("global::Settings.@Check(value,", changed.Managed);
        AssertGucCompilation(output, []);
    }

    /// <summary>
    /// Cached invalid setting analysis reports the current tree and then removes its diagnostics after repair.
    /// </summary>
    [TestMethod]
    public void GucDiagnosticsFollowCurrentInputsAndRecover()
    {
        string source = GucCacheSource("integer").Replace("demo.value", "invalid", StringComparison.Ordinal);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> first, context.CancellationToken);
        Assert.AreEqual("ANKUS181", Assert.ContainsSingle(first).Id);
        SyntaxTree current = CSharpSyntaxTree.ParseText(source, path: "Current.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation moved = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(moved, out _, out ImmutableArray<Diagnostic> second, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(second);

        Assert.AreEqual("ANKUS181", error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual("\"invalid\"", current.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "GucEmission"));
        CSharpCompilation repaired = moved.ReplaceSyntaxTree(current, CSharpSyntaxTree.ParseText(GucCacheSource("integer"),
            path: "Current.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedGucEmission(driver, "demo.value").Reason);
        AssertGucCompilation(output, []);
    }

    /// <summary>
    /// Assembly prefix rendering remains cached after an unrelated body edit or source movement.
    /// </summary>
    /// <param name="move">Whether to move the declaration rather than change an ordinary method body.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GucPrefixEmissionCachesIndependentEdits(bool move)
    {
        const string Source = "[assembly: Ankus.PgGucPrefix(\"demo\")] public static class Ordinary { public static int Value() => 1; }";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            move ? "\n\n" + Source : Source.Replace("=> 1", "=> 2", StringComparison.Ordinal),
            path: "Moved.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);

        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "GucPrefixEmission"));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(output, "Ankus.NativeSource"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(output, "Ankus.Exports")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Current property names, namespaces, containers and nullability invalidate the exact partial getter and dispatcher contracts.
    /// </summary>
    /// <param name="change">The managed property contract to edit.</param>
    [TestMethod]
    [DataRow("name")]
    [DataRow("namespace")]
    [DataRow("container")]
    [DataRow("nullable")]
    public void GucEmissionTracksPropertyContracts(string change)
    {
        string source = GucCacheSource("string");
        string replacement = change switch
        {
            "name" => source.Replace("string Value {", "string Renamed {", StringComparison.Ordinal),
            "namespace" => "namespace Cache;\n" + source,
            "container" => "public partial record class Outer { " + source + " }",
            _ => source.Replace("string", "string?", StringComparison.Ordinal),
        };
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out _);
        GucEmission previous = TrackedGucEmission(driver, "demo.value").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            replacement, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);
        (GucEmission changed, IncrementalStepRunReason reason) = TrackedGucEmission(driver, "demo.value");

        Assert.AreEqual(IncrementalStepRunReason.Modified, reason);
        Assert.AreNotEqual(previous.Property, changed.Property);
        string expected = change switch
        {
            "name" => "@Renamed =>",
            "namespace" => "namespace @Cache",
            "container" => "partial record class @Outer",
            _ => "global::Ankus.CompilerServices.NativeGuc.ReadString(\"demo.value\", ref global::Ankus.Generated.GucVariables.",
        };
        Assert.Contains(expected, changed.Property);
        if (change is "namespace" or "container")
        {
            Assert.AreNotEqual(previous.Symbol, changed.Symbol);
            Assert.DoesNotContain(previous.Symbol, ManifestValue(output, "Ankus.NativeSource"));
        }

        AssertGucCompilation(output, []);
    }

    /// <summary>
    /// Prefix inventory changes preserve literal case, deduplicate exact matches and remove stale initialization.
    /// </summary>
    /// <param name="change">The literal prefix inventory edit.</param>
    [TestMethod]
    [DataRow("reorder")]
    [DataRow("duplicate")]
    [DataRow("case")]
    [DataRow("literal")]
    [DataRow("remove")]
    public void GucPrefixEmissionTracksLiteralInventory(string change)
    {
        const string Marker = "[assembly: Ankus.PgModule]\n";
        const string Source = "[assembly: Ankus.PgGucPrefix(\"a\")][assembly: Ankus.PgGucPrefix(\"A\")][assembly: Ankus.PgGucPrefix(\"a\")]";
        string replacement = change switch
        {
            "reorder" => "[assembly: Ankus.PgGucPrefix(\"A\")][assembly: Ankus.PgGucPrefix(\"a\")][assembly: Ankus.PgGucPrefix(\"a\")]",
            "duplicate" => "[assembly: Ankus.PgGucPrefix(\"a\")][assembly: Ankus.PgGucPrefix(\"A\")]",
            "case" => Source.Replace("\"A\"", "\"a\"", StringComparison.Ordinal),
            "literal" => Source.Replace("\"A\"", "\"é\"", StringComparison.Ordinal),
            _ => "",
        };
        CSharpCompilation initial = ModuleCompilation(Marker + Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Marker + replacement, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation output);

        Assert.AreEqual(change is "reorder" or "duplicate" ? IncrementalStepRunReason.Cached : IncrementalStepRunReason.Modified,
            ModuleStep(driver, "GucPrefixEmission"));
        string[] expected = change switch
        {
            "case" => ["ankus_reserve_guc_prefix(\"\\141\");"],
            "literal" => ["ankus_reserve_guc_prefix(\"\\141\");", "ankus_reserve_guc_prefix(\"\\303\\251\");"],
            "remove" => [],
            _ => ["ankus_reserve_guc_prefix(\"\\101\");", "ankus_reserve_guc_prefix(\"\\141\");"],
        };
        Assert.AreSequenceEqual(expected, ManifestValue(output, "Ankus.NativeSource").Split('\n').Select(static line => line.Trim())
            .Where(static line => line.StartsWith("ankus_reserve_guc_prefix(\"", StringComparison.Ordinal)));
        if (change is "reorder" or "duplicate")
        {
            Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(output, "Ankus.NativeSource"));
        }
        else if (change == "remove")
        {
            Assert.AreEqual("Pg_magic_func", ManifestValue(output, "Ankus.Exports").Trim());
            Assert.DoesNotContain("MarkGUCPrefixReserved", ManifestValue(output, "Ankus.NativeSource"));
        }
    }

    /// <summary>
    /// Creates independently valid typed configuration and hook contracts for each native transport.
    /// </summary>
    /// <param name="kind">The requested native setting kind.</param>
    /// <returns>The ordinary C# consumer declarations.</returns>
    private static string GucCacheSource(string kind)
    {
        (string attribute, string type, string boot) = kind switch
        {
            "boolean" => ("Bool", "bool", "true"),
            "integer" => ("Int", "int", "7"),
            "real" => ("Real", "double", "1.5"),
            "string" => ("String", "string", "\"initial\""),
            _ => ("Enum", "Mode", "Mode.First"),
        };
        return $$"""
            public enum Mode { First = 10, Last = 20 }
            public static partial class Settings
            {
                [Ankus.PgGuc{{attribute}}("demo.value", {{boot}}, "Value", Check = nameof(Check))]
                public static partial {{type}} Value { get; }
                public static Ankus.PgGucCheckResult<{{type}}> Check({{type}} value, Ankus.PgGucSource source) => new(value);
            }
            """;
    }

    /// <summary>
    /// Reads the production setting renderer's actual incremental decision and detached output.
    /// </summary>
    /// <param name="driver">The executed production generator.</param>
    /// <param name="name">The exact PostgreSQL setting name.</param>
    /// <returns>The setting fragments and Roslyn cache decision.</returns>
    private static (GucEmission Emission, IncrementalStepRunReason Reason) TrackedGucEmission(GeneratorDriver driver, string name)
    {
        (object value, IncrementalStepRunReason reason) = Assert.ContainsSingle(Assert.ContainsSingle(driver.GetRunResult().Results)
            .TrackedSteps["GucEmission"].SelectMany(static step => step.Outputs).Where(output =>
                output.Reason != IncrementalStepRunReason.Removed && output.Value is GucEmission emission &&
                emission.Property.Contains("\"" + name + "\"", StringComparison.Ordinal)));
        return (Assert.IsInstanceOfType<GucEmission>(value), reason);
    }
}
