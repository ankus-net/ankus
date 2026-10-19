namespace Ankus.IntegrationTests;

public sealed partial class AggregateTests
{
    /// <summary>
    /// PostgreSQL invokes or skips nullable rows according to the typed interface; implementation annotations have no
    /// effect.
    /// </summary>
    /// <param name="name">The published aggregate name.</param>
    /// <param name="strict">The expected native transition strictness.</param>
    /// <param name="mixed">The exact result of a NULL row followed by a present row.</param>
    /// <param name="nullOnly">The exact result of a single SQL NULL input.</param>
    /// <param name="emptyNull">Whether the empty input group has a NULL state.</param>
    [TestMethod]
    [DataRow("nullable_contract", false, "<NULL>x", "<NULL>", true)]
    [DataRow("required_contract", true, "x", "", false)]
    [DataRow("annotated_contract", false, "101", "100", false)]
    public Task TypedAggregateSafeNullabilityDifferencesPreserveBackendContract(string name, bool strict,
        string mixed, string nullOnly, bool emptyNull)
        => Run(nameof(TypedAggregateSafeNullabilityDifferencesPreserveBackendContract), async (connection, transaction, token) =>
        {
            Assert.AreEqual(strict, await Scalar<bool>(connection, transaction, $$"""
                SELECT proisstrict FROM pg_proc
                WHERE oid = (SELECT aggtransfn FROM pg_aggregate
                    WHERE aggfnoid = 'aggregate_values.{{name}}(text)'::regprocedure)
                """, token));
            Assert.AreEqual(mixed, await Scalar<string>(connection, transaction, $$"""
                SELECT aggregate_values.{{name}}(value ORDER BY ordinal)::text
                FROM (VALUES (1, NULL::text), (2, 'x')) AS input(ordinal, value)
                """, token));
            Assert.AreEqual(nullOnly, await Scalar<string>(connection, transaction,
                $"SELECT aggregate_values.{name}(NULL::text)::text", token));
            Assert.AreEqual(emptyNull, await Scalar<bool>(connection, transaction,
                $"SELECT aggregate_values.{name}(NULL::text) IS NULL WHERE false", token));
            Assert.AreEqual(42, await Scalar<int>(connection, transaction, "SELECT 42", token));
        });
}
