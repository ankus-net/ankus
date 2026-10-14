namespace Ankus.TestExtension;

/// <summary>
/// Exercises ordinary aggregate states and nested function-schema restoration.
/// </summary>
public static class AggregateStateFunctions
{
    private static uint s_nestedEnumOid;
    private static bool s_failNested;

    /// <summary>
    /// Retains the first nonnull enum through an aggregate without a final function.
    /// </summary>
    [PgAggregate(Name = "state_first_mood")]
    public sealed class FirstMood : IPgAggregate<EnumMood?, EnumMood?>
    {
        /// <summary>
        /// Preserves the managed enum identity independently of its numeric ordering.
        /// </summary>
        public static EnumMood? Transition(PgAggregateContext context, EnumMood? state, EnumMood? value) => state ?? value;
    }

    /// <summary>
    /// Retains the first nonnull shaped array through subsequent callback allocations.
    /// </summary>
    [PgAggregate(Name = "state_first_array")]
    public sealed class FirstArray : IPgAggregate<PgArray<int?>?, PgArray<int?>?>
    {
        /// <summary>
        /// Returns the previously owned shape and cells after collecting temporary managed objects.
        /// </summary>
        public static PgArray<int?>? Transition(PgAggregateContext context, PgArray<int?>? state, PgArray<int?>? value)
        {
            GarbageCollection.Collect();
            return state ?? value;
        }
    }

    /// <summary>
    /// Keeps named composite state without losing row identity or nullable fields.
    /// </summary>
    [PgAggregate(Name = "state_first_dog", Requires = ["composite-types"])]
    public sealed class FirstDog : IPgAggregate<PgHeapTuple?, PgHeapTuple?>
    {
        /// <summary>
        /// Distinguishes a SQL NULL row from a nonnull row containing NULL cells.
        /// </summary>
        [return: PgCompositeType("dog", Schema = "tuple_values")]
        public static PgHeapTuple? Transition(
            PgAggregateContext context,
            [PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple? state,
            [PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple? value)
        {
            GarbageCollection.Collect();
            return state ?? value;
        }
    }

    /// <summary>
    /// Keeps a domain over a named composite and rechecks its constraints on returned state.
    /// </summary>
    [PgAggregate(Name = "state_first_dog_domain", Requires = ["composite-types"])]
    public sealed class FirstDogDomain : IPgAggregate<PgHeapTuple?, PgHeapTuple?>
    {
        /// <summary>
        /// Preserves domain identity or deliberately violates its age constraint on a sentinel input.
        /// </summary>
        [return: PgCompositeType("dog_domain", Schema = "tuple_values")]
        public static PgHeapTuple? Transition(
            PgAggregateContext context,
            [PgCompositeType("dog_domain", Schema = "tuple_values")] PgHeapTuple? state,
            [PgCompositeType("dog_domain", Schema = "tuple_values")] PgHeapTuple? value)
        {
            state ??= value;
            if (value?.Get<int?>("age") == 13)
            {
                state!.Set("age", -1);
            }

            return state;
        }
    }

    /// <summary>
    /// Orders named composite inputs including SQL NULL through explicitly typed native operands.
    /// </summary>
    [PgAggregate(Name = "state_ordered_dogs", Kind = PgAggregateKind.OrderedSet,
        FinalModify = PgAggregateFinalModify.ReadOnly, Requires = ["composite-types"])]
    public sealed class OrderedDogs : IPgAggregate<PgAggregateState<List<PgHeapTuple?>>?, PgHeapTuple?>,
        IPgFinalizingAggregate<PgAggregateState<List<PgHeapTuple?>>?, ValueTuple, string[]>
    {
        /// <summary>
        /// Retains each owned tuple, including an actual SQL NULL row.
        /// </summary>
        public static PgAggregateState<List<PgHeapTuple?>> Transition(PgAggregateContext context, PgAggregateState<List<PgHeapTuple?>>? state,
            [PgCompositeType("dog", Schema = "tuple_values")] PgHeapTuple? value)
        {
            state ??= new([]);
            state.Value.Add(value);
            return state;
        }

        /// <summary>
        /// Uses the named descriptor for every operand so SQL NULL does not become anonymous record.
        /// </summary>
        public static string[] Final(PgAggregateContext context, PgAggregateState<List<PgHeapTuple?>>? state, ValueTuple arguments)
        {
            if (state is null)
            {
                return [];
            }

            PgTupleDescriptor descriptor = PgTupleDescriptor.Load("tuple_values.dog");
            PgHeapTuple?[] values = [.. state.Value];
            Array.Sort(values, (left, right) => context.Compare(SpiParameter.Create(left, descriptor), SpiParameter.Create(right, descriptor)));
            return [.. values.Select(static value => value is null ? "NULL" :
                value.Get<string?>("name") + ":" + (value.Get<int?>("age")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"))];
        }
    }

    /// <summary>
    /// Observes the native support function's schema while resolving an unqualified enum.
    /// </summary>
    [PgAggregate(Name = "state_enum_scope", InitialCondition = "0")]
    public sealed class EnumScope : IPgAggregate<int, int>
    {
        /// <summary>
        /// Resolves the current enum OID before optionally raising an owned diagnostic.
        /// </summary>
        public static int Transition(PgAggregateContext context, int state, int value)
        {
            s_nestedEnumOid = PgEnums.GetTypeOid<EnumMood>();
            if (s_failNested)
            {
                throw new PgException("P7821", "Nested aggregate scope failure.");
            }

            return checked(state + value);
        }
    }

    /// <summary>
    /// Records distinct outer and aggregate schema identities through success and guarded error.
    /// </summary>
    /// <param name="fail">Whether the aggregate callback raises an error.</param>
    /// <returns>The before, nested, after identities and callback outcome.</returns>
    [PgFunction]
    public static string AggregateScopeWitness(bool fail)
    {
        s_nestedEnumOid = 0;
        s_failNested = fail;
        uint before = PgEnums.GetTypeOid<EnumMood>();
        string outcome;
        try
        {
            int result = Spi.ExecuteScalar<int>("SELECT aggregate_scope.probe(v) FROM (VALUES(1),(2)) AS input(v)");
            outcome = result.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (PgException exception)
        {
            outcome = exception.SqlState;
        }

        uint after = PgEnums.GetTypeOid<EnumMood>();
        return $"{before}:{s_nestedEnumOid}:{after}:{outcome}";
    }
}
