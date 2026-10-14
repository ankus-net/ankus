using System.Text.Json.Serialization;

namespace Ankus.Examples.TypeVariants;

/// <summary>
/// Variant 5: a record-shaped type returned and accepted as an array of the type.
/// </summary>
/// <param name="A">The number.</param>
/// <param name="B">The label.</param>
[PgType]
public sealed record Pair([property: JsonPropertyName("a")] int A, [property: JsonPropertyName("b")] string B);

/// <summary>
/// Array functions over the record-shaped <see cref="Pair"/> type.
/// </summary>
public static class CompositeAndArrayFunctions
{
    /// <summary>
    /// Returns two pairs as a <c>pair[]</c> value.
    /// </summary>
    /// <returns>The pairs (1, one) and (2, two).</returns>
    [PgFunction]
    public static Pair[] MakePairs() => [new(1, "one"), new(2, "two")];

    /// <summary>
    /// Sums the <see cref="Pair.A"/> members, skipping SQL NULL elements.
    /// </summary>
    /// <param name="arr">The pairs, which may contain SQL NULL elements.</param>
    /// <returns>The sum.</returns>
    [PgFunction]
    public static long SumPairA(Pair?[] arr)
    {
        long total = 0;
        foreach (Pair p in arr.OfType<Pair>())
        {
            total += p.A;
        }

        return total;
    }
}
