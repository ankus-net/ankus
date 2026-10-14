using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Ankus.Build;

/// <summary>
/// Reads source-generated extension artifacts from ECMA-335 metadata without executing the extension assembly.
/// </summary>
internal sealed class ExtensionManifest
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the generated C source for native PostgreSQL entry points.
    /// </summary>
    internal string NativeSource => _values["Ankus.NativeSource"];

    /// <summary>
    /// Gets the generated extension installation SQL.
    /// </summary>
    internal string Sql => _values["Ankus.Sql"];

    /// <summary>
    /// Gets the resolved installation graph, or null for a legacy generator.
    /// </summary>
    internal string? SqlGraph => _values.GetValueOrDefault("Ankus.SqlGraph");

    /// <summary>
    /// Gets the additional native symbols that the Native AOT linker must export.
    /// </summary>
    internal string Exports => _values["Ankus.Exports"];

    /// <summary>
    /// Gets whether generated declarations permit moving the extension to another schema.
    /// Older manifests without this metadata contain only relocatable declarations.
    /// </summary>
    internal bool Relocatable => !_values.TryGetValue("Ankus.Relocatable", out string? value) || bool.Parse(value);

    /// <summary>
    /// Reads generated assembly metadata from a compiled extension.
    /// </summary>
    /// <param name="assemblyPath">The managed intermediate assembly path.</param>
    /// <returns>The extension manifest.</returns>
    internal static ExtensionManifest Read(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        var manifest = new ExtensionManifest();
        foreach (CustomAttribute attribute in reader.GetAssemblyDefinition().GetCustomAttributes().Select(handle => reader.GetCustomAttribute(handle)))
        {
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            MemberReference constructor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            TypeReference type = reader.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            if (!reader.StringComparer.Equals(type.Namespace, "System.Reflection") ||
                !reader.StringComparer.Equals(type.Name, "AssemblyMetadataAttribute"))
            {
                continue;
            }

            BlobReader blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1)
            {
                throw new BadImageFormatException("Invalid custom attribute prolog.");
            }

            string? key = blob.ReadSerializedString();
            string? value = blob.ReadSerializedString();
            if (key is not null && value is not null && key.StartsWith("Ankus.", StringComparison.Ordinal))
            {
                manifest._values.Add(key, value);
            }
        }

        if (!manifest._values.ContainsKey("Ankus.NativeSource") || !manifest._values.ContainsKey("Ankus.Sql") ||
            !manifest._values.ContainsKey("Ankus.Exports"))
        {
            throw new InvalidOperationException("No generated Ankus manifest found. Declare a [PgFunction] or [PgInitialize] method, [PgSchema] class, [PgGuc*] property, or assembly [PgSql]/[PgSqlFile]/[PgGucPrefix].");
        }

        return manifest;
    }
}
