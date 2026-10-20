namespace Ankus.Examples.Notify;

/// <summary>
/// Ports pgrx's in-backend notify tests, which exercise the wrappers directly rather than through SQL.
/// </summary>
/// <remarks>
/// Test publications install these functions; ordinary publications omit them. The delivery tests, which need separate
/// listening sessions, run from the integration test client.
/// </remarks>
public static partial class NotifyTests
{
    /// <summary>
    /// A NUL character in either argument is rejected before PostgreSQL would truncate it.
    /// </summary>
    [PgTest]
    public static void NotifyRejectsInteriorNul()
    {
        RequireArgumentError("channel", () => Notifications.Notify("chan\0nul", "payload"));
        RequireArgumentError("payload", () => Notifications.Notify("chan", "pay\0load"));
    }

    /// <summary>
    /// LISTEN and UNLISTEN reject a NUL character in the channel name.
    /// </summary>
    [PgTest]
    public static void ListenRejectsInteriorNul()
    {
        RequireArgumentError("channel", () => Notifications.Listen("chan\0nul"));
        RequireArgumentError("channel", () => Notifications.Unlisten("chan\0nul"));
    }

    /// <summary>
    /// PostgreSQL reports an over-length payload as an error.
    /// </summary>
    [PgTest(ExpectedError = "payload string too long", SearchPath = ["pg_catalog", PgSearchPath.ExtensionSchema])]
    public static void OversizePayloadErrors()
    {
        string big = new('x', 9000);
        _ = Spi.Execute(Spi.Sql($"SELECT ankus_notify('chan', {big})"));
    }

    /// <summary>
    /// A rollback callback runs after its subtransaction has left the in-progress state, so every wrapper refuses it.
    /// </summary>
    [PgTest]
    public static void WrappersRejectCallsOutsideATransactionInProgress()
    {
        var rejected = new List<string>();
        using PgSubtransactionCallback registration = PgTransaction.RegisterSubtransactionCallback(PgSubtransactionEvent.Abort,
            (_, _) =>
            {
                Attempt(() => Notifications.Notify("aborting", "payload"));
                Attempt(() => Notifications.Listen("aborting"));
                Attempt(() => Notifications.Unlisten("aborting"));
                Attempt(Notifications.UnlistenAll);
            });

        // The PL/pgSQL exception block rolls back its own subtransaction and runs the callback.
        _ = Spi.Execute("DO $$ BEGIN PERFORM 1 / 0; EXCEPTION WHEN division_by_zero THEN NULL; END $$");
        if (rejected.Count != 4 || rejected.Any(static message => message != "LISTEN, NOTIFY and UNLISTEN require a transaction in progress."))
        {
            throw new InvalidOperationException($"Expected four rejected calls, observed: {string.Join(" | ", rejected)}");
        }

        // The enclosing transaction is still in progress and can notify normally.
        Notifications.Notify("in_progress", "payload");

        void Attempt(Action call)
        {
            try
            {
                call();
                rejected.Add("accepted");
            }
            catch (InvalidOperationException error)
            {
                rejected.Add(error.Message);
            }
        }
    }

    /// <summary>
    /// Requires an argument error naming the rejected parameter.
    /// </summary>
    /// <param name="parameter">The expected parameter name.</param>
    /// <param name="call">The rejected wrapper call.</param>
    private static void RequireArgumentError(string parameter, Action call)
    {
        try
        {
            call();
        }
        catch (ArgumentException error) when (error.ParamName == parameter)
        {
            return;
        }

        throw new InvalidOperationException($"The wrapper accepted a NUL character in '{parameter}'.");
    }
}
