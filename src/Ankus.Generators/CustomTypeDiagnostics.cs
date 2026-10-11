using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies one independently correctable custom-type declaration or storage contract.
/// </summary>
internal enum CustomTypeDiagnosticKind
{
    /// <summary>
    /// An undefined PostgreSQL alignment value.
    /// </summary>
    Alignment,
    /// <summary>
    /// A codec option that does not name a closed type.
    /// </summary>
    CodecType,
    /// <summary>
    /// Conflicting generated and explicit storage codecs.
    /// </summary>
    ConflictingCodecs,
    /// <summary>
    /// An invalid native-layout codec selection.
    /// </summary>
    NativeLayoutCodec,
    /// <summary>
    /// An invalid NULL-input error message.
    /// </summary>
    NullInputMessage,
    /// <summary>
    /// A ref-like custom type.
    /// </summary>
    RefLikeType,
    /// <summary>
    /// A static custom type.
    /// </summary>
    StaticType,
    /// <summary>
    /// An abstract custom type without concrete variants.
    /// </summary>
    AbstractType,
    /// <summary>
    /// An abstract custom type using an explicit storage codec.
    /// </summary>
    AbstractCodecType,
    /// <summary>
    /// An open or nested-generic custom type.
    /// </summary>
    GenericType,
    /// <summary>
    /// A custom type inaccessible to generated code.
    /// </summary>
    InaccessibleType,
    /// <summary>
    /// A declaration carrying both PgType and PgEnum.
    /// </summary>
    ConflictingTypeKinds,
    /// <summary>
    /// An inaccessible or unconstructible codec.
    /// </summary>
    CodecConstruction,
    /// <summary>
    /// A codec for a different managed type.
    /// </summary>
    CodecContract,
    /// <summary>
    /// A codec constructor that does not initialize required members.
    /// </summary>
    CodecRequiredMembers,
    /// <summary>
    /// An inherited schema with a null identifier.
    /// </summary>
    NullInheritedSchema,
    /// <summary>
    /// An invalid PostgreSQL type identifier.
    /// </summary>
    TypeName,
    /// <summary>
    /// An invalid PostgreSQL schema identifier.
    /// </summary>
    SchemaName,
    /// <summary>
    /// A native-layout root that is not a struct.
    /// </summary>
    NativeRoot,
    /// <summary>
    /// An unsupported native-layout field representation.
    /// </summary>
    NativeField,
    /// <summary>
    /// An invalid native struct layout declaration.
    /// </summary>
    NativeStructLayout,
    /// <summary>
    /// A recursive native value layout.
    /// </summary>
    NativeRecursive,
    /// <summary>
    /// An unsupported native fixed buffer.
    /// </summary>
    NativeFixedBuffer,
    /// <summary>
    /// An empty native-layout struct.
    /// </summary>
    NativeEmpty,
    /// <summary>
    /// A native layout whose packed size overflows.
    /// </summary>
    NativeTooLarge,
    /// <summary>
    /// A polymorphic discriminator colliding with stored data.
    /// </summary>
    DiscriminatorCollision,
    /// <summary>
    /// Invalid Unicode in serialization metadata.
    /// </summary>
    SerializationUnicode,
    /// <summary>
    /// A serialization graph exceeding its finite contract limit.
    /// </summary>
    SerializationGraphLimit,
    /// <summary>
    /// An unsupported serialization value contract.
    /// </summary>
    SerializationContract,
    /// <summary>
    /// An abstract serialization contract without tagged variants.
    /// </summary>
    SerializationAbstract,
    /// <summary>
    /// A dictionary with unsupported keys.
    /// </summary>
    DictionaryKey,
    /// <summary>
    /// Duplicate enum storage names or values.
    /// </summary>
    EnumIdentity,
    /// <summary>
    /// A framework type requiring an explicit codec.
    /// </summary>
    FrameworkContract,
    /// <summary>
    /// A conditional JsonIgnore contract.
    /// </summary>
    ConditionalIgnore,
    /// <summary>
    /// A serialized indexer.
    /// </summary>
    Indexer,
    /// <summary>
    /// A serialized property without a public getter.
    /// </summary>
    PublicGetter,
    /// <summary>
    /// A null serialized member name.
    /// </summary>
    NullMemberName,
    /// <summary>
    /// Duplicate serialized member names.
    /// </summary>
    DuplicateMemberName,
    /// <summary>
    /// An ambiguous or inaccessible serialization constructor.
    /// </summary>
    SerializationConstructor,
    /// <summary>
    /// A constructor parameter without an exact member binding.
    /// </summary>
    ConstructorParameter,
    /// <summary>
    /// A read-only member without a constructor binding.
    /// </summary>
    ReadOnlyMember,
    /// <summary>
    /// A constructor that does not preserve required members.
    /// </summary>
    RequiredMembers,
    /// <summary>
    /// A polymorphic contract that is not a class.
    /// </summary>
    PolymorphicClass,
    /// <summary>
    /// A polymorphic contract with identity-losing fallback.
    /// </summary>
    PolymorphicFallback,
    /// <summary>
    /// An incomplete tagged-variant registration.
    /// </summary>
    VariantRegistration,
    /// <summary>
    /// A null string discriminator.
    /// </summary>
    NullDiscriminator,
    /// <summary>
    /// An invalid registered variant type.
    /// </summary>
    VariantType,
    /// <summary>
    /// A duplicate registered variant or discriminator.
    /// </summary>
    DuplicateVariant,
    /// <summary>
    /// A polymorphic contract without registered variants.
    /// </summary>
    MissingVariants,
    /// <summary>
    /// A hidden inherited serialized member.
    /// </summary>
    HiddenMember,
    /// <summary>
    /// An unsupported System.Text.Json attribute.
    /// </summary>
    SerializationAttribute,
}

