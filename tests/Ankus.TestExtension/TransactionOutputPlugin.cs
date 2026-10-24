using System.Text;
using Ankus.Postgres;

namespace Ankus.TestExtension;

/// <summary>
/// Reads the database name in its startup callback through <see cref="PgTransaction.Run{TResult}(Func{TResult})"/>, which
/// a WAL sender calls outside a transaction and the SQL decoding functions call inside one, and writes what it found as
/// each decoded transaction begins.
/// </summary>
/// <remarks>
/// Setting <c>ankus_transaction_test.fail</c> to <c>on</c> makes the startup query divide by zero.
/// </remarks>
public static unsafe partial class TransactionOutputPlugin
{
    private static string s_startup = string.Empty;

    /// <summary>
    /// Supplies the startup callback.
    /// </summary>
    [PgNativeCallback(nameof(Startup))]
    private static partial OutputPluginCallbacks_startup_cbCallback StartupCallback { get; }

    /// <summary>
    /// Supplies the transaction-begin callback.
    /// </summary>
    [PgNativeCallback(nameof(BeginTransaction))]
    private static partial OutputPluginCallbacks_begin_cbCallback BeginCallback { get; }

    /// <summary>
    /// Supplies the row-change callback.
    /// </summary>
    [PgNativeCallback(nameof(Change))]
    private static partial OutputPluginCallbacks_change_cbCallback ChangeCallback { get; }

    /// <summary>
    /// Supplies the transaction-commit callback.
    /// </summary>
    [PgNativeCallback(nameof(CommitTransaction))]
    private static partial OutputPluginCallbacks_commit_cbCallback CommitCallback { get; }

    /// <summary>
    /// Registers the plugin's callbacks.
    /// </summary>
    /// <param name="callbacks">PostgreSQL's callback table, valid only during this call.</param>
    [PgOutputPlugin]
    public static void Initialize(OutputPluginCallbacks* callbacks)
    {
        ArgumentNullException.ThrowIfNull(callbacks);
        callbacks->startup_cb = StartupCallback;
        callbacks->begin_cb = BeginCallback;
        callbacks->change_cb = ChangeCallback;
        callbacks->commit_cb = CommitCallback;
    }

    /// <summary>
    /// Selects text output and reads the database name, starting a transaction when PostgreSQL has none.
    /// </summary>
    private static void Startup(LogicalDecodingContext* context, OutputPluginOptions* options, bool isInit)
    {
        options->output_type = OutputPluginOutputType.OUTPUT_PLUGIN_TEXTUAL_OUTPUT;
        string transaction = NativeMethods.IsTransactionState() ? "in a transaction" : "outside a transaction";
        using (PgMemoryContext? top = PgMemoryContext.Get(PgMemoryContextKind.TopTransaction))
        {
            transaction += top is null ? ", no top transaction context" : ", top transaction context";
        }

        string? database = PgTransaction.Run(static () => Spi.ExecuteScalar<string>(
            "SELECT current_database()::text WHERE 1 / CASE WHEN current_setting('ankus_transaction_test.fail', true) = 'on' THEN 0 ELSE 1 END = 1"));
        s_startup = transaction + ":" + database;
    }

    /// <summary>
    /// Writes what the startup callback found.
    /// </summary>
    private static void BeginTransaction(LogicalDecodingContext* context, ReorderBufferTXN* transaction) => Write(context, s_startup);

    /// <summary>
    /// Writes one line per change.
    /// </summary>
    private static void Change(LogicalDecodingContext* context, ReorderBufferTXN* transaction, RelationData* relation,
        ReorderBufferChange* change) => Write(context, "change");

    /// <summary>
    /// Writes nothing at commit.
    /// </summary>
    private static void CommitTransaction(LogicalDecodingContext* context, ReorderBufferTXN* transaction, ulong commitLsn)
    {
    }

    /// <summary>
    /// Writes one ASCII line to the decoding output.
    /// </summary>
    private static void Write(LogicalDecodingContext* context, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        NativeMethods.OutputPluginPrepareWrite(context, true);
        fixed (byte* data = bytes)
        {
            NativeMethods.appendBinaryStringInfo(context->@out, (sbyte*)data, bytes.Length);
        }

        NativeMethods.OutputPluginWrite(context, true);
    }
}
