using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reads exact attribute strings from their defining compiler-owned module.
/// </summary>
internal static class ExactAttributeStrings
{
    /// <summary>
    /// Rejects malformed UTF-8 so persisted identities keep their exact bytes.
    /// </summary>
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    /// <summary>
    /// Associates one type-name index with an image identity, including non-owning metadata copies.
    /// </summary>
    private static readonly ConditionalWeakTable<MetadataId, OwnerIndex> s_owners = new();

    /// <summary>
    /// Retains source constants or decodes complete type, property and field attribute blobs.
    /// </summary>
    /// <param name="owner">The declaration carrying the selected attribute.</param>
    /// <param name="attribute">The exact selected semantic attribute.</param>
    /// <param name="cancellationToken">Cancels metadata traversal.</param>
    /// <param name="strings">The exact scalar string arguments and named properties.</param>
    /// <returns>Whether the complete contract is readable without string normalization.</returns>
    internal static bool TryRead(ISymbol owner, AttributeData attribute, CancellationToken cancellationToken,
        out AttributeStrings? strings)
    {
        cancellationToken.ThrowIfCancellationRequested();
        strings = null;
        if (attribute.AttributeClass is null || attribute.AttributeConstructor is null ||
            attribute.ConstructorArguments.Length != attribute.AttributeConstructor.Parameters.Length ||
            attribute.ConstructorArguments.Any(static value => value.Kind == TypedConstantKind.Error) ||
            attribute.NamedArguments.Any(static value => value.Value.Kind == TypedConstantKind.Error))
        {
            return false;
        }

        int ordinal = 0;
        bool selected = false;
        foreach (AttributeData candidate in owner.GetAttributes())
        {
            if (!SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute.AttributeClass))
            {
                continue;
            }

            if (ReferenceEquals(candidate, attribute))
            {
                selected = true;
                break;
            }

            ordinal++;
        }

        if (!selected)
        {
            return false;
        }

        var arguments = new Dictionary<int, string?>();
        var properties = new Dictionary<string, string?>(StringComparer.Ordinal);
        IModuleSymbol? definingModule = owner.ContainingModule;
        if (definingModule is null)
        {
            return false;
        }

        ModuleMetadata? module = definingModule.GetMetadata();
        if (module is null)
        {
            INamedTypeSymbol? sourceType = owner as INamedTypeSymbol ?? owner.ContainingType;
            if (owner.DeclaringSyntaxReferences.Length == 0 && sourceType?.DeclaringSyntaxReferences.Length is not > 0)
            {
                return false;
            }

            for (int index = 0; index < attribute.ConstructorArguments.Length; index++)
            {
                TypedConstant value = attribute.ConstructorArguments[index];
                if (value.Type?.SpecialType == SpecialType.System_String)
                {
                    arguments.Add(index, value.Value as string);
                }
            }

            foreach (KeyValuePair<string, TypedConstant> value in attribute.NamedArguments.Where(static value => value.Value.Type?.SpecialType == SpecialType.System_String))
            {
                properties.Add(value.Key, value.Value.Value as string);
            }

            strings = new(arguments, properties);
            return true;
        }

