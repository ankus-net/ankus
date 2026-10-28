using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Holds one statically resolved node in a custom-type serialization graph.
/// </summary>
/// <param name="type">The exact type, including nested nullability.</param>
/// <param name="index">The stable generated method suffix.</param>
internal sealed class SerializationNode(ITypeSymbol type, int index)
{
    /// <summary>
    /// Gets the Roslyn contract.
    /// </summary>
    internal ITypeSymbol Type { get; } = type;

    /// <summary>
    /// Gets the emitted managed type name.
    /// </summary>
    internal string Managed => DefaultTypeSerializer.Display(Type);

    /// <summary>
    /// Gets the stable method suffix.
    /// </summary>
    internal int Index { get; } = index;

    /// <summary>
    /// Gets or sets the contract category.
    /// </summary>
    internal string Kind
    {
        get;
        set;
    } = "object";

    /// <summary>
    /// Gets or sets the primitive reader/writer method suffix.
    /// </summary>
    internal string? Primitive
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the element or nullable-underlying node.
    /// </summary>
    internal SerializationNode? Element
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the discriminator property for a closed polymorphic contract.
    /// </summary>
    internal string DiscriminatorName
    {
        get;
        set;
    } = "$type";

    /// <summary>
    /// Gets the explicitly registered concrete variants and their typed discriminators.
    /// </summary>
    internal List<(SerializationNode Shape, object Tag)> Variants { get; } = [];

    /// <summary>
    /// Gets or sets the concrete base shape used when no discriminator is present.
    /// </summary>
    internal SerializationNode? BaseShape
    {
        get;
        set;
    }

    /// <summary>
    /// Gets whether null is part of this exact type contract.
    /// </summary>
    internal bool CanBeNull => Kind == "nullable" || Type.IsReferenceType && Type.NullableAnnotation != NullableAnnotation.NotAnnotated;

    /// <summary>
    /// Gets the ordered serialized members.
    /// </summary>
    internal List<SerializationMember> Members { get; } = [];

    /// <summary>
    /// Gets the declared enum names and serialized names.
    /// </summary>
    internal List<(string Member, string Name)> EnumMembers { get; } = [];

    /// <summary>
    /// Gets or sets the selected object constructor.
    /// </summary>
    internal IMethodSymbol? Constructor
    {
        get;
        set;
    }

    /// <summary>
    /// Gets members in the selected constructor's parameter order.
    /// </summary>
    internal List<SerializationMember> ConstructorMembers { get; } = [];
}
