namespace Ankus;

/// <summary>
/// Retains regclass identity in detached results without acquiring a relation reference before a typed read.
/// </summary>
/// <param name="Oid">The exact regclass datum, including zero or an OID whose relation no longer exists.</param>
internal readonly record struct PgRelationIdentity(uint Oid)
{
    /// <summary>
    /// Copies live relation identities into detached scalar or array cells without retaining their close obligations.
    /// Other supported cell values preserve their existing storage semantics.
    /// </summary>
    internal static object? Snapshot(object? value) => value switch
    {
        // The common cells are recognized with one check.
        null or string => value,
        PgRelation relation => new PgRelationIdentity(relation.Oid),
        PgRelation?[] vector => new PgArray<PgRelationIdentity?>(vector.Select(Identify)),
        PgArray<PgRelation?> array => new PgArray<PgRelationIdentity?>(
            array.Select(Identify).ToArray(), array.Lengths, array.LowerBounds),
        _ => value,
    };

    /// <summary>
    /// Preserves SQL NULL while copying a present relation's checked OID.
    /// </summary>
    private static PgRelationIdentity? Identify(PgRelation? relation) => relation is null ? null : new(relation.Oid);
}