        try
        {
            MetadataReader reader = module.GetMetadataReader();
            if (!FindOwner(reader, module.Id, owner, cancellationToken, out CustomAttributeHandleCollection handles))
            {
                return false;
            }

            int current = 0;
            foreach (CustomAttributeHandle handle in handles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CustomAttribute encoded = reader.GetCustomAttribute(handle);
                if (!IsConstructor(reader, encoded.Constructor, definingModule, attribute.AttributeClass) || current++ != ordinal)
                {
                    continue;
                }

                BlobReader value = reader.GetBlobReader(encoded.Value);
                if (value.ReadUInt16() != 1)
                {
                    return false;
                }

                foreach (IParameterSymbol parameter in attribute.AttributeConstructor.Parameters)
                {
                    string? text = ReadValue(ref value, parameter.Type, cancellationToken);
                    if (parameter.Type.SpecialType == SpecialType.System_String)
                    {
                        arguments.Add(parameter.Ordinal, text);
                    }
                }

                int count = value.ReadUInt16();
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte target = value.ReadByte();
                    BlobReader type = value;
                    SkipType(ref value);
                    string? name = ReadString(ref value);
                    ISymbol? member = name is null ? null : attribute.AttributeClass.GetMembers(name).FirstOrDefault();
                    ITypeSymbol? expected = member switch
                    {
                        IPropertySymbol { SetMethod: not null } property when target == 0x54 => property.Type,
                        IFieldSymbol { IsReadOnly: false } field when target == 0x53 => field.Type,
                        _ => null,
                    };
                    if (name is null || !names.Add(name) || expected is null || !MatchesType(ref type, expected))
                    {
                        return false;
                    }

                    string? text = ReadValue(ref value, expected, cancellationToken);
                    if (expected.SpecialType == SpecialType.System_String)
                    {
                        properties.Add(name, text);
                    }
                }

                if (value.RemainingBytes != 0)
                {
                    return false;
                }

                strings = new(arguments, properties);
                return true;
            }
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Finds the exact declaration within its defining module, including nested generic containers.
    /// </summary>
    private static bool FindOwner(MetadataReader reader, MetadataId image, ISymbol owner, CancellationToken cancellationToken,
        out CustomAttributeHandleCollection attributes)
    {
        attributes = default;
        INamedTypeSymbol? type = owner as INamedTypeSymbol ?? owner.ContainingType;
        if (type is null)
        {
            return false;
        }

        string identity = MetadataTypeName.Create(type);
        if (TryFindOwnerType(reader, image, identity, cancellationToken, out TypeDefinitionHandle handle))
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            if (owner is INamedTypeSymbol)
            {
                attributes = definition.GetCustomAttributes();
                return true;
            }

            if (owner is IFieldSymbol)
            {
                foreach (FieldDefinition field in definition.GetFields().Select(fieldHandle => reader.GetFieldDefinition(fieldHandle)))
                {
                    if (reader.StringComparer.Equals(field.Name, owner.MetadataName))
                    {
                        attributes = field.GetCustomAttributes();
                        return true;
                    }
                }
            }
            else if (owner is IPropertySymbol)
            {
                foreach (PropertyDefinitionHandle propertyHandle in definition.GetProperties())
                {
                    PropertyDefinition property = reader.GetPropertyDefinition(propertyHandle);
                    if (reader.StringComparer.Equals(property.Name, owner.MetadataName))
                    {
                        attributes = property.GetCustomAttributes();
                        return true;
                    }
                }
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Finds a metadata type through the index shared by every reader of the same image.
    /// </summary>
    /// <param name="reader">The caller-owned reader of the image.</param>
    /// <param name="image">The image identity shared by non-owning metadata copies.</param>
    /// <param name="identity">The exact CLR definition name.</param>
    /// <param name="cancellationToken">Cancels initial indexing and subsequent lookup.</param>
    /// <param name="handle">The selected logical definition handle.</param>
    /// <returns>Whether the selected image contains the named type.</returns>
    internal static bool TryFindOwnerType(MetadataReader reader, MetadataId image, string identity,
        CancellationToken cancellationToken, out TypeDefinitionHandle handle)
    {
        OwnerIndex index = s_owners.GetValue(image, static _ => new());
        return index.TryFind(reader, identity, cancellationToken, out handle);
    }

    /// <summary>
    /// Reads values for the finite CLR attribute constant kinds used by these declarations.
    /// </summary>
    private static string? ReadValue(ref BlobReader reader, ITypeSymbol type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (type is IArrayTypeSymbol array)
        {
            int count = reader.ReadInt32();
            if (count < -1 || count > reader.RemainingBytes)
            {
                throw new BadImageFormatException("The attribute array has an invalid length.");
            }

            for (int index = 0; index < count; index++)
            {
                ReadValue(ref reader, array.ElementType, cancellationToken);
            }

            return null;
        }

        if (type is INamedTypeSymbol { EnumUnderlyingType: { } underlying })
        {
            return ReadValue(ref reader, underlying, cancellationToken);
        }

        if (type is INamedTypeSymbol named && MetadataTypeName.Create(named) == "System.Type")
        {
            if (ReadString(ref reader)?.Contains('\0') == true)
            {
                throw new BadImageFormatException("An attribute type identity contains zero characters.");
            }

            return null;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return ReadString(ref reader);
            case SpecialType.System_Boolean:
            case SpecialType.System_Byte:
            case SpecialType.System_SByte:
                reader.ReadByte();
                break;
            case SpecialType.System_Char:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
                reader.ReadUInt16();
                break;
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Single:
                reader.ReadUInt32();
                break;
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Double:
                reader.ReadUInt64();
                break;
            default:
                throw new BadImageFormatException("The attribute uses an unsupported constant type.");
        }

        return null;
    }

    /// <summary>
    /// Advances over one encoded named-argument type before its exact property name.
    /// </summary>
    private static void SkipType(ref BlobReader reader)
    {
        byte kind = reader.ReadByte();
        if (kind == 0x1d)
        {
            kind = reader.ReadByte();
            if (kind == 0x1d)
            {
                throw new BadImageFormatException("Nested arrays are not valid attribute argument types.");
            }
        }

        if (kind == 0x55)
        {
            ReadString(ref reader);
        }
    }

    /// <summary>
    /// Requires an encoded named-argument kind matching its declared member type.
    /// </summary>
    private static bool MatchesType(ref BlobReader reader, ITypeSymbol expected)
    {
        byte kind = reader.ReadByte();
        if (expected is IArrayTypeSymbol array)
        {
            return kind == 0x1d && MatchesType(ref reader, array.ElementType);
        }

        if (expected.TypeKind == TypeKind.Enum)
        {
            return kind == 0x55 && ReadString(ref reader)?.Split(',')[0] == MetadataTypeName.Create((INamedTypeSymbol)expected);
        }

        if (expected is INamedTypeSymbol named && MetadataTypeName.Create(named) == "System.Type")
        {
            return kind == 0x50;
        }

        return expected.SpecialType switch
        {
            SpecialType.System_Boolean => kind == 0x02,
            SpecialType.System_Char => kind == 0x03,
            SpecialType.System_SByte => kind == 0x04,
            SpecialType.System_Byte => kind == 0x05,
            SpecialType.System_Int16 => kind == 0x06,
            SpecialType.System_UInt16 => kind == 0x07,
            SpecialType.System_Int32 => kind == 0x08,
            SpecialType.System_UInt32 => kind == 0x09,
            SpecialType.System_Int64 => kind == 0x0a,
            SpecialType.System_UInt64 => kind == 0x0b,
            SpecialType.System_Single => kind == 0x0c,
            SpecialType.System_Double => kind == 0x0d,
            SpecialType.System_String => kind == 0x0e,
            _ => false,
        };
    }

    /// <summary>
    /// Rejects unpaired UTF-16 code units before a source constant could be encoded with replacement characters.
    /// </summary>
    /// <param name="value">The decoded semantic string.</param>
    /// <returns>Whether its code units can be persisted as exact UTF-8 without replacement.</returns>
    internal static bool IsUnicode(string? value)
    {
        if (value is null)
        {
            return true;
        }

        try
        {
            _ = s_utf8.GetByteCount(value);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Preserves exact ECMA-335 SerString bytes, including nullable and empty values.
    /// </summary>
    private static string? ReadString(ref BlobReader reader)
    {
        if (reader.TryReadCompressedInteger(out int length))
        {
            return s_utf8.GetString(reader.ReadBytes(length));
        }

        if (reader.ReadByte() == byte.MaxValue)
        {
            return null;
        }

        throw new BadImageFormatException("The attribute contains an invalid serialized string.");
    }

    /// <summary>
    /// Forms the equivalent identity for a metadata type definition.
    /// </summary>
    private static string Identity(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        TypeDefinitionHandle parent = type.GetDeclaringType();
        if (!parent.IsNil)
        {
            return Identity(reader, parent) + "+" + reader.GetString(type.Name);
        }

        string space = reader.GetString(type.Namespace);
        return (space.Length == 0 ? string.Empty : space + ".") + reader.GetString(type.Name);
    }

    /// <summary>
    /// Matches constructors of the selected attribute before selecting its semantic ordinal.
    /// </summary>
    private static bool IsConstructor(MetadataReader reader, EntityHandle handle, IModuleSymbol module, INamedTypeSymbol expected)
    {
        EntityHandle owner;
        StringHandle name;
        if (handle.Kind == HandleKind.MemberReference)
        {
            MemberReference member = reader.GetMemberReference((MemberReferenceHandle)handle);
            owner = member.Parent;
            name = member.Name;
        }
        else if (handle.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinition method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
            owner = method.GetDeclaringType();
            name = method.Name;
        }
        else
        {
            return false;
        }

        if (!reader.StringComparer.Equals(name, ".ctor"))
        {
            return false;
        }

        if (owner.Kind == HandleKind.TypeDefinition)
        {
            return SymbolEqualityComparer.Default.Equals(module.ContainingAssembly.GetTypeByMetadataName(
                Identity(reader, (TypeDefinitionHandle)owner)), expected);
        }

        if (owner.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        TypeReference type = reader.GetTypeReference((TypeReferenceHandle)owner);
        var names = new List<string> { reader.GetString(type.Name) };
        while (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            type = reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);
            names.Add(reader.GetString(type.Name));
        }

        // Nested type names run from the outermost type inward, as metadata names write them.
        names.Reverse();
        string space = reader.GetString(type.Namespace);
        string identity = (space.Length == 0 ? string.Empty : space + ".") + string.Join("+", names);
        if (identity != MetadataTypeName.Create(expected))
        {
            return false;
        }

        IAssemblySymbol? assembly = null;
        if (type.ResolutionScope.Kind is HandleKind.ModuleDefinition or HandleKind.ModuleReference)
        {
            assembly = module.ContainingAssembly;
        }
        else if (type.ResolutionScope.Kind == HandleKind.AssemblyReference)
        {
            AssemblyReference reference = reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            AssemblyIdentity requested;
            try
            {
                requested = new(reader.GetString(reference.Name), reference.Version, reader.GetString(reference.Culture),
                    reader.GetBlobContent(reference.PublicKeyOrToken), (reference.Flags & AssemblyFlags.PublicKey) != 0,
                    (reference.Flags & AssemblyFlags.Retargetable) != 0,
                    (AssemblyContentType)((int)(reference.Flags & AssemblyFlags.ContentTypeMask) >> 9));
            }
            catch (ArgumentException error)
            {
                throw new BadImageFormatException("The attribute constructor references an invalid assembly identity.", error);
            }

            for (int index = 0; index < module.ReferencedAssemblies.Length; index++)
            {
                if (requested.Equals(module.ReferencedAssemblies[index]))
                {
                    assembly = module.ReferencedAssemblySymbols[index];
                    break;
                }
            }
        }

        return assembly is not null && SymbolEqualityComparer.Default.Equals(
            assembly.GetTypeByMetadataName(identity) ?? assembly.ResolveForwardedType(identity), expected);
    }

    /// <summary>
    /// Publishes a complete image index without retaining a reader, symbol or metadata owner.
    /// </summary>
    private sealed class OwnerIndex
    {
        /// <summary>
        /// Serializes initial construction so concurrent attribute reads share the completed index.
        /// </summary>
        private readonly object _gate = new();

        /// <summary>
        /// Holds only owned names and logical table handles after successful construction.
        /// </summary>
        private Dictionary<string, TypeDefinitionHandle>? _types;

        /// <summary>
        /// Finds a declaration without traversing the type table again after publication.
        /// </summary>
        internal bool TryFind(MetadataReader reader, string identity, CancellationToken cancellationToken, out TypeDefinitionHandle handle)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _types ??= Create(reader, cancellationToken);
                return _types.TryGetValue(identity, out handle);
            }
        }

        /// <summary>
        /// Resolves each definition once, detecting cyclic and duplicate metadata so only a complete index is published.
        /// </summary>
        private static Dictionary<string, TypeDefinitionHandle> Create(MetadataReader reader, CancellationToken cancellationToken)
        {
            Dictionary<string, TypeDefinitionHandle> types = new(StringComparer.Ordinal);
            Dictionary<TypeDefinitionHandle, string> identities = new();
            Stack<TypeDefinitionHandle> ancestors = new();
            HashSet<TypeDefinitionHandle> active = new();
            foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                active.Clear();
                TypeDefinitionHandle current = handle;
                while (!current.IsNil && !identities.ContainsKey(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!active.Add(current))
                    {
                        throw new BadImageFormatException("A metadata type has a cyclic declaring type.");
                    }

                    ancestors.Push(current);
                    current = reader.GetTypeDefinition(current).GetDeclaringType();
                }

                while (ancestors.Count != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TypeDefinitionHandle ancestor = ancestors.Pop();
                    TypeDefinition definition = reader.GetTypeDefinition(ancestor);
                    TypeDefinitionHandle parent = definition.GetDeclaringType();
                    string name;
                    if (parent.IsNil)
                    {
                        string space = reader.GetString(definition.Namespace);
                        name = (space.Length == 0 ? string.Empty : space + ".") + reader.GetString(definition.Name);
                    }
                    else
                    {
                        name = identities[parent] + "+" + reader.GetString(definition.Name);
                    }

                    identities.Add(ancestor, name);
                }

                string identity = identities[handle];
                if (types.ContainsKey(identity))
                {
                    throw new BadImageFormatException("A metadata image contains duplicate type identities.");
                }

                types.Add(identity, handle);
            }

            return types;
        }
    }
}

/// <summary>
/// Keeps exact nullable scalar strings independently of compiler symbols and metadata readers.
/// </summary>
/// <param name="Arguments">String-valued constructor arguments indexed by ordinal.</param>
/// <param name="Properties">Explicit string-valued named properties.</param>
internal sealed record AttributeStrings(Dictionary<int, string?> Arguments, Dictionary<string, string?> Properties)
{
    /// <summary>
    /// Keeps existing omitted and explicit-null defaults without normalizing non-null text.
    /// </summary>
    /// <param name="name">The exact named option.</param>
    /// <param name="fallback">Its declared default.</param>
    /// <returns>The exact non-null value, or the existing default.</returns>
    internal string? Property(string name, string? fallback)
        => Properties.TryGetValue(name, out string? value) && value is not null ? value : fallback;
}
