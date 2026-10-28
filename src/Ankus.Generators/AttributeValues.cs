using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reads named compile-time attribute constants without constructing attribute instances.
/// </summary>
internal static class AttributeValues
{
    /// <summary>
    /// Gets a named scalar option, falling back when it is absent or null.
    /// </summary>
    /// <typeparam name="T">The scalar constant type.</typeparam>
    /// <param name="attribute">The semantic attribute.</param>
    /// <param name="name">The property name.</param>
    /// <param name="fallback">The default value.</param>
    /// <returns>The constant or its default.</returns>
    internal static T Get<T>(AttributeData attribute, string name, T fallback)
    {
        foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Kind != TypedConstantKind.Array && argument.Value.Value is T value)
            {
                return value;
            }
        }

        return fallback;
    }
}
