using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reads native callback capability once per immutable reference without loading dependency assemblies.
/// </summary>
internal static class NativeReferenceCapabilities
{
    /// <summary>
    /// Discovers a dependency's declared callback contract at the independently cached reference boundary.
    /// </summary>
    /// <param name="reference">The exact portable image or immutable compilation reference.</param>
    /// <param name="cancellationToken">Cancels metadata traversal.</param>
    /// <returns>Whether the dependency requires native callback dispatch.</returns>
    internal static bool Read(MetadataReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference is CompilationReference compilation)
        {
            return compilation.Compilation.Assembly.GetAttributes().Any(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
                attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[0].Value is "Ankus.NativeCallbacks" &&
                attribute.ConstructorArguments[1].Value is "1");
        }

        if (reference is not PortableExecutableReference image)
        {
            return false;
        }

        try
        {
            if (image.GetMetadata() is not AssemblyMetadata assembly)
            {
                return false;
            }

            foreach (ModuleMetadata module in assembly.GetModules())
            {
                cancellationToken.ThrowIfCancellationRequested();
                MetadataReader reader = module.GetMetadataReader();
                if (!reader.IsAssembly)
                {
                    continue;
                }

                foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CustomAttribute attribute = reader.GetCustomAttribute(handle);
                    if (!IsMetadataConstructor(reader, attribute.Constructor))
                    {
                        continue;
                    }

                    BlobReader value = reader.GetBlobReader(attribute.Value);
                    if (value.ReadUInt16() == 1 && value.ReadSerializedString() == "Ankus.NativeCallbacks" &&
                        value.ReadSerializedString() == "1" && value.ReadUInt16() == 0 && value.RemainingBytes == 0)
                    {
                        return true;
                    }
                }
            }
        }
        catch (BadImageFormatException)
        {
            // The compiler owns diagnostics for malformed reference images.
            return false;
        }
        catch (IOException)
        {
            // Missing or unreadable references cannot provide valid compiler metadata.
            return false;
        }

        return false;
    }

    /// <summary>
    /// Requires the exact metadata attribute name and two-string constructor signature before decoding its value.
    /// </summary>
    /// <param name="reader">The compiler-owned reference metadata.</param>
    /// <param name="constructor">The attribute constructor reference or definition.</param>
    /// <returns>Whether the constructor has the declared framework metadata contract.</returns>
    private static bool IsMetadataConstructor(MetadataReader reader, EntityHandle constructor)
    {
        EntityHandle type;
        BlobHandle signature;
        StringHandle name;
        if (constructor.Kind == HandleKind.MemberReference)
        {
            MemberReference member = reader.GetMemberReference((MemberReferenceHandle)constructor);
            type = member.Parent;
            signature = member.Signature;
            name = member.Name;
        }
        else if (constructor.Kind == HandleKind.MethodDefinition)
        {
            MethodDefinition method = reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
            type = method.GetDeclaringType();
            signature = method.Signature;
            name = method.Name;
        }
        else
        {
            return false;
        }

        StringHandle typeName;
        StringHandle typeNamespace;
        if (type.Kind == HandleKind.TypeReference)
        {
            TypeReference definition = reader.GetTypeReference((TypeReferenceHandle)type);
            if (definition.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return false;
            }

            typeName = definition.Name;
            typeNamespace = definition.Namespace;
        }
        else if (type.Kind == HandleKind.TypeDefinition)
        {
            TypeDefinition definition = reader.GetTypeDefinition((TypeDefinitionHandle)type);
            if (!definition.GetDeclaringType().IsNil)
            {
                return false;
            }

            typeName = definition.Name;
            typeNamespace = definition.Namespace;
        }
        else
        {
            return false;
        }

        if (!reader.StringComparer.Equals(name, ".ctor") || !reader.StringComparer.Equals(typeName, "AssemblyMetadataAttribute") ||
            !reader.StringComparer.Equals(typeNamespace, "System.Reflection"))
        {
            return false;
        }

        BlobReader parameters = reader.GetBlobReader(signature);
        SignatureHeader header = parameters.ReadSignatureHeader();
        return header.Kind == SignatureKind.Method && header.IsInstance && !header.IsGeneric &&
            parameters.ReadCompressedInteger() == 2 && parameters.ReadSignatureTypeCode() == SignatureTypeCode.Void &&
            parameters.ReadSignatureTypeCode() == SignatureTypeCode.String && parameters.ReadSignatureTypeCode() == SignatureTypeCode.String &&
            parameters.RemainingBytes == 0;
    }
}
