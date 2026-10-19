using System.Globalization;

namespace Ankus;

/// <summary>
/// Represents PostgreSQL's 32-bit transaction ID type, <c>xid</c>.
/// </summary>
/// <param name="Value">The raw transaction ID.</param>
public readonly record struct PgTransactionId(uint Value)
{
    /// <summary>
    /// Gets PostgreSQL's invalid transaction ID.
    /// </summary>
    public static PgTransactionId Invalid => new(0);

    /// <summary>
    /// Gets PostgreSQL's bootstrap transaction ID.
    /// </summary>
    public static PgTransactionId Bootstrap => new(1);

    /// <summary>
    /// Gets PostgreSQL's frozen transaction ID.
    /// </summary>
    public static PgTransactionId Frozen => new(2);

    /// <summary>
    /// Gets the first ordinary transaction ID.
    /// </summary>
    public static PgTransactionId FirstNormal => new(3);

    /// <summary>
    /// Gets whether the transaction ID is valid.
    /// </summary>
    public bool IsValid => Value != Invalid.Value;

    /// <summary>
    /// Gets whether this is an ordinary transaction ID; PostgreSQL's special values lie below FirstNormal.
    /// </summary>
    public bool IsNormal => Value >= FirstNormal.Value;

    /// <summary>
    /// Expands this 32-bit transaction ID with the epoch nearest PostgreSQL's next transaction ID.
    /// </summary>
    /// <returns>The 64-bit transaction ID. Special transaction IDs are returned unchanged.</returns>
    public ulong ToFullTransactionId() => Expand(this, NativeBackend.ReadNextFullTransactionId());

    /// <summary>
    /// Formats the raw transaction ID as an unsigned decimal number.
    /// </summary>
    /// <returns>The PostgreSQL transaction ID text.</returns>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Applies PostgreSQL's wrap-aware epoch selection to a transaction ID.
    /// </summary>
    /// <param name="transactionId">The 32-bit transaction ID.</param>
    /// <param name="nextFullTransactionId">PostgreSQL's next full transaction ID.</param>
    /// <returns>The nearest full transaction ID.</returns>
    internal static ulong Expand(PgTransactionId transactionId, ulong nextFullTransactionId)
    {
        if (!transactionId.IsNormal)
        {
            return transactionId.Value;
        }

        uint next = (uint)nextFullTransactionId;
        ulong epoch = nextFullTransactionId >> 32;
        if (transactionId.Value > next && Precedes(transactionId.Value, next))
        {
            epoch--;
        }
        else if (transactionId.Value < next && Follows(transactionId.Value, next))
        {
            epoch++;
        }

        return (epoch << 32) | transactionId.Value;
    }

    private static bool Precedes(uint left, uint right) => unchecked((int)(left - right)) < 0;

    private static bool Follows(uint left, uint right) => unchecked((int)(left - right)) > 0;
}
