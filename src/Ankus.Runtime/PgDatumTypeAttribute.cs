namespace Ankus;

/// <summary>
/// Maps a managed type to an existing PostgreSQL representation through an explicit datum converter.
/// </summary>
/// <remarks>
/// This declaration generates conversion registration only. It defines no type and no input/output functions.
/// The converter implements IPgDatumReader&lt;T&gt;, IPgDatumWriter&lt;T&gt;, or both for this exact managed type.
/// A default generic declaration is a template selected by fully constructed roots in supported generated
/// signatures or exact managed PgSqlTypeProvider declarations. Each selected construction must have an
/// exact reader or writer interface on its supplied or inferred closed converter.
/// An open generic converter definition is closed at compile time from its exact reader/writer interface
/// patterns. Every parameter, including containing-type parameters, must have one unambiguous assignment
/// satisfying the C# constraints. No runtime reflection or open-ended registration is generated.
/// An explicit managed-type declaration selects one closed construction and registers a local root even
/// without a generated signature. It takes precedence over the optional default declaration on that type.
/// Exact declarations can assign distinct SQL identities and converters to different closed constructions.
/// Supported paths are scalar and array generated callbacks, set/TABLE and aggregate slots, declared SPI/function
/// parameters, typed SPI scalar and catalog/native-address function results, SpiRow and PgHeapTuple cells read or
/// written with the mapped type, PgDatum.Create, and explicit PgDatum.Read&lt;T&gt; calls.
/// One array layer uses the scalar converter with exact element and array identity, preserving NULL and shape.
/// Value-type mappings can add PgRangeType to compose the same converter into finite PgRange&lt;T&gt; bounds
/// and arrays of those ranges. Range and scalar SQL identities and ownership remain independent.
/// Readable mappings may declare PgEquality, PgOrdering and PgHashing; generated helpers require no writer.
/// Native-address calls retain the caller's responsibility for the actual result type and representation.
/// A row or tuple cell must have exactly the mapped PostgreSQL type; the indexer and other types do not read it.
/// A reader must return independent managed data; retaining a checked PgDatum does not detach its storage.
/// </remarks>
/// <param name="name">The exact unquoted PostgreSQL type identifier.</param>
/// <param name="converter">The closed converter or inferable generic definition with an accessible parameterless constructor.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = true, Inherited = false)]
public sealed class PgDatumTypeAttribute(string name, Type converter) : Attribute
{
    /// <summary>
    /// Maps one exact closed construction of the annotated type independently of its other constructions.
    /// </summary>
    /// <param name="managedType">The closed managed type whose definition carries this attribute.</param>
    /// <param name="name">The exact unquoted PostgreSQL type identifier.</param>
    /// <param name="converter">The closed converter or inferable generic definition with an accessible parameterless constructor.</param>
    public PgDatumTypeAttribute(Type managedType, string name, Type converter) : this(name, converter) => ManagedType = managedType;

    /// <summary>
    /// Gets the exact closed managed identity, or null for the annotated type's default mapping.
    /// </summary>
    public Type? ManagedType { get; }

    /// <summary>
    /// Gets the exact catalog type identifier.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the supplied reader or writer type, whose generic arguments may be inferred at compile time.
    /// </summary>
    public Type Converter { get; } = converter;

    /// <summary>
    /// Gets or sets the fixed schema. External mappings require an explicit schema.
    /// </summary>
    public string? Schema
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether the SQL type is supplied by this extension or an existing external schema.
    /// </summary>
    public PgTypeOrigin Origin
    {
        get;
        set;
    }
}
