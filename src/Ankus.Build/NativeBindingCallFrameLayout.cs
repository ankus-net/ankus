namespace Ankus.Build;

/// <summary>
/// Places argument descriptors and native object bytes in one correctly aligned, bounded allocation.
/// </summary>
internal static class NativeBindingCallFrameLayout
{
    /// <summary>
    /// Computes exact native argument alignment without assuming CLR stack alignment or narrowing frame lengths.
    /// </summary>
    /// <param name="graph">The validated target and native object representations.</param>
    /// <param name="call">The resolved fixed call.</param>
    /// <returns>Offsets and an allocation length including the leading alignment allowance.</returns>
    internal static NativeBindingCallFrame Create(NativeRecordGraph graph, NativeBindingCall call)
        => Create(graph, [.. call.Parameters.Select(static parameter => parameter.StorageType)], call.Result?.StorageType);

    /// <summary>
    /// Computes a shared frame from exact storage identities independently of how its native target is selected.
    /// </summary>
    /// <param name="graph">The validated native storage graph.</param>
    /// <param name="parameters">Ordered native argument storage identities.</param>
    /// <param name="result">The native result storage identity, or null for a void call.</param>
    /// <returns>Offsets and a bounded allocation length preserving every argument's alignment.</returns>
    internal static NativeBindingCallFrame Create(NativeRecordGraph graph, IReadOnlyList<int> parameters, int? result)
    {
        try
        {
            long position = checked((long)parameters.Count * graph.Target.PointerSize * 2);
            long alignment = graph.Target.PointerSize;
            var offsets = new List<long>(parameters.Count);
            foreach (NativeRecordType storage in parameters.Select(parameter => graph.Types[parameter]))
            {
                long required = storage.Alignment!.Value;
                alignment = Math.Max(alignment, required);
                position = checked(position + (required - 1)) & -required;
                offsets.Add(position);
                position = checked(position + Math.Max(1, storage.Size!.Value));
            }

            long resultOffset = position;
            if (result is int resultType)
            {
                // Native bodies memcpy results; the receiving bytes need no native result alignment.
                position = checked(position + Math.Max(1, graph.Types[resultType].Size!.Value));
            }

            long length = position == 0 ? 0 : checked(position + (alignment - 1));
            if (graph.Target.PointerSize == 4 && length > uint.MaxValue)
            {
                throw new FormatException("Native call storage exceeds the target address space.");
            }

            return new(offsets, resultOffset, alignment, length);
        }
        catch (OverflowException error)
        {
            throw new FormatException("Native call storage exceeds the supported address space.", error);
        }
    }
}

/// <summary>
/// Describes one complete native call frame independently of its eventual stack or heap address.
/// </summary>
/// <param name="Arguments">Byte offsets of aligned native argument objects after their descriptors.</param>
/// <param name="Result">The exact result byte destination offset.</param>
/// <param name="Alignment">The alignment required by every object in the allocation.</param>
/// <param name="AllocationSize">The total native allocation size including alignment padding.</param>
internal sealed record NativeBindingCallFrame(IReadOnlyList<long> Arguments, long Result, long Alignment, long AllocationSize);
