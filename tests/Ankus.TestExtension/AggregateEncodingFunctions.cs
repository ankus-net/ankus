namespace Ankus.TestExtension;

/// <summary>
/// Produces owned encoded text from internal state, including a deliberate server-encoding failure at final output.
/// </summary>
[PgSchema("aggregate_values", Create = false)]
public static class AggregateEncodingFunctions
{
    /// <summary>
    /// Retains generated Unicode strings across transitions and returns them from the final callback.
    /// </summary>
    [PgAggregate(Name = "generated_text", Requires = ["aggregate-support"])]
    public sealed class GeneratedText : IPgAggregate<PgAggregateState<List<string>>?, int>,
        IPgFinalizingAggregate<PgAggregateState<List<string>>?, ValueTuple, string>
    {
        /// <summary>
        /// Creates representable or deliberately unrepresentable managed text without client-side encoding failure.
        /// </summary>
        public static PgAggregateState<List<string>> Transition(PgAggregateContext context, PgAggregateState<List<string>>? state, int value)
        {
            state ??= new PgAggregateState<List<string>>([]);
            state.Value.Add(value == 1 ? "café" : "🐘");
            GarbageCollection.Collect();
            Spi.Execute("SELECT repeat('churn',1000)");
            return state;
        }

        /// <summary>
        /// Preserves the generated text after managed collection and native SPI memory churn.
        /// </summary>
        public static string Final(PgAggregateContext context, PgAggregateState<List<string>>? state, ValueTuple arguments)
            => state is null ? "" : string.Concat(state.Value);
    }
}
