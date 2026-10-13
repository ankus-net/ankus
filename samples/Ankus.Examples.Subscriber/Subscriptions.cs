[assembly: PgSql("subscription_table", """
    CREATE TABLE subscription (
        name text PRIMARY KEY,
        provider text NOT NULL,
        publication text NOT NULL,
        slot text NOT NULL,
        pid integer
    );
    """, Order = PgSqlOrder.Bootstrap)]

namespace Ankus.Examples.Subscriber;

/// <summary>
/// Creates and removes subscriptions, each run by its own background worker.
/// </summary>
public static class Subscriptions
{
    private const string Library = "Ankus.Examples.Subscriber";

    /// <summary>
    /// Records a subscription and starts its worker, which copies the publication's tables and then streams their
    /// changes. The worker runs as the current user in the current database.
    /// </summary>
    /// <param name="name">The subscription's name.</param>
    /// <param name="provider">The provider's connection string.</param>
    /// <param name="publication">The provider's publication.</param>
    [PgFunction]
    public static void Subscribe(string name, string provider, string publication)
    {
        Spi.Execute($"INSERT INTO {Spi.QuoteQualifiedIdentifier(Schema(), "subscription")} (name, provider, publication, slot) VALUES ($1, $2, $3, $4)",
            SpiParameter.Create(name), SpiParameter.Create(provider), SpiParameter.Create(publication),
            SpiParameter.Create("ankus_subscriber_" + name));
        (uint database, uint user) = Spi.ExecuteScalars<uint, uint>(
            "SELECT d.oid, r.oid FROM pg_database d, pg_roles r WHERE d.datname = current_database() AND r.rolname = current_user");
        PgBackgroundWorkerOptions options = new("ankus subscriber " + name, Library, nameof(Apply))
        {
            Type = "ankus subscriber",
            DatabaseAccess = true,
            Argument = ((nuint)database << 32) | user,
            Extra = name,
            RestartDelay = TimeSpan.FromSeconds(5),
        };
        if (!PgBackgroundWorker.TryStart(options, out PgBackgroundWorkerHandle? worker))
        {
            throw new PgException(PgSqlStates.ConfigurationLimitExceeded, "No background worker slot is free for the subscription.");
        }

        worker.Dispose();
    }

    /// <summary>
    /// Stops a subscription's worker, then drops its slot on the provider, its replication origin and its record.
    /// </summary>
    /// <param name="name">The subscription's name.</param>
    [PgFunction]
    public static void Unsubscribe(string name)
    {
        (string? provider, string? slot, int? pid) = Spi.ExecuteScalars<string?, string?, int?>(
            $"SELECT provider, slot, pid FROM {Spi.QuoteQualifiedIdentifier(Schema(), "subscription")} WHERE name = $1 FOR UPDATE", SpiParameter.Create(name));
        if (provider is null || slot is null)
        {
            throw new PgException(PgSqlStates.UndefinedObject, $"Subscription \"{name}\" does not exist.");
        }

        if (pid is not null)
        {
            // A terminated worker exits cleanly, so PostgreSQL does not restart it.
            Spi.Execute("SELECT pg_terminate_backend($1)", SpiParameter.Create(pid.Value));
            for (int attempt = 0; Spi.ExecuteScalar<bool>(
                "SELECT pg_stat_clear_snapshot() IS NULL AND EXISTS (SELECT FROM pg_stat_activity WHERE pid = $1)", SpiParameter.Create(pid.Value)); attempt++)
            {
                if (attempt == 300)
                {
                    throw new PgException(PgSqlStates.ObjectInUse, $"The worker for subscription \"{name}\" did not stop.");
                }

                Spi.Execute("SELECT pg_sleep(0.1)");
            }
        }

        // The worker may have stopped before creating the slot.
        using (PgReplicationConnection connection = PgReplicationConnection.Connect(provider, "ankus_subscriber_drop"))
        {
            ApplyWorker.DropSlot(connection, slot);
        }

        PgReplicationOrigin.Drop(ApplyWorker.OriginName(name), missingOk: true);
        Spi.Execute($"DELETE FROM {Spi.QuoteQualifiedIdentifier(Schema(), "subscription")} WHERE name = $1", SpiParameter.Create(name));
    }

    /// <summary>
    /// Runs a subscription's worker. The worker waits for the transaction that created the subscription to commit,
    /// then records its process ID and applies the subscription until PostgreSQL asks it to stop.
    /// </summary>
    /// <param name="argument">The database OID in the high 32 bits and the user OID in the low 32 bits.</param>
    [PgBackgroundWorker]
    public static void Apply(nuint argument)
    {
        PgBackgroundWorker.Connect(unchecked((uint)((ulong)argument >> 32)), unchecked((uint)argument));
        string name = PgBackgroundWorker.Extra;
        for (int attempt = 0; ; attempt++)
        {
            (string? provider, string? publication, string? slot) = PgBackgroundWorker.RunTransaction(() =>
                Spi.ExecuteScalars<string?, string?, string?>(
                    $"UPDATE {Spi.QuoteQualifiedIdentifier(Schema(), "subscription")} SET pid = pg_backend_pid() WHERE name = $1 RETURNING provider, publication, slot",
                    SpiParameter.Create(name)));
            if (provider is not null && publication is not null && slot is not null)
            {
                try
                {
                    new ApplyWorker(name, provider, publication, slot).Run();
                }
                catch (OperationCanceledException) when (!PgBackgroundWorker.CanContinue)
                {
                    // A stop request rolled back the remote transaction in progress; it is applied again on restart.
                }

                return;
            }

            if (attempt == 100 || !PgBackgroundWorker.Wait(TimeSpan.FromMilliseconds(100)))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Finds the extension's schema, which holds the subscription table, so the worker finds it whatever its search
    /// path.
    /// </summary>
    private static string Schema()
        => Spi.ExecuteScalar<string>(
            "SELECT n.nspname::text FROM pg_extension e JOIN pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = 'ankus_subscriber'");
}
