namespace Ankus;

/// <summary>
/// Returns a value as the generic type a caller has already matched it to.
/// </summary>
internal static class GenericValue
{
    /// <summary>
    /// Converts a value through a box to <typeparamref name="T"/>, which is its own type or the nullable form of it.
    /// </summary>
    /// <typeparam name="T">The type the caller matched.</typeparam>
    /// <param name="value">The value, or null for a reference or nullable type.</param>
    /// <returns>The same value as <typeparamref name="T"/>.</returns>
    internal static T As<T>(object? value) => (T)value!;
}
