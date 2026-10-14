using Ankus.Postgres;

namespace Ankus.TestExtension;

/// <summary>
/// Executes field-named callback values through selected-header native method-table storage.
/// </summary>
public static unsafe partial class NativeFieldCallbackFunctions
{
    private static int s_mode;
    private static int s_calls;
    private static int s_callbackFinally;
    private static int s_outerFinally;
    private static bool s_caught;

    /// <summary>
    /// Supplies the stable field-specific signature without naming a collected graph index.
    /// </summary>
    [PgNativeCallback(nameof(ExecuteScan))]
    private static partial CustomExecMethods_ExecCustomScanCallback Execute { get; }

    /// <summary>
    /// Assigns and reads a native method table, then invokes its exact callback signature over owned native storage.
    /// </summary>
    /// <param name="mode">
    /// Zero for success, one for managed failure, two for native failure, three for caught native failure, four for an
    /// ordinary exception and five for an ordinary exception the caller catches from a subtransaction after it crosses
    /// the native frame.
    /// </param>
    /// <returns>The shared state mutation and preserved callback and result addresses.</returns>
    [PgFunction]
    public static string NativeFieldCallbackRoundtrip(int mode)
    {
        s_mode = mode;
        s_calls = 0;
        s_callbackFinally = 0;
        s_outerFinally = 0;
        s_caught = false;
        try
        {
            using PgMemoryContext owner = PgMemoryContext.Create("field callback storage");
            using PgNativeBox<TupleTableSlot> slot = owner.CreateBox<TupleTableSlot>(default);
            CustomScanState value = default;
            value.ss.ps.type = NodeTag.T_CustomScanState;
            value.ss.ss_ScanTupleSlot = (TupleTableSlot*)slot.DangerousGetPointer();
            value.flags = 7;
            using PgNativeBox<CustomScanState> state = owner.CreateBox(value);
            using PgNativeBox<CustomExecMethods> methods = owner.CreateBox(new CustomExecMethods { ExecCustomScan = Execute });
            CustomExecMethods_ExecCustomScanCallback callback = methods.Value.ExecCustomScan;
            if (mode == 5)
            {
                // A subtransaction rolls the callback's error back, so the backend is usable after the catch.
                CustomScanState* scan = (CustomScanState*)state.DangerousGetPointer();
                try
                {
                    PgTransaction.RunInSubtransaction(() => _ = callback.Invoke(scan));
                }
                catch (PgException error)
                {
                    s_caught = Spi.ExecuteScalar<int>("SELECT 6 * 7") == 42;
                    return $"{error.GetType().Name}|{error.SqlState}|{error.Message}";
                }
            }

            TupleTableSlot* first = callback.Invoke((CustomScanState*)state.DangerousGetPointer());
            TupleTableSlot* second = methods.Value.ExecCustomScan.Invoke((CustomScanState*)state.DangerousGetPointer());
            return $"{state.Value.flags}|{first == slot.DangerousGetPointer()}|{second == first}|" +
                $"{callback.DangerousGetAddress() == Execute.DangerousGetAddress()}";
        }
        finally
        {
            s_outerFinally++;
        }
    }

    /// <summary>
    /// Reports managed invocation and unwind observations after the native callback has returned or raised an error.
    /// </summary>
    /// <returns>Callback entries, callback finally executions, outer finally executions and the caught-native marker.</returns>
    [PgFunction]
    public static string NativeFieldCallbackState() => $"{s_calls}|{s_callbackFinally}|{s_outerFinally}|{s_caught}";

    /// <summary>
    /// Mutates original native bytes, returns the original slot address and supplies controlled managed and native errors.
    /// </summary>
    private static TupleTableSlot* ExecuteScan(CustomScanState* state)
    {
        s_calls++;
        try
        {
            state->flags++;
            if (s_mode == 1)
            {
                throw new PgException("P7511", "managed field callback failure", "field callback detail", "field callback hint");
            }

            if (s_mode is 4 or 5)
            {
                throw new InvalidOperationException("ordinary field callback failure");
            }

            if (s_mode is 2 or 3)
            {
                try
                {
                    _ = Spi.Execute("SELECT 1 / 0");
                }
                catch (PgException error) when (s_mode == 3)
                {
                    s_caught = error.SqlState == "22012" && error.Message == "division by zero" &&
                        error.Detail is null && error.Hint is null && Spi.ExecuteScalar<int>("SELECT 6 * 7") == 42;
                }
            }

            return state->ss.ss_ScanTupleSlot;
        }
        finally
        {
            s_callbackFinally++;
        }
    }
}
