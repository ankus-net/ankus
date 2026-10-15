using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

/// <summary>
/// Preserves mandatory execution at generated SQL, event and backend-test call sites.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Every conditional entry is rejected at its attribute, including either partial declaration and enabled symbols.
    /// </summary>
    /// <param name="kind">A SQL function, event trigger, or backend test.</param>
    /// <param name="placement">An ordinary method, partial definition, or partial implementation.</param>
    /// <param name="defined">Whether the condition is enabled across the whole compilation.</param>
    [TestMethod]
    [DataRow(0, 0, false)]
    [DataRow(0, 0, true)]
    [DataRow(0, 1, false)]
    [DataRow(0, 1, true)]
    [DataRow(0, 2, false)]
    [DataRow(0, 2, true)]
    [DataRow(1, 0, false)]
    [DataRow(1, 0, true)]
    [DataRow(1, 1, false)]
    [DataRow(1, 1, true)]
    [DataRow(1, 2, false)]
    [DataRow(1, 2, true)]
    [DataRow(2, 0, false)]
    [DataRow(2, 0, true)]
    [DataRow(2, 1, false)]
    [DataRow(2, 1, true)]
    [DataRow(2, 2, false)]
    [DataRow(2, 2, true)]
    public void GeneratedEntriesRejectConditionalCalls(int kind, int placement, bool defined)
    {
        CSharpCompilation input = ConditionalEntryCompilation(ConditionalEntrySource(kind, placement, true), defined);
        ConditionalEntryDriver(input).RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS277", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(ConditionalEntryAttribute,
            error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreSame(input.SyntaxTrees.Single(), error.Location.SourceTree);
        Assert.AreEqual("https://ankus.net/reference/execution/#conditional-entry-methods", error.Descriptor.HelpLinkUri);
        Assert.Contains("Entry", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.Contains("remove", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.Contains("\"sibling\"", InstallationBody(output));
        Assert.ContainsSingle(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers()
            .OfType<IMethodSymbol>().Where(static method => method.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute")));
        AssertCallbackCompiles(output);
    }

    /// <summary>
    /// Ordinary and unrelated debugger metadata preserve compiled entry calls and sibling exports.
    /// </summary>
    /// <param name="kind">A SQL function, event trigger, or backend test.</param>
    /// <param name="debugger">Whether an unrelated debugger annotation is present.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public void GeneratedEntriesRetainOrdinaryAndDebuggerCalls(int kind, bool debugger)
    {
        string source = ConditionalEntrySource(kind, 0, false);
        if (debugger)
        {
            source = source.Replace("public static void Entry", "[System.Diagnostics.DebuggerStepThrough] public static void Entry", StringComparison.Ordinal);
        }

        CSharpCompilation input = ConditionalEntryCompilation(source, false);
        ConditionalEntryDriver(input).RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Assert.IsEmpty(diagnostics);
        AssertCallbackCompiles(output);
        using var image = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult emitted = output.Emit(image, cancellationToken: context.CancellationToken);
        Assert.IsTrue(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var load = new AssemblyLoadContext("ConditionalEntryControl", isCollectible: true);
        try
        {
            Assembly assembly = load.LoadFromStream(image);
            System.Reflection.TypeInfo dispatcher = assembly.GetType("Ankus.Generated.ExtensionDispatchers", throwOnError: true)!.GetTypeInfo();
            MethodInfo target = assembly.GetType("Functions", throwOnError: true)!.GetMethod("Entry")!;
            Assert.ContainsSingle(dispatcher.DeclaredMethods.Where(method =>
                HasDirectEntryCall(method.Module, method.GetMethodBody()!.GetILAsByteArray()!, target)));
        }
        finally
        {
            load.Unload();
        }
    }

    /// <summary>
    /// Attribute edits invalidate validation while sibling rendering stays cached; moving and repairing refresh diagnostics correctly.
    /// </summary>
    /// <param name="kind">A SQL function, event trigger, or backend test.</param>
    /// <param name="defined">Whether the condition is enabled across the whole compilation.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public void ConditionalEntryMetadataEditsPreserveSiblingCaching(int kind, bool defined)
    {
        CSharpCompilation initial = ConditionalEntryCompilation(ConditionalEntrySource(kind, 0, false), defined);
        GeneratorDriver driver = ConditionalEntryDriver(initial);
        driver = driver.RunGeneratorsAndUpdateCompilation(initial, out Compilation first, out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Assert.IsEmpty(diagnostics);
        string sibling = ConditionalSiblingEmission(driver).Source.Managed;
        string native = ManifestValue(first, "Ankus.NativeSource");
        string rejectedSource = ConditionalEntrySource(kind, 0, true);
        CSharpCompilation rejected = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            rejectedSource, (CSharpParseOptions)initial.SyntaxTrees.Single().Options, path: "Entries.cs", cancellationToken: context.CancellationToken));
        driver = driver.RunGeneratorsAndUpdateCompilation(rejected, out _, out diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS277", error.Id);
        Assert.AreEqual(sibling, ConditionalSiblingEmission(driver).Source.Managed);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ConditionalSiblingEmission(driver).Reason);

        CSharpCompilation moved = rejected.ReplaceSyntaxTree(rejected.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            "\n\n" + rejectedSource, (CSharpParseOptions)initial.SyntaxTrees.Single().Options, path: "Moved.cs", cancellationToken: context.CancellationToken));
        driver = driver.RunGeneratorsAndUpdateCompilation(moved, out _, out diagnostics, context.CancellationToken);
        Diagnostic current = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS277", current.Id);
        Assert.AreSame(moved.SyntaxTrees.Single(), current.Location.SourceTree);
        Assert.AreEqual(error.Location.GetLineSpan().StartLinePosition.Line + 2, current.Location.GetLineSpan().StartLinePosition.Line);
        Assert.AreEqual(ConditionalEntryAttribute, current.Location.SourceTree!.GetText(context.CancellationToken).ToString(current.Location.SourceSpan));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ConditionalSiblingEmission(driver).Reason);

        driver = driver.RunGeneratorsAndUpdateCompilation(initial, out Compilation repaired, out diagnostics, context.CancellationToken);
        Assert.IsEmpty(diagnostics);
        Assert.AreEqual(native, ManifestValue(repaired, "Ankus.NativeSource"));
        Assert.AreEqual(sibling, ConditionalSiblingEmission(driver).Source.Managed);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ConditionalSiblingEmission(driver).Reason);
        AssertCallbackCompiles(repaired);
    }

    /// <summary>
    /// Identifies the exact omission attribute in authored source.
    /// </summary>
    private const string ConditionalEntryAttribute = "System.Diagnostics.Conditional(\"ANKUS_ENTRY_ENABLED\")";

    /// <summary>
    /// Builds one entry and an independent scalar sibling, with explicit partial placement.
    /// </summary>
    /// <param name="kind">The entry marker.</param>
    /// <param name="placement">The selected method declaration.</param>
    /// <param name="conditional">Whether to declare omission metadata.</param>
    /// <returns>The complete consumer source.</returns>
    private static string ConditionalEntrySource(int kind, int placement, bool conditional)
    {
        string marker = kind switch { 0 => "Ankus.PgFunction", 1 => "Ankus.PgEventTrigger", _ => "Ankus.PgTest" };
        string parameters = kind == 1 ? "Ankus.PgEventTriggerContext context" : string.Empty;
        string attribute = conditional ? "[" + ConditionalEntryAttribute + "] " : string.Empty;
        string declaration = placement == 0 ? "[" + marker + "] " + attribute + "public static void Entry(" + parameters + ") { Effects++; }" :
            "[" + marker + "] " + (placement == 1 ? attribute : string.Empty) + "public static partial void Entry(" + parameters + ");\n" +
            (placement == 2 ? attribute : string.Empty) + "public static partial void Entry(" + parameters + ") { Effects++; }";
        return "public static partial class Functions { public static int Effects; " + declaration +
            "\n[Ankus.PgFunction] public static int Sibling() => 7; }";
    }

    /// <summary>
    /// Creates a consumer compilation with the condition applied to generated files too.
    /// </summary>
    /// <param name="source">The complete authored source.</param>
    /// <param name="defined">Whether to enable the condition globally.</param>
    /// <returns>The controlled compilation.</returns>
    private CSharpCompilation ConditionalEntryCompilation(string source, bool defined)
        => CSharpCompilation.Create("GeneratorTest", [CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.CSharp14, preprocessorSymbols: defined ? ["ANKUS_ENTRY_ENABLED"] : []),
            path: "Entries.cs", cancellationToken: context.CancellationToken)], s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>
    /// Tracks all generated contracts and explicitly includes backend test entries.
    /// </summary>
    /// <param name="input">The compilation supplying matching parser options.</param>
    /// <returns>The tracked generator driver.</returns>
    private static CSharpGeneratorDriver ConditionalEntryDriver(CSharpCompilation input)
        => CSharpGeneratorDriver.Create([new PgFunctionGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions)input.SyntaxTrees.Single().Options, optionsProvider: new BackendOptions("true"),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    /// <summary>
    /// Reads the actual independent sibling rendering rather than aggregate output counters.
    /// </summary>
    /// <param name="driver">The completed tracked driver.</param>
    /// <returns>The sibling artifact and its cache result.</returns>
    private static (FunctionEmission Source, IncrementalStepRunReason Reason) ConditionalSiblingEmission(GeneratorDriver driver)
    {
        (object value, IncrementalStepRunReason reason) = driver.GetRunResult().Results.Single().TrackedSteps["FunctionEmission"]
            .SelectMany(static step => step.Outputs).Single(static item => item.Value is FunctionEmission emission &&
                emission.Managed.Contains("Sibling(", StringComparison.Ordinal));
        return ((FunctionEmission)value, reason);
    }

    /// <summary>
    /// Resolves actual compiled call instructions against the expected managed entry method.
    /// </summary>
    /// <param name="module">The emitted module owning call tokens.</param>
    /// <param name="body">The compiled dispatcher body.</param>
    /// <param name="target">The exact entry method that must be called.</param>
    /// <returns>Whether the dispatcher contains the entry's direct call.</returns>
    private static bool HasDirectEntryCall(Module module, byte[] body, MethodInfo target)
    {
        Dictionary<ushort, OpCode> codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(OpCode)).Select(static field => (OpCode)field.GetValue(null)!)
            .ToDictionary(static code => unchecked((ushort)code.Value));
        int index = 0;
        while (index < body.Length)
        {
            ushort value = body[index++];
            if (value == 0xFE)
            {
                value = checked((ushort)((value << 8) | body[index++]));
            }

            OpCode code = codes[value];
            int length = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod or
                    OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(index, 4))),
                _ => throw new InvalidDataException("The compiled dispatcher has an unsupported IL operand."),
            };
            if (length < 0 || index > body.Length - length)
            {
                throw new InvalidDataException("The compiled dispatcher has a truncated IL operand.");
            }

            if (code == OpCodes.Call && module.ResolveMethod(BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(index, 4))) == target)
            {
                return true;
            }

            index += length;
        }

        return false;
    }
}