/// <summary>
/// Carries one transient custom-type failure with its authored source location.
/// </summary>
/// <param name="Kind">The closed diagnostic contract.</param>
/// <param name="Location">The declaration, option, member, or attribute that must be corrected.</param>
/// <param name="Arguments">The fixed message substitutions.</param>
internal sealed record CustomTypeValidationFailure(CustomTypeDiagnosticKind Kind, Location? Location, EquatableArray<string> Arguments);

/// <summary>
/// Defines precise custom-type diagnostics and their stable identifiers.
/// </summary>
internal static class CustomTypeDiagnostics
{
    private const string HelpLink = "https://willibrandon.github.io/ankus/custom-types/#declaration-diagnostics";

    private static readonly DiagnosticDescriptor s_alignment = new("ANKUS419", "Invalid PostgreSQL type alignment",
        "'{0}' must use PgTypeAlignment.FourBytes or PgTypeAlignment.EightBytes", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_codecType = new("ANKUS420", "Invalid custom-type codec option",
        "'{0}' codec options must name accessible closed codec types", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_conflictingCodecs = new("ANKUS421", "Conflicting custom-type storage codecs",
        "'{0}' cannot combine TextCodec with an explicit storage codec", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nativeLayoutCodec = new("ANKUS422", "Invalid native-layout codec selection",
        "'{0}' NativeLayout requires TextCodec and cannot use an explicit storage codec", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nullInputMessage = new("ANKUS423", "Invalid custom-type NULL message",
        "'{0}' NullInputErrorMessage must contain valid Unicode without zero characters", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_refLikeType = new("ANKUS424", "PostgreSQL type cannot be ref-like",
        "'{0}' cannot use PgType because ref-like values cannot cross the generated boundary", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_staticType = new("ANKUS425", "PostgreSQL type cannot be static",
        "'{0}' cannot use PgType because it has no value instances", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_abstractType = new("ANKUS426", "Abstract PostgreSQL type requires variants",
        "'{0}' must declare concrete JsonDerivedType variants when generated serialization is used", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_genericType = new("ANKUS427", "PostgreSQL type cannot be generic",
        "'{0}' and its containing types must be closed and non-generic", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_inaccessibleType = new("ANKUS428", "PostgreSQL type is inaccessible",
        "'{0}' and its containing types must be accessible to generated code and cannot be file-local", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_conflictingTypeKinds = new("ANKUS429", "Conflicting PostgreSQL type declarations",
        "'{0}' cannot carry both PgType and PgEnum", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_codecConstruction = new("ANKUS430", "Custom-type codec is not constructible",
        "Codec '{0}' must be accessible, closed and concrete with an accessible parameterless constructor", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_codecContract = new("ANKUS431", "Custom-type codec has the wrong contract",
        "Codec '{0}' must derive from {1}<T> for the exact non-nullable managed type '{2}'", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_codecRequiredMembers = new("ANKUS432", "Custom-type codec leaves required members unset",
        "Codec '{0}' has required members, so its parameterless constructor must carry SetsRequiredMembers", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nullInheritedSchema = new("ANKUS433", "Inherited PostgreSQL schema is null",
        "'{0}' inherits a PgSchema declaration with a null identifier", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_typeName = new("ANKUS434", "Invalid PostgreSQL type name",
        "'{0}' must use a nonempty PostgreSQL identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_schemaName = new("ANKUS435", "Invalid PostgreSQL type schema",
        "'{0}' schema must be a nonempty identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nativeRoot = new("ANKUS436", "Native layout requires a struct",
        "'{0}' must be an unmanaged struct to use NativeLayout", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nativeField = new("ANKUS437", "Unsupported native-layout field",
        "Native field type '{0}' is not a fixed-width numeric value, enum, or sequential unmanaged struct from the same assembly", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nativeStructLayout = new("ANKUS438", "Invalid native struct layout",
        "'{0}' and every nested struct must use sequential layout, with an optional power-of-two Pack and without Size, CharSet, or InlineArray overrides", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nativeRecursive = new("ANKUS439", "Recursive native value layout",
        "'{0}' contains a recursive native value layout", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nativeFixedBuffer = new("ANKUS440", "Unsupported native fixed buffer",
        "Fixed buffer '{0}' must contain at least one fixed-width numeric element", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nativeEmpty = new("ANKUS441", "Empty native-layout struct",
        "'{0}' cannot use NativeLayout because its packed payload is empty", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nativeTooLarge = new("ANKUS442", "Native layout is too large",
        "'{0}' has a packed native layout that exceeds the supported size", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_discriminatorCollision = new("ANKUS443", "Polymorphic discriminator collides with a member",
        "Serialized type '{0}' uses discriminator '{1}' as a stored member name", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_serializationUnicode = new("ANKUS444", "Invalid serialization metadata text",
        "Serialization names and discriminators for '{0}' must contain valid Unicode", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_serializationGraphLimit = new("ANKUS445", "Serialization graph is too large",
        "The generated serializer for '{0}' exceeds 256 distinct reachable type contracts; use an explicit codec", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_serializationContract = new("ANKUS446", "Unsupported serialization value contract",
        "Serialized value '{0}' must be an accessible closed class, struct, enum, supported collection, or primitive", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_serializationAbstract = new("ANKUS447", "Abstract serialization contract requires variants",
        "Abstract serialized type '{0}' must declare concrete variants with JsonDerivedType", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_dictionaryKey = new("ANKUS448", "Unsupported serialized dictionary key",
        "Dictionary '{0}' must use non-null string keys", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_enumIdentity = new("ANKUS449", "Duplicate serialized enum identity",
        "Enum '{0}' must have unique stored names and underlying values", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_frameworkContract = new("ANKUS450", "Framework type requires an explicit codec",
        "Framework type '{0}' is not a stable generated storage contract; use an explicit codec", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_conditionalIgnore = new("ANKUS451", "Conditional JsonIgnore changes stored shape",
        "Member '{0}' must use JsonIgnoreCondition.Always, JsonIgnoreCondition.Never, or an explicit codec", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_indexer = new("ANKUS452", "Serialized indexer requires an explicit codec",
        "Indexer '{0}' cannot be represented by the generated serializer", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_publicGetter = new("ANKUS453", "Serialized property requires a public getter",
        "Property '{0}' must have a public getter", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nullMemberName = new("ANKUS454", "Serialized member name cannot be null",
        "Member '{0}' has a null JsonPropertyName", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_duplicateMemberName = new("ANKUS455", "Duplicate serialized member name",
        "Serialized member name '{0}' is already used in '{1}'", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_serializationConstructor = new("ANKUS456", "Serialization constructor is ambiguous",
        "Type '{0}' must select one accessible constructor with JsonConstructor or provide an accessible parameterless constructor", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_constructorParameter = new("ANKUS457", "Constructor parameter does not match serialized state",
        "Parameter '{0}' must match exactly one serialized member by name, type, nullability, and by-value passing", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_readOnlyMember = new("ANKUS458", "Read-only member lacks constructor binding",
        "Read-only member '{0}' must bind to a constructor parameter", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_requiredMembers = new("ANKUS459", "Serialization constructor does not preserve required members",
        "Constructor for '{0}' must carry SetsRequiredMembers when it binds or ignores C# required members", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_polymorphicClass = new("ANKUS460", "Polymorphic storage requires a class",
        "Polymorphic serialized type '{0}' must be a class", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_polymorphicFallback = new("ANKUS461", "Polymorphic fallback loses type identity",
        "Polymorphic type '{0}' must fail for unknown discriminators and derived types", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_variantRegistration = new("ANKUS462", "Invalid tagged-variant registration",
        "JsonDerivedType on '{0}' must specify a concrete type and a string or Int32 discriminator", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_nullDiscriminator = new("ANKUS463", "Tagged-variant discriminator cannot be null",
        "JsonDerivedType on '{0}' cannot use a null string discriminator", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_variantType = new("ANKUS464", "Invalid tagged-variant type",
        "Variant '{0}' must be an accessible closed concrete class assignable to '{1}'", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_duplicateVariant = new("ANKUS465", "Duplicate tagged variant",
        "Type '{0}' must use unique variant types and typed discriminators", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_missingVariants = new("ANKUS466", "Polymorphic contract has no variants",
        "JsonPolymorphic type '{0}' must declare at least one JsonDerivedType", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_hiddenMember = new("ANKUS467", "Hidden serialized member requires an explicit codec",
        "Member '{0}' hides inherited serialized state; ignore one member or use an explicit codec", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_serializationAttribute = new("ANKUS468", "Unsupported serialization attribute",
        "Serialization attribute '{0}' on '{1}' is not supported by generated storage; use an explicit codec", "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);
    private static readonly DiagnosticDescriptor s_abstractCodecType = new("ANKUS469", "Abstract PostgreSQL type cannot use an explicit codec",
        "'{0}' must be concrete when using an explicit storage codec; use generated serialization with concrete JsonDerivedType variants or make the type concrete",
        "Ankus", DiagnosticSeverity.Error, true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the stable descriptor for one closed validation failure.
    /// </summary>
    internal static DiagnosticDescriptor Get(CustomTypeDiagnosticKind kind) => kind switch
    {
        CustomTypeDiagnosticKind.Alignment => s_alignment,
        CustomTypeDiagnosticKind.CodecType => s_codecType,
        CustomTypeDiagnosticKind.ConflictingCodecs => s_conflictingCodecs,
        CustomTypeDiagnosticKind.NativeLayoutCodec => s_nativeLayoutCodec,
        CustomTypeDiagnosticKind.NullInputMessage => s_nullInputMessage,
        CustomTypeDiagnosticKind.RefLikeType => s_refLikeType,
        CustomTypeDiagnosticKind.StaticType => s_staticType,
        CustomTypeDiagnosticKind.AbstractType => s_abstractType,
        CustomTypeDiagnosticKind.AbstractCodecType => s_abstractCodecType,
        CustomTypeDiagnosticKind.GenericType => s_genericType,
        CustomTypeDiagnosticKind.InaccessibleType => s_inaccessibleType,
        CustomTypeDiagnosticKind.ConflictingTypeKinds => s_conflictingTypeKinds,
        CustomTypeDiagnosticKind.CodecConstruction => s_codecConstruction,
        CustomTypeDiagnosticKind.CodecContract => s_codecContract,
        CustomTypeDiagnosticKind.CodecRequiredMembers => s_codecRequiredMembers,
        CustomTypeDiagnosticKind.NullInheritedSchema => s_nullInheritedSchema,
        CustomTypeDiagnosticKind.TypeName => s_typeName,
        CustomTypeDiagnosticKind.SchemaName => s_schemaName,
        CustomTypeDiagnosticKind.NativeRoot => s_nativeRoot,
        CustomTypeDiagnosticKind.NativeField => s_nativeField,
        CustomTypeDiagnosticKind.NativeStructLayout => s_nativeStructLayout,
        CustomTypeDiagnosticKind.NativeRecursive => s_nativeRecursive,
        CustomTypeDiagnosticKind.NativeFixedBuffer => s_nativeFixedBuffer,
        CustomTypeDiagnosticKind.NativeEmpty => s_nativeEmpty,
        CustomTypeDiagnosticKind.NativeTooLarge => s_nativeTooLarge,
        CustomTypeDiagnosticKind.DiscriminatorCollision => s_discriminatorCollision,
        CustomTypeDiagnosticKind.SerializationUnicode => s_serializationUnicode,
        CustomTypeDiagnosticKind.SerializationGraphLimit => s_serializationGraphLimit,
        CustomTypeDiagnosticKind.SerializationContract => s_serializationContract,
        CustomTypeDiagnosticKind.SerializationAbstract => s_serializationAbstract,
        CustomTypeDiagnosticKind.DictionaryKey => s_dictionaryKey,
        CustomTypeDiagnosticKind.EnumIdentity => s_enumIdentity,
        CustomTypeDiagnosticKind.FrameworkContract => s_frameworkContract,
        CustomTypeDiagnosticKind.ConditionalIgnore => s_conditionalIgnore,
        CustomTypeDiagnosticKind.Indexer => s_indexer,
        CustomTypeDiagnosticKind.PublicGetter => s_publicGetter,
        CustomTypeDiagnosticKind.NullMemberName => s_nullMemberName,
        CustomTypeDiagnosticKind.DuplicateMemberName => s_duplicateMemberName,
        CustomTypeDiagnosticKind.SerializationConstructor => s_serializationConstructor,
        CustomTypeDiagnosticKind.ConstructorParameter => s_constructorParameter,
        CustomTypeDiagnosticKind.ReadOnlyMember => s_readOnlyMember,
        CustomTypeDiagnosticKind.RequiredMembers => s_requiredMembers,
        CustomTypeDiagnosticKind.PolymorphicClass => s_polymorphicClass,
        CustomTypeDiagnosticKind.PolymorphicFallback => s_polymorphicFallback,
        CustomTypeDiagnosticKind.VariantRegistration => s_variantRegistration,
        CustomTypeDiagnosticKind.NullDiscriminator => s_nullDiscriminator,
        CustomTypeDiagnosticKind.VariantType => s_variantType,
        CustomTypeDiagnosticKind.DuplicateVariant => s_duplicateVariant,
        CustomTypeDiagnosticKind.MissingVariants => s_missingVariants,
        CustomTypeDiagnosticKind.HiddenMember => s_hiddenMember,
        CustomTypeDiagnosticKind.SerializationAttribute => s_serializationAttribute,
        _ => throw new InvalidOperationException("Unknown custom-type diagnostic."),
    };

    /// <summary>
    /// Formats the same message used by compiler diagnostics for direct semantic callers.
    /// </summary>
    internal static string Message(CustomTypeDiagnosticKind kind, params string[] arguments)
        => string.Format(CultureInfo.InvariantCulture, Get(kind).MessageFormat.ToString(CultureInfo.InvariantCulture), arguments);
}
