using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Adapts detached attribution to existing graph validators for one transient composition.
/// </summary>
/// <param name="sources">The immutable source attribution map.</param>
internal sealed class GeneratorSourceResolver(GeneratorSourceMap sources)
{
    private readonly Dictionary<GeneratorLocation, Location> _locations = [];
    private readonly Dictionary<Location, GeneratorLocation> _coordinates = new(LocationIdentityComparer.Instance);
    private readonly Dictionary<GeneratorLocation, GeneratorSourceMap.Entry> _entries = sources.Entries.ToDictionary(static entry => entry.Coordinates);

    /// <summary>
    /// Creates tree-free line attribution and retains the declaration anchor for exact diagnostic transport.
    /// </summary>
    /// <param name="coordinates">The distinct tree occurrence, declaration and relative span.</param>
    /// <returns>The transient location with physical source lines.</returns>
    internal Location Resolve(GeneratorLocation coordinates)
    {
        if (!_locations.TryGetValue(coordinates, out Location? location))
        {
            GeneratorSourceMap.Entry entry = _entries[coordinates];
            location = Location.Create(entry.Physical.Path, coordinates.Span, entry.Physical.Span);
            _locations.Add(coordinates, location);
            _coordinates.Add(location, coordinates);
        }

        return location;
    }

    /// <summary>
    /// Preserves distinct source trees even when their file paths and spans are identical.
    /// </summary>
    /// <param name="location">The transient graph location, or no location.</param>
    /// <returns>The exact original diagnostic coordinates.</returns>
    internal GeneratorLocation? Coordinates(Location? location)
        => location is null || location == Location.None ? null : _coordinates[location];

    /// <summary>
    /// Uses transient object identity so Roslyn's external-file equality cannot merge source trees.
    /// </summary>
    private sealed class LocationIdentityComparer : IEqualityComparer<Location>
    {
        /// <summary>
        /// Gets the shared stateless reference comparer.
        /// </summary>
        internal static LocationIdentityComparer Instance { get; } = new();

        /// <inheritdoc />
        public bool Equals(Location? x, Location? y) => ReferenceEquals(x, y);

        /// <inheritdoc />
        public int GetHashCode(Location obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
