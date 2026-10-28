using System;

namespace Ankus;

/// <summary>
/// Owns the table identity and reason bits reported during a table_rewrite event trigger.
/// </summary>
public sealed class PgTableRewrite
{
    /// <summary>
    /// Copies the guarded rewrite metadata projection while retaining combined and future reason bits.
    /// </summary>
    internal PgTableRewrite(SpiRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        TableOid = row.Get<uint>(0);
        int reason = row.Get<int>(1);
        if (TableOid == 0 || reason < 0)
        {
            throw new InvalidOperationException("Table rewrite metadata requires a valid relation OID and nonnegative reason bits.");
        }

        Reason = (PgTableRewriteReason)reason;
    }

    /// <summary>
    /// Gets the catalog OID of the relation about to be rewritten.
    /// </summary>
    public uint TableOid { get; }

    /// <summary>
    /// Gets the combined PostgreSQL reason flags, including unrecognized nonnegative bits from later server versions.
    /// </summary>
    public PgTableRewriteReason Reason { get; }
}
