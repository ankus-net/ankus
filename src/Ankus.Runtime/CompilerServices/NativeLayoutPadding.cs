namespace Ankus.CompilerServices;

/// <summary>
/// Sets a native layout's padding bytes to zero, so stored values do not depend on whatever a struct copy left there.
/// </summary>
/// <remarks>
/// .NET does not preserve padding through struct copies; a promoted struct can be written field by field. The bytes are
/// therefore cleared in the destination after each write.
/// </remarks>
internal static class NativeLayoutPadding
{
    /// <summary>
    /// Requires generated padding pairs to lie within the payload.
    /// </summary>
    /// <param name="padding">Offset and length pairs.</param>
    /// <param name="size">The payload size.</param>
    /// <returns>The validated pairs.</returns>
    internal static int[] Validate(int[] padding, int size)
    {
        ArgumentNullException.ThrowIfNull(padding);
        if (padding.Length % 2 != 0)
        {
            throw new ArgumentException("Native layout padding must be offset and length pairs.", nameof(padding));
        }

        for (int index = 0; index < padding.Length; index += 2)
        {
            if (padding[index] < 0 || padding[index + 1] <= 0 || padding[index] > size - padding[index + 1])
            {
                throw new ArgumentOutOfRangeException(nameof(padding), "Native layout padding lies outside the payload.");
            }
        }

        return padding;
    }

    /// <summary>
    /// Clears every padding range in a written payload.
    /// </summary>
    /// <param name="payload">The complete payload.</param>
    /// <param name="padding">Validated offset and length pairs.</param>
    internal static void Clear(Span<byte> payload, int[] padding)
    {
        for (int index = 0; index < padding.Length; index += 2)
        {
            payload.Slice(padding[index], padding[index + 1]).Clear();
        }
    }
}
