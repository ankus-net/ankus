using System.Numerics;

namespace Ankus;

/// <summary>
/// Supplies wrapping arithmetic and atomic bitwise operations for PostgreSQL shared scalars.
/// </summary>
public static class PgAtomic
{
    /// <summary>
    /// Adds an integer with wrapping overflow and returns the updated value.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <param name="value">The amount to add.</param>
    /// <returns>The sum stored by this operation.</returns>
    public static T Add<T>(this PgAtomic<T> storage, T value) where T : unmanaged, IBinaryInteger<T>
        => Apply(storage, value, static (current, operand) => unchecked(current + operand), returnNew: true);

    /// <summary>
    /// Subtracts an integer with wrapping overflow and returns the updated value.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <param name="value">The amount to subtract.</param>
    /// <returns>The difference stored by this operation.</returns>
    public static T Subtract<T>(this PgAtomic<T> storage, T value) where T : unmanaged, IBinaryInteger<T>
        => Apply(storage, value, static (current, operand) => unchecked(current - operand), returnNew: true);

    /// <summary>
    /// Adds one with wrapping overflow and returns the updated value.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <returns>The incremented value.</returns>
    public static T Increment<T>(this PgAtomic<T> storage) where T : unmanaged, IBinaryInteger<T>
        => storage.Add(T.One);

    /// <summary>
    /// Subtracts one with wrapping overflow and returns the updated value.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <returns>The decremented value.</returns>
    public static T Decrement<T>(this PgAtomic<T> storage) where T : unmanaged, IBinaryInteger<T>
        => storage.Subtract(T.One);

    /// <summary>
    /// Stores the bitwise AND and returns the previous value, following Interlocked.And.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <param name="value">The bit mask.</param>
    /// <returns>The value before the update.</returns>
    public static T And<T>(this PgAtomic<T> storage, T value) where T : unmanaged, IBinaryInteger<T>
        => Apply(storage, value, static (current, operand) => current & operand, returnNew: false);

    /// <summary>
    /// Stores the bitwise OR and returns the previous value, following Interlocked.Or.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <param name="value">The bit mask.</param>
    /// <returns>The value before the update.</returns>
    public static T Or<T>(this PgAtomic<T> storage, T value) where T : unmanaged, IBinaryInteger<T>
        => Apply(storage, value, static (current, operand) => current | operand, returnNew: false);

    /// <summary>
    /// Stores the bitwise exclusive OR and returns the previous value.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <param name="value">The bit mask.</param>
    /// <returns>The value before the update.</returns>
    public static T Xor<T>(this PgAtomic<T> storage, T value) where T : unmanaged, IBinaryInteger<T>
        => Apply(storage, value, static (current, operand) => current ^ operand, returnNew: false);

    /// <summary>
    /// Stores the larger of the current value and an operand and returns the previous value.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <param name="value">The other operand.</param>
    /// <returns>The value before the update.</returns>
    public static T Max<T>(this PgAtomic<T> storage, T value) where T : unmanaged, IBinaryInteger<T>
        => Apply(storage, value, static (current, operand) => T.Max(current, operand), returnNew: false);

    /// <summary>
    /// Stores the smaller of the current value and an operand and returns the previous value.
    /// </summary>
    /// <typeparam name="T">A supported unmanaged binary integer.</typeparam>
    /// <param name="storage">The registered scalar.</param>
    /// <param name="value">The other operand.</param>
    /// <returns>The value before the update.</returns>
    public static T Min<T>(this PgAtomic<T> storage, T value) where T : unmanaged, IBinaryInteger<T>
        => Apply(storage, value, static (current, operand) => T.Min(current, operand), returnNew: false);

    /// <summary>
    /// Stores the Boolean conjunction and returns the previous value.
    /// </summary>
    /// <param name="storage">The registered Boolean scalar.</param>
    /// <param name="value">The other operand.</param>
    /// <returns>The value before the update.</returns>
    public static bool And(this PgAtomic<bool> storage, bool value)
        => Apply(storage, value, static (current, operand) => current & operand, returnNew: false);

    /// <summary>
    /// Stores the Boolean disjunction and returns the previous value.
    /// </summary>
    /// <param name="storage">The registered Boolean scalar.</param>
    /// <param name="value">The other operand.</param>
    /// <returns>The value before the update.</returns>
    public static bool Or(this PgAtomic<bool> storage, bool value)
        => Apply(storage, value, static (current, operand) => current | operand, returnNew: false);

    /// <summary>
    /// Stores the Boolean exclusive OR and returns the previous value.
    /// </summary>
    /// <param name="storage">The registered Boolean scalar.</param>
    /// <param name="value">The other operand.</param>
    /// <returns>The value before the update.</returns>
    public static bool Xor(this PgAtomic<bool> storage, bool value)
        => Apply(storage, value, static (current, operand) => current ^ operand, returnNew: false);

    private static T Apply<T>(PgAtomic<T> storage, T value, Func<T, T, T> operation, bool returnNew) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(storage);
        while (true)
        {
            using NativeSharedMemoryAccessLease lease = storage.Open();
            ref T target = ref lease.GetReference<T>();
            T current = Interlocked.CompareExchange(ref target, default, default);
            T updated = operation(current, value);
            T observed = Interlocked.CompareExchange(ref target, updated, current);
            if (EqualityComparer<T>.Default.Equals(current, observed))
            {
                return returnNew ? updated : current;
            }
        }
    }
}
