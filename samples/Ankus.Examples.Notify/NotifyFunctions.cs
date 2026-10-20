using System.Globalization;

[assembly: PgSql("create-products-trigger", """
    CREATE TABLE products (
        id    bigserial NOT NULL PRIMARY KEY,
        name  text NOT NULL
    );

    CREATE TRIGGER products_notify
        AFTER INSERT OR UPDATE OR DELETE ON products
        FOR EACH ROW EXECUTE PROCEDURE products_notify();
    """, Requires = ["products-notify"])]

namespace Ankus.Examples.Notify;

/// <summary>
/// Ports pgrx's <c>notify</c> example: SQL wrappers over <see cref="Notifications"/> and a per-row
/// cache-invalidation trigger.
/// </summary>
public static class NotifyFunctions
{
    /// <summary>
    /// The channel that announces changed product IDs.
    /// </summary>
    private const string CacheInvalidationChannel = "cache_invalidation";

    /// <summary>
    /// Queues <paramref name="payload"/> on <paramref name="channel"/>, delivered when the calling transaction commits.
    /// </summary>
    /// <param name="channel">The channel name.</param>
    /// <param name="payload">The notification payload.</param>
    [PgFunction]
    public static void AnkusNotify(string channel, string payload) => Notifications.Notify(channel, payload);

    /// <summary>
    /// Subscribes the calling session to <paramref name="channel"/>, like <c>LISTEN</c>.
    /// </summary>
    /// <param name="channel">The channel name.</param>
    [PgFunction]
    public static void AnkusListen(string channel) => Notifications.Listen(channel);

    /// <summary>
    /// Unsubscribes the calling session from <paramref name="channel"/>, like <c>UNLISTEN</c>.
    /// </summary>
    /// <param name="channel">The channel name.</param>
    [PgFunction]
    public static void AnkusUnlisten(string channel) => Notifications.Unlisten(channel);

    /// <summary>
    /// Unsubscribes the calling session from every channel, like <c>UNLISTEN *</c>.
    /// </summary>
    [PgFunction]
    public static void AnkusUnlistenAll() => Notifications.UnlistenAll();

    /// <summary>
    /// Broadcasts the ID of every inserted, updated or deleted product on the <c>cache_invalidation</c> channel.
    /// </summary>
    /// <param name="context">The <c>AFTER ... FOR EACH ROW</c> trigger context on <c>products</c>.</param>
    /// <returns>The changed row; PostgreSQL ignores the result of an AFTER trigger.</returns>
    [PgTrigger]
    [PgFunction(Id = "products-notify")]
    public static PgHeapTuple? ProductsNotify(PgTriggerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // On DELETE the changed row is OLD; otherwise it is NEW.
        PgHeapTuple? row = context.New ?? context.Old;
        if (row?.Get<long?>("id") is long id)
        {
            Notifications.Notify(CacheInvalidationChannel, id.ToString(CultureInfo.InvariantCulture));
        }

        return row;
    }
}
