using System;

namespace Ankus;

/// <summary>
/// Configures the native block sizes of a PostgreSQL AllocSet memory context.
/// </summary>
/// <param name="minimumContextSize">The retained first-block size, or zero to use the initial block size.</param>
/// <param name="initialBlockSize">The first ordinary allocation block size.</param>
/// <param name="maximumBlockSize">The largest ordinary allocation block size.</param>
/// <remarks>
/// Sizes are validated when creating a context. Nonzero sizes must satisfy the target PostgreSQL
/// server's alignment requirements; the native bridge also validates version-specific size limits.
/// Blocks need not be powers of two, and the minimum context size may exceed the initial block size.
/// </remarks>
public sealed class PgMemoryContextOptions(
    nuint minimumContextSize = 0,
    nuint initialBlockSize = 8192,
    nuint maximumBlockSize = 8388608)
{
    /// <summary>
    /// Gets PostgreSQL's default AllocSet sizing preset.
    /// </summary>
    public static PgMemoryContextOptions Default { get; } = new();

    /// <summary>
    /// Gets the preset for contexts expected to remain small.
    /// </summary>
    public static PgMemoryContextOptions Small { get; } = new(0, 1024, 8192);

    /// <summary>
    /// Gets the preset for contexts that begin small and can grow to the default maximum.
    /// </summary>
    public static PgMemoryContextOptions StartSmall { get; } = new(0, 1024, 8388608);

    /// <summary>
    /// Gets the retained first-block size, or zero to use the initial block size.
    /// </summary>
    public nuint MinimumContextSize { get; } = minimumContextSize;

    /// <summary>
    /// Gets the first ordinary allocation block size.
    /// </summary>
    public nuint InitialBlockSize { get; } = initialBlockSize;

    /// <summary>
    /// Gets the largest ordinary allocation block size.
    /// </summary>
    public nuint MaximumBlockSize { get; } = maximumBlockSize;

    /// <summary>
    /// Rejects portable size violations before entering the native allocator.
    /// </summary>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(InitialBlockSize, (nuint)1024, nameof(InitialBlockSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumBlockSize, InitialBlockSize, nameof(MaximumBlockSize));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumBlockSize, nuint.MaxValue / 2, nameof(MaximumBlockSize));
        if (MinimumContextSize != 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(MinimumContextSize, (nuint)1024, nameof(MinimumContextSize));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(MinimumContextSize, MaximumBlockSize, nameof(MinimumContextSize));
        }
    }
}
