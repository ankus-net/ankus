namespace Ankus.TestExtension;

/// <summary>
/// Exposes native comparison equivalence and collation identities independently of sorted array tie ordering.
/// </summary>
[PgSchema("aggregate_values", Create = false)]
public static class AggregateCollationFunctions
{
    /// <summary>
    /// Counts values before, equivalent to, and after a direct argument under the selected native ordering.
    /// </summary>
    [PgAggregate(Name = "collation_compare", Kind = PgAggregateKind.OrderedSet, Requires = ["aggregate-support"])]
    public sealed class CollationCompare : IPgAggregate<PgAggregateState<List<string?>>?, string?>,
        IPgFinalizingAggregate<PgAggregateState<List<string?>>?, string?, long[]>
    {
        /// <summary>
        /// Keeps every owned input, including SQL NULL, for final comparison.
        /// </summary>
        public static PgAggregateState<List<string?>> Transition(PgAggregateContext context, PgAggregateState<List<string?>>? state, string? value)
        {
            state ??= new PgAggregateState<List<string?>>([]);
            state.Value.Add(value);
            return state;
        }

        /// <summary>
        /// Returns native comparison counts followed by the callback and key collation OIDs.
        /// </summary>
        public static long[] Final(PgAggregateContext context, PgAggregateState<List<string?>>? state, string? target)
        {
            long before = 0;
            long equal = 0;
            long after = 0;
            if (state is not null)
            {
                foreach (int comparison in state.Value.Select(value => context.Compare(value, target)))
                {
                    if (comparison < 0)
                    {
                        before++;
                    }
                    else if (comparison > 0)
                    {
                        after++;
                    }
                    else
                    {
                        equal++;
                    }
                }
            }

            return [before, equal, after, context.CollationOid, context.SortKeys[0].CollationOid];
        }
    }
}
