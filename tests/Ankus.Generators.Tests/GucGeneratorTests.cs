using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Every native setting kind compiles as a typed partial getter without SQL functions or managed initialization.
    /// </summary>
    /// <param name="attribute">The typed declaration.</param>
    /// <param name="type">The exact managed type.</param>
    /// <param name="kind">The stable native kind.</param>
    /// <param name="reader">The typed read helper.</param>
    /// <param name="boot">The native boot initializer.</param>
    [TestMethod]
    [DataRow("Ankus.PgGucBool(\"demo.enabled\", true, \"Enabled\")", "bool", 0, "ReadBoolean", ".boot.boolean = true")]
    [DataRow("Ankus.PgGucInt(\"demo.enabled\", -2147483648, \"Enabled\")", "int", 1, "ReadInt32", ".boot.integer = -2147483648")]
    [DataRow("Ankus.PgGucReal(\"demo.enabled\", -0.0, \"Enabled\")", "double", 2, "ReadDouble", ".boot.real = -0.0")]
    [DataRow("Ankus.PgGucString(\"demo.enabled\", null, \"Enabled\")", "string?", 3, "ReadString", ".boot.string = NULL")]
    [DataRow("Ankus.PgGucString(\"demo.enabled\", \"\", \"Enabled\")", "string", 3, "ReadRequiredString", ".boot.string = \"\"")]
    [DataRow("Ankus.PgGucEnum(\"demo.enabled\", Mode.Last, \"Enabled\")", "Mode", 4, "ReadEnum", ".boot.integer = 1")]
    public void GucNativeOnlyKindsCompileWithoutManagedRegistration(string attribute, string type, int kind, string reader, string boot)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public enum Mode : ulong { First = 42, Last = ulong.MaxValue } public static partial class Settings { [" + attribute +
            "] public static partial " + type + " Value { get; } }");
        AssertGucCompilation(compilation, diagnostics);
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(compilation).ReplaceLineEndings("\n"));
        Assert.AreSequenceEqual(["Pg_magic_func", "_PG_init"], ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("true", ManifestValue(compilation, "Ankus.Relocatable"));
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains($".kind = {kind}, .context = 6, .flags = 0U, .unit = 0", native);
        Assert.Contains(boot, native);
        Assert.DoesNotContain("ankus_spi_execute", native);
        Assert.DoesNotContain("cannot run through shared_preload_libraries", native);
        Assert.DoesNotContain("AnkusError *error =", native);
        Assert.IsEmpty(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
        IPropertySymbol property = Assert.IsInstanceOfType<IPropertySymbol>(Assert.ContainsSingle(compilation.GetTypeByMetadataName("Settings")!.GetMembers("Value")));
        Assert.IsNotNull(property.PartialImplementationPart);
        Assert.Contains($"global::Ankus.CompilerServices.NativeGuc.{reader}(\"demo.enabled\")",
            property.PartialImplementationPart.DeclaringSyntaxReferences.Single().GetSyntax(context.CancellationToken).ToString());
    }

    /// <summary>
    /// Contexts, all stable flags, numeric units, and inclusive bounds survive native descriptor emission.
    /// </summary>
    /// <param name="nativeContext">The stable context value.</param>
    /// <param name="unit">The stable numeric unit.</param>
    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    [DataRow(3, 3)]
    [DataRow(4, 4)]
    [DataRow(5, 5)]
    [DataRow(6, 6)]
    [DataRow(6, 7)]
    [DataRow(6, 8)]
    public void GucNativeMetadataPreservesContextsFlagsAndUnits(int nativeContext, int unit)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static partial class Settings
            {
                [Ankus.PgGucInt("demo.count", 3, "Count", Minimum = 3, Maximum = 3,
                    Context = (Ankus.PgGucContext){{nativeContext}}, Flags = (Ankus.PgGucOptions)991,
                    Unit = (Ankus.PgGucUnit){{unit}}, LongDescription = "Long description")]
                public static partial int Count { get; }
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains($".kind = 1, .context = {nativeContext}, .flags = 991U, .unit = {unit}", native);
        Assert.Contains(".minimum.integer = 3, .maximum.integer = 3", native);
        Assert.Contains(".long_utf8 = \"\\114\\157\\156\\147\\040\\144\\145\\163\\143\\162\\151\\160\\164\\151\\157\\156\"", native);
    }

    /// <summary>
    /// Real metadata permits explicit infinities and all numeric unit families without losing the sign of zero.
    /// </summary>
    /// <param name="boot">The real boot expression.</param>
    /// <param name="expected">The emitted C constant.</param>
    [TestMethod]
    [DataRow("double.PositiveInfinity", "HUGE_VAL")]
    [DataRow("double.NegativeInfinity", "(-HUGE_VAL)")]
    [DataRow("0.0", "0.0")]
    [DataRow("-0.0", "-0.0")]
    public void GucRealMetadataPreservesFiniteAndInfiniteBounds(string boot, string expected)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static partial class Settings { [Ankus.PgGucReal(\"demo.real\", " + boot +
            ", \"Real\", Minimum = double.NegativeInfinity, Maximum = double.PositiveInfinity, Unit = Ankus.PgGucUnit.Bytes)] public static partial double Value { get; } }");
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains(".boot.real = " + expected, native);
        Assert.Contains(".minimum.real = (-HUGE_VAL), .maximum.real = HUGE_VAL", native);
        Assert.Contains(".unit = 1", native);
    }

    /// <summary>
    /// UTF-8 retained literals use bounded octal escapes and names keep PostgreSQL's high-bit identifier rules.
    /// </summary>
    [TestMethod]
    public void GucTextMetadataPreservesUnicodeQuotesAndEmptyDescriptions()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            namespace @event;
            public partial record class Outer
            {
                internal static partial class @class
                {
                    [Ankus.PgGucString("démo.value$2", "é\"\\7", "", LongDescription = "🐘", Flags = Ankus.PgGucOptions.IsName)]
                    internal static partial string @event { get; }
                }
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains(".boot.string = \"\\303\\251\\042\\134\\067\"", native);
        Assert.Contains(".short_utf8 = \"\", .long_utf8 = \"\\360\\237\\220\\230\"", native);
        Assert.Contains(".flags = 32U", native);
    }

    /// <summary>
    /// Enum aliases retain canonical declaration order, hidden labels, and every underlying value without SQL enum creation.
    /// </summary>
    [TestMethod]
    public void GucEnumAliasesPreserveWideValuesAndCanonicalOrder()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public enum Mode : ulong
            {
                [Ankus.PgGucLabel("", Hidden = true)] Maximum = ulong.MaxValue,
                [Ankus.PgGucLabel("alias")] Alias = Maximum,
                [Ankus.PgGucLabel("zero", Hidden = true)] Zero = 0,
            }
            public static partial class Settings
            {
                [Ankus.PgGucEnum("demo.mode", Mode.Alias, "Mode", Check = nameof(Check))]
                public static partial Mode Mode { get; }
                public static Ankus.PgGucCheckResult<Mode> Check(Mode value, Ankus.PgGucSource source) => new(value);
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("    { \"\", 0, true },\n    { \"\\141\\154\\151\\141\\163\", 0, false },\n    { \"\\172\\145\\162\\157\", 1, true },", native);
        Assert.Contains(".boot.integer = 0", native);
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(compilation).ReplaceLineEndings("\n"));
        string managed = string.Join("\n", compilation.SyntaxTrees.Select(static tree => tree.ToString()));
        Assert.Contains("0 => global::Mode.@Maximum, 1 => global::Mode.@Zero", managed);
        Assert.Contains("global::Mode.@Maximum => 0, global::Mode.@Zero => 1", managed);
        Assert.DoesNotContain("global::Mode.@Alias =>", managed);
        Assert.Contains("A GUC check returned an undefined enum value.", managed);
    }

    /// <summary>
    /// Every hook type compiles with the exact property type and keeps check rejection distinct from unexpected exceptions.
    /// </summary>
    /// <param name="attribute">The GUC kind and boot expression.</param>
    /// <param name="type">The exact typed hook value.</param>
    [TestMethod]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Value\"", "bool")]
    [DataRow("Ankus.PgGucInt(\"demo.value\", 42, \"Value\"", "int")]
    [DataRow("Ankus.PgGucReal(\"demo.value\", 1.5, \"Value\"", "double")]
    [DataRow("Ankus.PgGucString(\"demo.value\", \"value\", \"Value\"", "string")]
    [DataRow("Ankus.PgGucString(\"demo.value\", null, \"Value\"", "string?")]
    [DataRow("Ankus.PgGucEnum(\"demo.value\", Mode.First, \"Value\"", "Mode")]
    public void GucHookDispatchCompilesWithTypedLifetimeAndErrorBoundaries(string attribute, string type)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public enum Mode { First = -2147483648, Last = 2147483647 }
            public static partial class Settings
            {
                [{{attribute}}, Check = nameof(Check), Assign = nameof(Assign), Show = nameof(Show))]
                public static partial {{type}} Value { get; }
                public static Ankus.PgGucCheckResult<{{type}}> Check({{type}} proposed, Ankus.PgGucSource source) => new(proposed);
                public static void Assign({{type}} accepted, Ankus.PgGucExtra? extra) { }
                public static string Show({{type}} current, Ankus.PgGucExtra? extra) => "shown";
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        IMethodSymbol callback = Assert.IsInstanceOfType<IMethodSymbol>(Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!
            .GetMembers().Where(static member => member.Name.EndsWith("_guc", StringComparison.Ordinal))));
        Assert.AreEqual(SpecialType.System_Int32, callback.ReturnType.SpecialType);
        Assert.AreSequenceEqual(["int", "Ankus.CompilerServices.NativeValue*", "Ankus.CompilerServices.NativeValue*", "int", "Ankus.CompilerServices.NativeCallError*", "nint", "nint", "nint", "nint"],
            callback.Parameters.Select(static parameter => parameter.Type.ToDisplayString()));
        AttributeData entry = Assert.ContainsSingle(callback.GetAttributes());
        Assert.AreEqual("System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute", entry.AttributeClass!.ToDisplayString());
        Assert.AreEqual(callback.Name, entry.NamedArguments.Single(static argument => argument.Key == "EntryPoint").Value.Value);
        Assert.AreEqual("System.Runtime.CompilerServices.CallConvCdecl",
            Assert.IsInstanceOfType<ITypeSymbol>(Assert.ContainsSingle(entry.NamedArguments.Single(static argument => argument.Key == "CallConvs").Value.Values).Value).ToDisplayString());
        MethodDeclarationSyntax syntax = Assert.IsInstanceOfType<MethodDeclarationSyntax>(callback.DeclaringSyntaxReferences.Single().GetSyntax(context.CancellationToken));
        Assert.AreEqual("nint previousBackend = global::Ankus.CompilerServices.NativeBackend.Enter(execute);", syntax.Body!.Statements[0].ToString());
        Assert.AreEqual("nint previousRead = global::Ankus.CompilerServices.NativeGuc.Enter(read);", syntax.Body.Statements[1].ToString());
        Assert.AreEqual("nint previousLog = global::Ankus.CompilerServices.NativeLog.Enter(log);", syntax.Body.Statements[2].ToString());
        TryStatementSyntax guarded = AssertMemoryCallbackScope(syntax, "global::Ankus.CompilerServices.NativeLog.Exit(previousLog);",
            "global::Ankus.CompilerServices.NativeGuc.Exit(previousRead);", "global::Ankus.CompilerServices.NativeBackend.Exit(previousBackend);");
        CatchClauseSyntax error = Assert.ContainsSingle(guarded.Catches);
        Assert.AreSequenceEqual(["global::Ankus.CompilerServices.NativeError.Write(exception, error);", "return 1;"], error.Block.Statements.Select(static statement => statement.ToString()));
        SwitchStatementSyntax phases = Assert.IsInstanceOfType<SwitchStatementSyntax>(guarded.Block.Statements[3]);
        Assert.AreSequenceEqual(["case 0:", "case 1:", "case 2:", "default:"], phases.Sections.Select(static section => section.Labels.Single().ToString()));
        Assert.Contains("global::Ankus.CompilerServices.NativeGuc.WriteCheckError(result.Error, error);", phases.Sections[0].ToString());
        Assert.Contains("return 2;", phases.Sections[0].ToString());
        Assert.Contains("results[1] = global::Ankus.CompilerServices.NativeGuc.FromExtra(result.Extra);", phases.Sections[0].ToString());
        Assert.Contains("global::Settings.@Assign(value, global::Ankus.CompilerServices.NativeGuc.ReadExtra(arguments[1]));", phases.Sections[1].ToString());
        Assert.Contains("A GUC show hook returned null.", phases.Sections[2].ToString());
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains($"extern int {callback.Name}(int, AnkusValue *, AnkusValue *, int, AnkusError *, AnkusGucRead, AnkusExecute, AnkusGucLog, AnkusMemoryApi *);", native);
        AssertGucNativeDiagnosticOwnership(native);
        Assert.Contains("ankus_capture_error(data, error);\n            ankus_recovery_record(error, false);\n            ankus_free_error_data(data);", native.ReplaceLineEndings("\n"));
        Assert.Contains(".has_check = true, .has_assign = true, .has_show = true", native);
        Assert.DoesNotContain("ankus_raise_error(", native);
    }

    /// <summary>
    /// Tracking extra ownership requires native assignment notification even without an author assignment hook.
    /// </summary>
    /// <param name="options">A partial hook selection.</param>
    /// <param name="flags">The expected author hook availability.</param>
    [TestMethod]
    [DataRow("Check = nameof(Check)", ".has_check = true, .has_assign = false, .has_show = false")]
    [DataRow("Show = nameof(Show)", ".has_check = false, .has_assign = false, .has_show = true")]
    [DataRow("Assign = nameof(Assign)", ".has_check = false, .has_assign = true, .has_show = false")]
    public void GucPartialHookSelectionsRetainNativeAssignmentTracking(string options, string flags)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static partial class Settings
            {
            """ + "[Ankus.PgGucInt(\"demo.count\", 0, \"Count\", " + options + ")] public static partial int Count { get; }" + """
                public static Ankus.PgGucCheckResult<int> Check(int value, Ankus.PgGucSource source) => new(value);
                public static void Assign(int value, Ankus.PgGucExtra? extra) { }
                public static string Show(int value, Ankus.PgGucExtra? extra) => "shown";
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains(flags, native);
        Assert.Contains(".assign.integer = ankus_guc_", native);
        Assert.DoesNotContain("cannot run through shared_preload_libraries", native);
        Assert.Contains("extern int32_t RhEnableForkSupport(void);", native);
        Assert.Contains("int32_t fork_status = RhEnableForkSupport();", native);
        Assert.Contains("Ankus runtime fork support failed: %d", native);
        Assert.Contains("extern int RhEnterForkHost(void);", native);
        Assert.Contains("extern int RhExitForkHost(void);", native);
        Assert.Contains("ankus_fork_host_enter();", native);
        Assert.Contains("ankus_fork_host_exit();", native);
        Assert.Contains("return InitializingParallelWorker;", native);
        Assert.Contains("definition->worker_restore_pending = true;", native);
        Assert.Contains("ankus_guc_complete_worker_restore();", native);
        Assert.Contains("existing->scontext == PGC_SIGHUP", native);
        Assert.Contains("set_config_option_ext(definition->name, value,", native);
        Assert.Contains("existing->scontext, existing->source, existing->srole,", native);
        Assert.Contains("existing->flags |= GUC_ALLOW_IN_PARALLEL;", native);
        Assert.Contains("existing->flags = original_flags;", native);
        Assert.Contains("errcontext(\"configuration parameter \\\"%s\\\"\", definition->name);", native);
        Assert.Contains("error_context_stack = &error_context;", native);
        Assert.Contains("error_context_stack = error_context.previous;", native);
        Assert.Contains("GUC_ACTION_SET, true, ERROR, true);", native);
        Assert.Contains("definition->extra = existing->extra;", native);
        Assert.Contains("ankus_release_error(", native);
        AssertGucNativeDiagnosticOwnership(native);
        Assert.Contains("ankus_guc_read(", native);
        Assert.Contains("ankus_guc_prepare_encoding();", native);
        Assert.Contains("ankus_guc_log(", native);
        Assert.Contains("ankus_log_level(", native);
        Assert.Contains("ankus_log_enabled(", native);
        Assert.Contains("&frame->error, ankus_guc_read, NULL, ankus_guc_log, &memory));", native);
        Assert.DoesNotContain("ankus_raise_error(", native);
        if (options.StartsWith("Check", StringComparison.Ordinal))
        {
            Assert.Contains("ankus_guc_check(", native);
            Assert.Contains("ankus_spi_execute(", native);
            Assert.Contains("ankus_read_guc = ankus_guc_read;", native);
            Assert.Contains("&frame->error, ankus_guc_read, transactional ? ankus_spi_execute : NULL, ankus_guc_log, &memory));", native);
        }
        else
        {
            Assert.DoesNotContain("ankus_guc_check(", native);
            Assert.DoesNotContain("ankus_spi_execute(", native);
            Assert.DoesNotContain("ankus_read_guc", native);
            Assert.DoesNotContain("ankus_enum_supported(", native);
            string reporting = options.StartsWith("Show", StringComparison.Ordinal)
                ? "ankus_guc_report(&frame->error, frame->error.report_level == 0 ?\n" +
                    "                    (IsTransactionState() ? ERROR : FATAL) : ankus_log_level(frame->error.report_level - 1));"
                : "ankus_guc_report(&frame->error, frame->error.report_level == 0 ? FATAL :\n" +
                    "                    ankus_log_level(frame->error.report_level - 1));";
            Assert.Contains(reporting, native.ReplaceLineEndings("\n"));
            Assert.Contains("ankus_capture_error(", native);
        }

        if (options.StartsWith("Show", StringComparison.Ordinal))
        {
            Assert.Contains("ankus_guc_show(", native);
        }
        else
        {
            Assert.DoesNotContain("ankus_guc_show(", native);
        }

        Assert.Contains("ankus_guc_assign(", native);
    }

    /// <summary>
    /// Every SQL callback family and managed initialization retain the native error reporter they call after unwinding.
    /// </summary>
    /// <param name="source">A callback declaration that requires native error raising.</param>
    /// <param name="call">The native call using its owned error header.</param>
    [TestMethod]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static int Value() => 42; }", "ankus_raise_error(&error);")]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static System.Collections.Generic.IEnumerable<int> Values() => System.Array.Empty<int>(); }", "ankus_raise_error(&error);")]
    [DataRow("public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Row(Ankus.PgTriggerContext context) => null; }", "ankus_raise_error(error);")]
    [DataRow("public static class Functions { [Ankus.PgEventTrigger] public static void Event(Ankus.PgEventTriggerContext context) { } }", "ankus_raise_error(error);")]
    [DataRow("public static class Functions { [Ankus.PgInitialize] public static void Initialize() { } }", "ankus_raise_error(error);")]
    [DataRow("[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class SumValues : Ankus.IPgAggregate<long,int> { " +
        "public static long Transition(Ankus.PgAggregateContext context,long state, int value) => state + value; }", "ankus_raise_error(error);")]
    public void GucNativeBackendCallbacksRetainErrorRaising(string source, string call)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("""
            static void
            ankus_raise_error(AnkusError *error)
            {
                ankus_report(error, error->report_level == 0 ? ERROR : ankus_log_level(error->report_level - 1));
            }
            """.ReplaceLineEndings("\n"), native);
        Assert.Contains(call, native);
        Assert.Contains("ankus_spi_execute(", native);
    }

    /// <summary>
    /// Invalid metadata is rejected before PostgreSQL can partially install a definition or terminate bootstrap.
    /// </summary>
    /// <param name="attribute">The invalid typed attribute.</param>
    /// <param name="type">The declared property type.</param>
    /// <param name="id">The independently correctable diagnostic.</param>
    [TestMethod]
    [DataRow("Ankus.PgGucBool(\"nodot\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\".demo\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\"demo.\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\"demo..value\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\"demo.2value\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\"demo.$value\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\"demo.bad-name\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\"demo.value\\0\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\"demo.value\\uD800\", true, \"Description\")", "bool", "ANKUS181")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, null!)", "bool", "ANKUS182")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"bad\\0\")", "bool", "ANKUS182")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\", LongDescription = \"bad\\uD800\")", "bool", "ANKUS184")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\", Context = (Ankus.PgGucContext)(-1))", "bool", "ANKUS185")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\", Context = (Ankus.PgGucContext)7)", "bool", "ANKUS185")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\", Flags = (Ankus.PgGucOptions)1024)", "bool", "ANKUS186")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\", Flags = (Ankus.PgGucOptions)(-1))", "bool", "ANKUS186")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\", Flags = Ankus.PgGucOptions.IsName)", "bool", "ANKUS188")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\", Unit = Ankus.PgGucUnit.Bytes)", "bool", "ANKUS189")]
    [DataRow("Ankus.PgGucInt(\"demo.value\", 1, \"Description\", Unit = (Ankus.PgGucUnit)9)", "int", "ANKUS187")]
    [DataRow("Ankus.PgGucInt(\"demo.value\", 1, \"Description\", Unit = (Ankus.PgGucUnit)(-1))", "int", "ANKUS187")]
    [DataRow("Ankus.PgGucInt(\"demo.value\", 1, \"Description\", Minimum = 2)", "int", "ANKUS191")]
    [DataRow("Ankus.PgGucInt(\"demo.value\", 1, \"Description\", Maximum = 0)", "int", "ANKUS191")]
    [DataRow("Ankus.PgGucInt(\"demo.value\", 1, \"Description\", Minimum = 2, Maximum = 0)", "int", "ANKUS190")]
    [DataRow("Ankus.PgGucReal(\"demo.value\", double.NaN, \"Description\")", "double", "ANKUS193")]
    [DataRow("Ankus.PgGucReal(\"demo.value\", 1.0, \"Description\", Minimum = double.NaN)", "double", "ANKUS192")]
    [DataRow("Ankus.PgGucReal(\"demo.value\", 1.0, \"Description\", Maximum = double.NaN)", "double", "ANKUS192")]
    [DataRow("Ankus.PgGucReal(\"demo.value\", double.PositiveInfinity, \"Description\")", "double", "ANKUS193")]
    [DataRow("Ankus.PgGucReal(\"demo.value\", 1.0, \"Description\", Minimum = 2.0)", "double", "ANKUS193")]
    [DataRow("Ankus.PgGucReal(\"demo.value\", 1.0, \"Description\", Maximum = 0.0)", "double", "ANKUS193")]
    [DataRow("Ankus.PgGucString(\"demo.value\", null, \"Description\")", "string", "ANKUS195")]
    [DataRow("Ankus.PgGucString(\"demo.value\", \"bad\\0\", \"Description\")", "string?", "ANKUS194")]
    [DataRow("Ankus.PgGucString(\"demo.value\", \"bad\\uD800\", \"Description\")", "string", "ANKUS194")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\")", "int", "ANKUS183")]
    [DataRow("Ankus.PgGucInt(\"demo.value\", 1, \"Description\")", "long", "ANKUS183")]
    [DataRow("Ankus.PgGucReal(\"demo.value\", 1.0, \"Description\")", "float", "ANKUS183")]
    [DataRow("Ankus.PgGucBool(\"demo.value\", true, \"Description\")", "bool?", "ANKUS183")]
    public void InvalidGucMetadataIsDiagnosed(string attribute, string type, string id)
        => AssertInvalidGuc("public static partial class Settings { [" + attribute + "] public static partial " + type + " Value { get; } }", id);

    /// <summary>
    /// A valid setting property must be a supported, accessible partial declaration in partial classes.
    /// </summary>
    /// <param name="source">The unsupported property or container.</param>
    /// <param name="id">The independently correctable diagnostic.</param>
    [TestMethod]
    [DataRow("public partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public partial bool Value { get; } }", "ANKUS170")]
    [DataRow("public partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] private static partial bool Value { get; } }", "ANKUS174")]
    [DataRow("public partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static bool Value { get; } }", "ANKUS173")]
    [DataRow("public partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public bool this[int index] => true; }", "ANKUS203")]
    [DataRow("public partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static partial bool Value { get; set; } }", "ANKUS171")]
    [DataRow("public partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static partial bool Value { get; } public static partial bool Value => true; }", "ANKUS175")]
    [DataRow("public partial class Settings<T> { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static partial bool Value { get; } }", "ANKUS177")]
    [DataRow("public partial struct Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static partial bool Value { get; } }", "ANKUS176")]
    [DataRow("file partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static partial bool Value { get; } }", "ANKUS178")]
    [DataRow("public partial class Outer { private partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static partial bool Value { get; } } }", "ANKUS179")]
    [DataRow("public partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\"), Ankus.PgGucInt(\"demo.value\", 1, \"Value\")] public static partial bool Value { get; } }", "ANKUS169")]
    public void InvalidGucPropertyDeclarationsAreDiagnosed(string source, string id) => AssertInvalidGuc(source, id);

    /// <summary>
    /// Enum validation is specific to GUC label comparison and exact declared defaults.
    /// </summary>
    /// <param name="members">The enum declarations.</param>
    /// <param name="value">The proposed enum default.</param>
    /// <param name="id">The independently correctable diagnostic.</param>
    [TestMethod]
    [DataRow("First, first", "Mode.First", "ANKUS198")]
    [DataRow("[Ankus.PgGucLabel(\"same\")] First, [Ankus.PgGucLabel(\"SAME\")] Last", "Mode.First", "ANKUS198")]
    [DataRow("[Ankus.PgGucLabel(\"bad\\0\")] First", "Mode.First", "ANKUS197")]
    [DataRow("[Ankus.PgGucLabel(\"bad\\uD800\")] First", "Mode.First", "ANKUS197")]
    [DataRow("First = 42", "(Mode)43", "ANKUS196")]
    [DataRow("", "(Mode)0", "ANKUS196")]
    [DataRow("First", "0", "ANKUS196")]
    public void InvalidGucEnumMappingsAreDiagnosed(string members, string value, string id)
        => AssertInvalidGuc("public enum Mode { " + members + " } public static partial class Settings { [Ankus.PgGucEnum(\"demo.mode\", " +
            value + ", \"Mode\")] public static partial Mode Value { get; } }", id);

    /// <summary>
    /// Hooks resolve one exact, callable typed contract instead of relying on overload choice or nullable conversions,
    /// and the diagnostic states the first rule the named hook breaks.
    /// </summary>
    /// <param name="role">The hook role.</param>
    /// <param name="declaration">The invalid hook declaration.</param>
    /// <param name="problem">The problem the message states.</param>
    [TestMethod]
    [DataRow("Check", "", "'Settings' has no method named 'Hook'")]
    [DataRow("Check", "public static bool Hook(int value, Ankus.PgGucSource source) => true;", "'Hook' returns bool")]
    [DataRow("Check", "public static Ankus.PgGucCheckResult<long> Hook(int value, Ankus.PgGucSource source) => new(value);", "'Hook' returns Ankus.PgGucCheckResult<long>")]
    [DataRow("Check", "public static Ankus.PgGucCheckResult<int>? Hook(int value, Ankus.PgGucSource source) => null;", "'Hook' returns Ankus.PgGucCheckResult<int>?")]
    [DataRow("Check", "public static ref Ankus.PgGucCheckResult<int> Hook(int value, Ankus.PgGucSource source) => throw new System.Exception();", "'Hook' returns by reference")]
    [DataRow("Check", "private static Ankus.PgGucCheckResult<int> Hook(int value, Ankus.PgGucSource source) => new(value);", "'Hook' is not public or internal")]
    [DataRow("Check", "public Ankus.PgGucCheckResult<int> Hook(int value, Ankus.PgGucSource source) => new(value);", "'Hook' is not static")]
    [DataRow("Check", "public static Ankus.PgGucCheckResult<int> Hook<T>(int value, Ankus.PgGucSource source) => new(value);", "'Hook' is generic")]
    [DataRow("Check", "public static Ankus.PgGucCheckResult<int> Hook(ref int value, Ankus.PgGucSource source) => new(value);", "'Hook' parameter 'value' is passed by reference")]
    [DataRow("Check", "public static Ankus.PgGucCheckResult<int> Hook(int value, Ankus.PgGucSource source = default) => new(value);", "'Hook' parameter 'source' has a default")]
    [DataRow("Check", "public static Ankus.PgGucCheckResult<int> Hook(int value, int source) => new(value);", "'Hook' takes int as its second parameter")]
    [DataRow("Check", "public static Ankus.PgGucCheckResult<int> Hook(int value, Ankus.PgGucSource source) => new(value); public static void Hook() { }", "'Hook' has 2 overloads")]
    [DataRow("Assign", "public static int Hook(int value, Ankus.PgGucExtra? extra) => value;", "'Hook' returns int")]
    [DataRow("Assign", "public static void Hook(int value, Ankus.PgGucExtra extra) { }", "'Hook' takes Ankus.PgGucExtra as its second parameter")]
    [DataRow("Assign", "[System.Diagnostics.Conditional(\"NEVER\")] public static void Hook(int value, Ankus.PgGucExtra? extra) { }", "'Hook' has Conditional")]
    [DataRow("Assign", "public static partial void Hook(int value, Ankus.PgGucExtra? extra); public static async partial void Hook(int value, Ankus.PgGucExtra? extra) { await System.Threading.Tasks.Task.Yield(); }", "'Hook' is async")]
    [DataRow("Show", "public static string? Hook(int value, Ankus.PgGucExtra? extra) => null;", "'Hook' returns string?")]
    [DataRow("Show", "public static ref readonly string Hook(int value, Ankus.PgGucExtra? extra) => throw new System.Exception();", "'Hook' returns by reference")]
    [DataRow("Show", "public static string Hook(long value, Ankus.PgGucExtra? extra) => \"shown\";", "'Hook' takes long as its first parameter")]
    public void InvalidGucHookContractsAreDiagnosed(string role, string declaration, string problem)
    {
        string id = role switch
        {
            "Check" => "ANKUS199",
            "Assign" => "ANKUS200",
            _ => "ANKUS201",
        };
        string source = "public partial class Settings { [Ankus.PgGucInt(\"demo.value\", 1, \"Value\", " + role +
            " = \"Hook\")] public static partial int Value { get; } " + declaration + " }";
        AssertInvalidGuc(source, id);
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Assert.EndsWith("; " + problem, Assert.ContainsSingle(diagnostics).GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Duplicate native names use ASCII folding, while non-ASCII labels remain distinct.
    /// </summary>
    [TestMethod]
    public void GucDuplicateNamesUsePostgresAsciiCaseFolding()
    {
        AssertInvalidGuc("""
            public static partial class Settings
            {
                [Ankus.PgGucBool("DEMO.Value", true, "First")] public static partial bool First { get; }
                [Ankus.PgGucBool("demo.value", false, "Second")] public static partial bool Second { get; }
            }
            """, "ANKUS202");
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public enum Mode { [Ankus.PgGucLabel("É")] Upper, [Ankus.PgGucLabel("é")] Lower }
            public static partial class Settings
            {
                [Ankus.PgGucEnum("démo.value", Mode.Lower, "First")] public static partial Mode First { get; }
                [Ankus.PgGucBool("dÉmo.value", false, "Second")] public static partial bool Second { get; }
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        Assert.Contains(".boot.integer = 1", ManifestValue(compilation, "Ankus.NativeSource"));
    }

    /// <summary>
    /// Registration precedes user initialization, shares one loader entry point, and preserves ordinary installation SQL.
    /// </summary>
    [TestMethod]
    public void GucRegistrationComposesWithInitializationAndSql()
    {
        const string source = """
            public static partial class Settings
            {
                [Ankus.PgGucInt("demo.value", 42, "Value")] public static partial int Value { get; }
                [Ankus.PgInitialize] public static void Initialize() { System.GC.KeepAlive(Value); }
                [Ankus.PgFunction] public static int Read() => Value;
            }
            """;
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertGucCompilation(compilation, diagnostics);
        string[] exports = ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.HasCount(4, exports);
        Assert.AreEqual("Pg_magic_func", exports[0]);
        Assert.AreEqual("_PG_init", exports[1]);
        Assert.AreEqual("pg_finfo_" + exports[2], exports[3]);
        Assert.AreEqual($"CREATE FUNCTION \"read\"()\nRETURNS integer AS 'MODULE_PATHNAME', '{exports[2]}' LANGUAGE c " +
            "VOLATILE PARALLEL UNSAFE STRICT SECURITY INVOKER NOT LEAKPROOF COST 1;\n",
            InstallationBody(compilation).ReplaceLineEndings("\n"));
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        int loader = native.IndexOf("PGDLLEXPORT void _PG_init(void)\n", StringComparison.Ordinal);
        int binding = native.IndexOf("ankus_read_guc = ankus_guc_read;", loader, StringComparison.Ordinal);
        int registration = native.IndexOf("ankus_guc_register(&ankus_guc_", binding, StringComparison.Ordinal);
        int invocation = native.IndexOf("ANKUS_MANAGED_INVOKE(status, error, ankus_managed_", registration, StringComparison.Ordinal);
        Assert.IsGreaterThan(loader, binding);
        Assert.IsGreaterThan(binding, registration);
        Assert.IsGreaterThan(registration, invocation);
    }

    /// <summary>
    /// Ordinary SQL access needs native typed reads without bringing unused managed hook machinery into the library.
    /// </summary>
    [TestMethod]
    public void GucNativeOnlyWithSqlEmitsReadsWithoutHookHelpers()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static partial class Settings
            {
                [Ankus.PgGucInt("demo.count", 42, "Count")] public static partial int Count { get; }
                [Ankus.PgFunction] public static int Read() => Count;
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource");
        Assert.Contains("ankus_read_guc = ankus_guc_read;", native);
        Assert.DoesNotContain("ankus_guc_check(", native);
        Assert.DoesNotContain("ankus_guc_assign(", native);
        Assert.DoesNotContain("ankus_guc_show(", native);
        Assert.DoesNotContain("cannot run through shared_preload_libraries", native);
        Assert.HasCount(4, ManifestValue(compilation, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Hidden labels remain legal input/default mappings even when no label appears in PostgreSQL suggestions.
    /// </summary>
    [TestMethod]
    public void GucAllHiddenEnumLabelsRemainUsable()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public enum Mode : long
            {
                [Ankus.PgGucLabel("", Hidden = true)] First = long.MinValue,
                [Ankus.PgGucLabel("last", Hidden = true)] Last = long.MaxValue,
            }
            public static partial class Settings
            {
                [Ankus.PgGucEnum("demo.mode", Mode.Last, "Mode")] public static partial Mode Value { get; }
            }
            """);
        AssertGucCompilation(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("    { \"\", 0, true },\n    { \"\\154\\141\\163\\164\", 1, true },\n    { NULL, 0, false }", native);
        Assert.Contains(".boot.integer = 1", native);
    }

    /// <summary>
    /// Registration order and emitted identities remain deterministic when source declaration order changes.
    /// </summary>
    [TestMethod]
    public void GucPropertyOrderingDoesNotChangeGeneratedArtifacts()
    {
        const string first = "[Ankus.PgGucBool(\"demo.a\", true, \"First\")] public static partial bool First { get; }";
        const string second = "[Ankus.PgGucInt(\"DEMO.z\", 42, \"Last\")] public static partial int Last { get; }";
        (Compilation forward, ImmutableArray<Diagnostic> forwardDiagnostics) = Generate("public static partial class Settings { " + first + second + " }");
        (Compilation reverse, ImmutableArray<Diagnostic> reverseDiagnostics) = Generate("public static partial class Settings { " + second + first + " }");
        AssertGucCompilation(forward, forwardDiagnostics);
        AssertGucCompilation(reverse, reverseDiagnostics);
        foreach (string key in new[] { "Ankus.NativeSource", "Ankus.Exports", "Ankus.Sql", "Ankus.Relocatable" })
        {
            Assert.AreEqual(ManifestValue(forward, key), ManifestValue(reverse, key));
        }

        string native = ManifestValue(forward, "Ankus.NativeSource");
        int firstBoot = native.IndexOf(".boot.boolean = true", StringComparison.Ordinal);
        int secondBoot = native.IndexOf(".boot.integer = 42", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, firstBoot);
        Assert.IsGreaterThan(firstBoot, secondBoot);
    }

    /// <summary>
    /// Both native bridge shapes own older PostgreSQL's borrowed diagnostic fields until capture completes.
    /// </summary>
    /// <param name="source">The full or minimal native hook source.</param>
    private static void AssertGucNativeDiagnosticOwnership(string source)
    {
        string native = source.ReplaceLineEndings("\n");
        int copy = native.IndexOf("ankus_copy_error_data(void)", StringComparison.Ordinal);
        int free = native.IndexOf("ankus_free_error_data(ErrorData *data)", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, copy);
        Assert.IsGreaterThan(copy, free);
        string copying = native[copy..free];
        int version = copying.IndexOf("#if PG_VERSION_NUM < 170000", StringComparison.Ordinal);
        int end = copying.IndexOf("#endif", version, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, version);
        Assert.IsGreaterThan(version, end);
        foreach (string field in new[] { "filename", "funcname", "domain", "context_domain", "message_id" })
        {
            Assert.Contains($"if (data->{field} != NULL) data->{field} = pstrdup(data->{field});", copying[version..end]);
            Assert.Contains($"data->{field} = NULL;", native[free..]);
        }

        Assert.Contains("data->filename, data->funcname, data->domain, data->context_domain, data->message_id", native[free..]);
        Assert.Contains("pfree((void *) fields[index]);", native[free..]);
        Assert.Contains("FreeErrorData(data);", native[free..]);
        Assert.Contains("ErrorData *data = ankus_copy_error_data();\n            FlushErrorState();\n            flushed = true;\n            ankus_guc_capture_error(data, error);\n            ankus_recovery_record(error, false);\n            ankus_free_error_data(data);", native);
        // A hook can log or read a setting while the caller's error is still pending, so the guard saves that error and
        // pushes it back after flushing its own.
        Assert.Contains("AnkusPendingError *volatile pending = ankus_pending_error_save();", native);
        Assert.Contains("if (flushed)\n        ankus_pending_error_restore(pending);\n    else\n        ankus_pending_error_release(pending);", native);
    }

    /// <summary>
    /// Validated GUC source compiles and emits without warning or error diagnostics.
    /// </summary>
    private void AssertGucCompilation(Compilation compilation, ImmutableArray<Diagnostic> diagnostics)
    {
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        using var assembly = new MemoryStream();
        Assert.IsTrue(compilation.Emit(assembly, cancellationToken: context.CancellationToken).Success);
    }

    /// <summary>
    /// Requires the dedicated declaration error and excludes unrelated parser or binding failures.
    /// </summary>
    private void AssertInvalidGuc(string source, string id)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetMessage(CultureInfo.InvariantCulture)));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id != "CS9248"));
    }
}
