using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Backend-capable extensions emit one permanent native dispatcher with stable managed event values.
    /// </summary>
    [TestMethod]
    public void NativeBridgeMapsEveryTransactionAndSubtransactionEvent()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgFunction] public static int Value() => 1; }");
        AssertMemoryCompilationSucceeds(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("RegisterXactCallback(ankus_transaction_callback, NULL);", native);
        Assert.Contains("RegisterSubXactCallback(ankus_subtransaction_callback, NULL);", native);
        Assert.Contains("case XACT_EVENT_ABORT:\n            ankus_transaction_dispatch(0, 0, 0, 0, ANKUS_CALLBACK_PANIC, false);", native);
        Assert.Contains("case XACT_EVENT_COMMIT:\n            ankus_transaction_dispatch(0, 1, 0, 0, ANKUS_CALLBACK_PANIC, false);", native);
        Assert.Contains("case XACT_EVENT_PRE_COMMIT:\n            ankus_transaction_dispatch(0, 2, 0, 0, ANKUS_CALLBACK_ERROR, true);", native);
        Assert.Contains("case XACT_EVENT_PARALLEL_ABORT:\n            ankus_transaction_dispatch(0, 3, 0, 0, ANKUS_CALLBACK_PANIC, false);", native);
        Assert.Contains("case XACT_EVENT_PARALLEL_COMMIT:\n            ankus_transaction_dispatch(0, 4, 0, 0, ANKUS_CALLBACK_PANIC, false);", native);
        Assert.Contains("case XACT_EVENT_PARALLEL_PRE_COMMIT:\n            ankus_transaction_dispatch(0, 5, 0, 0, ANKUS_CALLBACK_ERROR, false);", native);
        Assert.Contains("case XACT_EVENT_PREPARE:\n            ankus_transaction_dispatch(0, 6, 0, 0, ANKUS_CALLBACK_PANIC, false);", native);
        Assert.Contains("case XACT_EVENT_PRE_PREPARE:\n            ankus_transaction_dispatch(0, 7, 0, 0, ANKUS_CALLBACK_ERROR, true);", native);
        Assert.Contains("case SUBXACT_EVENT_ABORT_SUB:\n            ankus_transaction_dispatch(1, 0, subtransaction_id, parent_subtransaction_id, ANKUS_CALLBACK_WARNING, false);", native);
        Assert.Contains("case SUBXACT_EVENT_COMMIT_SUB:\n            /* A committed savepoint still belongs to its parent, which PostgreSQL then aborts. */\n            ankus_transaction_dispatch(1, 1, subtransaction_id, parent_subtransaction_id, ANKUS_CALLBACK_ERROR, false);", native);
        Assert.Contains("case SUBXACT_EVENT_PRE_COMMIT_SUB:\n            ankus_transaction_dispatch(1, 2, subtransaction_id, parent_subtransaction_id, ANKUS_CALLBACK_ERROR, true);", native);
        Assert.Contains("case SUBXACT_EVENT_START_SUB:\n            ankus_transaction_dispatch(1, 3, subtransaction_id, parent_subtransaction_id, ANKUS_CALLBACK_ERROR, true);", native);
    }

    /// <summary>
    /// Registration travels through the guarded request ABI and hides guard-owned subtransactions from consumers.
    /// </summary>
    [TestMethod]
    public void NativeRegistrationUsesGuardAndSuppressesImplementationSubtransactions()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgFunction] public static void Register() => " +
            "Ankus.PgTransaction.RegisterCallback(Ankus.PgTransactionEvent.Commit, () => { }); }");
        AssertMemoryCompilationSucceeds(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("ANKUS_SPI_GUC_READ,\n    ANKUS_SPI_TRANSACTION_CALLBACKS", native);
        Assert.Contains("SPIPlanPtr plan;\n    intptr_t callback;\n    int64 cursor_id;", native);
        Assert.Contains("bool transaction_callbacks = request->operation == ANKUS_SPI_TRANSACTION_CALLBACKS;", native);
        Assert.Contains("ankus_transaction_ensure((AnkusTransactionManaged) request->callback,", native);
        Assert.Contains("request->scalar_operation);", native);
        Assert.Contains("find_rendezvous_variable(\"ankus_internal_subtransactions\")", native);
        Assert.Contains("registry->starting++;\n    PG_TRY();\n    {\n        BeginInternalSubTransaction(NULL);\n    }\n    PG_FINALLY();\n    {\n        ankus_subtransactions->starting--;\n    }", native);
        Assert.Contains("if (ankus_internal_subtransaction(event, subtransaction_id))\n    {\n        return;\n    }", native);
        Assert.Contains("registry->entries[index].subtransaction == subtransaction && registry->entries[index].transaction == transaction", native);
        int rollback = native.IndexOf("RollbackAndReleaseCurrentSubTransaction();",
            native.IndexOf("ankus_spi_execute_core(AnkusRequest *request, AnkusResult *result, AnkusError *error)\n{", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, rollback);
        Assert.IsGreaterThan(rollback, native.IndexOf("ankus_trim_internal_subtransactions();", rollback, StringComparison.Ordinal));
        Assert.DoesNotContain("BeginInternalSubTransaction(NULL);", native[native.IndexOf("static int\nankus_spi_execute_core(", StringComparison.Ordinal)..]);
    }

    /// <summary>
    /// Managed failures unwind callback scopes before native ERROR or PANIC reporting, with SQL limited to reversible phases.
    /// </summary>
    [TestMethod]
    public void TransactionDispatchRestoresManagedAndNativeScopesBeforeReporting()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgFunction] public static int Value() => 1; }");
        AssertMemoryCompilationSucceeds(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("&error, sql ? ankus_spi_execute : NULL, (intptr_t) ankus_transaction_log, &memory));", native);
        Assert.Contains("bool direct_spi = transaction_direct_spi || ankus_parallel_without_subtransactions();", native);
        int callbackGuard = native.IndexOf("if (recovery_subtransaction)", StringComparison.Ordinal);
        int guardedSubtransaction = native.IndexOf("ankus_begin_internal_subtransaction();", callbackGuard,
            StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, callbackGuard);
        Assert.IsGreaterThan(callbackGuard, guardedSubtransaction);
        Assert.Contains("if (transaction_direct_spi && transaction_frame->failed)", native);
        Assert.Contains("ankus_capture_error(data, &transaction_frame->failure);", native);
        Assert.Contains("ankus_memory_protection = protection.previous;\n    if (snapshot_owned)\n    {\n        PopActiveSnapshot();", native);
        Assert.Contains("ankus_transaction_report(&error, failure == ANKUS_CALLBACK_ERROR ? ERROR\n            : failure == ANKUS_CALLBACK_WARNING ? WARNING : PANIC);", native);
        Assert.Contains("else if (sql && kind == 0)\n    {", native);
        Assert.Contains("AfterTriggerFireDeferred();", native);
        Assert.Contains("PG_FINALLY();\n    {\n        ankus_release_error(error);\n    }", native);
        Assert.Contains("Transaction callbacks require an active PostgreSQL transaction", native);
    }

    /// <summary>
    /// Built-in value operations avoid subtransactions on success and require actual rollback after native ERROR.
    /// </summary>
    [TestMethod]
    public void ValueOperationsRequireActualErrorRecovery()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class Functions { [Ankus.PgFunction] public static decimal Value(decimal value) => value + 1; }");
        AssertMemoryCompilationSucceeds(compilation, diagnostics);
        string native = ManifestValue(compilation, "Ankus.NativeSource").ReplaceLineEndings("\n");
        Assert.Contains("ankus_uses_builtin_range(const AnkusRequest *request)", native);
        Assert.Contains("type == INT4RANGEOID || type == INT8RANGEOID || type == NUMRANGEOID", native);
        Assert.Contains("request->parameter_count > 0 && request->parameters != NULL", native);
        Assert.Contains("ankus_is_builtin_range(request->parameters[0].type_oid)", native);
        Assert.Contains("bool lightweight = !input_recovery && (numeric || temporal || network || geometry ||", native);
        Assert.Contains("(datum && request->scalar_operation == 6));", native);
        // TryParse before PostgreSQL 16 nests its own subtransaction, also inside an explicit scope.
        Assert.Contains("!ankus_parallel_without_subtransactions() && (transaction_frame == NULL || transaction_frame->scope);", native);
        Assert.Contains("bool recovery_subtransaction = (!direct_spi || input_recovery || (subtransaction && !ankus_parallel_without_subtransactions())) &&\n" +
            "        !lightweight;", native);
        // From PostgreSQL 16, a soft input error needs no subtransaction.
        Assert.Contains("bool soft_input = request->recover_input && request->scalar_operation == 0 &&", native);
        Assert.Contains("bool recovered = false;", native);
        Assert.Contains("if (lightweight && operation_context != NULL)", native);
        Assert.Contains("MemoryContextDelete((MemoryContext) operation_context);", native);
    }
}
