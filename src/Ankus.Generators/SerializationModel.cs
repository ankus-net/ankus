using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Retains a closed, comparable serialization graph whose nodes refer to each other by index, holding no compiler
/// symbols or recursive objects.
/// </summary>
/// <param name="Nodes">The validated contracts in deterministic reader and writer order.</param>
internal sealed record SerializationModel(EquatableArray<SerializationModel.Node> Nodes)
{
    /// <summary>
    /// Renders a statically bound codec from the current immutable contract graph.
    /// </summary>
    /// <param name="name">The exact generated codec class identifier.</param>
    /// <param name="source">The destination managed dispatcher source.</param>
    /// <param name="textCodec">The optional statically constructed text codec name.</param>
    internal void Emit(string name, StringBuilder source, string? textCodec = null)
        => SerializationEmitter.Emit(this, name, source, textCodec);

    /// <summary>
    /// Describes one exact managed contract and its direct serialization operations.
    /// </summary>
    /// <param name="Managed">The managed spelling including nested nullability.</param>
    /// <param name="ConstructionType">The managed spelling without root reference nullability.</param>
    /// <param name="IsReferenceType">Whether a required contract needs a runtime null check.</param>
    /// <param name="CanBeNull">Whether the exact persisted contract permits serialized null.</param>
    /// <param name="Index">The stable generated reader and writer suffix.</param>
    /// <param name="Kind">The validated scalar, collection, object or polymorphic category.</param>
    /// <param name="Primitive">The primitive reader and writer operation, when applicable.</param>
    /// <param name="Element">The element or nullable underlying node index, when applicable.</param>
    /// <param name="DiscriminatorName">The exact persisted polymorphic discriminator property.</param>
    /// <param name="Variants">The closed concrete variants in declared order.</param>
    /// <param name="BaseShape">The concrete base shape used without a discriminator, when allowed.</param>
    /// <param name="Members">The directly accessible persisted members in deterministic order.</param>
    /// <param name="EnumMembers">The declared managed and persisted enum names.</param>
    /// <param name="ConstructorMembers">The member indexes in the selected constructor's parameter order.</param>
    internal sealed record Node(string Managed, string ConstructionType, bool IsReferenceType, bool CanBeNull,
        int Index, string Kind, string? Primitive, int? Element, string DiscriminatorName,
        EquatableArray<Variant> Variants, int? BaseShape, EquatableArray<Member> Members,
        EquatableArray<(string Member, string Name)> EnumMembers, EquatableArray<int> ConstructorMembers);

    /// <summary>
    /// Retains an exact persisted member without its declaring compiler symbol.
    /// </summary>
    /// <param name="Name">The directly accessible managed identifier.</param>
    /// <param name="SerializedName">The persisted map property name.</param>
    /// <param name="Value">The exact value contract node index.</param>
    /// <param name="Writable">Whether an object initializer can assign this member.</param>
    /// <param name="Required">Whether presence is required even for a nullable value contract.</param>
    internal sealed record Member(string Name, string SerializedName, int Value, bool Writable, bool Required);

    /// <summary>
    /// Retains a closed concrete variant without conflating text and numeric discriminator identities.
    /// </summary>
    /// <param name="Shape">The concrete object contract node index.</param>
    /// <param name="Text">The string discriminator, or null for a numeric discriminator.</param>
    /// <param name="Number">The numeric discriminator, or null for a string discriminator.</param>
    internal sealed record Variant(int Shape, string? Text, int? Number);
}
