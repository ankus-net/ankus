using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Ankus.IntegrationTests;

/// <summary>
/// Reads where a compiled binding companion's PDB places its declarations, as Go to Definition does.
/// </summary>
internal sealed class CompanionSymbols : IDisposable
{
    private static readonly Guid s_embeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid s_typeDefinitionDocuments = new("932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");
    private readonly PEReader _image;
    private readonly MetadataReaderProvider _symbols;
    private readonly MetadataReader _metadata;
    private readonly MetadataReader _pdb;

    /// <summary>
    /// Opens a companion assembly and the PDB beside it.
    /// </summary>
    /// <param name="assembly">The companion assembly's path.</param>
    internal CompanionSymbols(string assembly)
    {
        _image = new PEReader(File.OpenRead(assembly));
        _symbols = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(assembly, ".pdb")));
        _metadata = _image.GetMetadataReader();
        _pdb = _symbols.GetMetadataReader();
        Documents = _pdb.Documents.ToDictionary(handle => _pdb.GetString(_pdb.GetDocument(handle).Name), Embedded, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets every document the PDB names, with its embedded source, or null when the PDB does not embed it.
    /// </summary>
    internal IReadOnlyDictionary<string, string?> Documents { get; }

    /// <summary>
    /// Gets the document holding a method's first statement, which is where Go to Definition opens a member.
    /// </summary>
    /// <param name="type">The declaring type's name.</param>
    /// <param name="method">The method's metadata name, such as <c>get_</c> and a property's name.</param>
    /// <returns>The document's path in the PDB.</returns>
    internal string MemberDocument(string type, string method)
    {
        MethodDefinitionHandle handle = Type(type).GetMethods().Single(handle => _metadata.GetString(_metadata.GetMethodDefinition(handle).Name) == method);
        SequencePoint point = _pdb.GetMethodDebugInformation(handle).GetSequencePoints().First(static point => !point.IsHidden);
        return _pdb.GetString(_pdb.GetDocument(point.Document).Name);
    }

    /// <summary>
    /// Gets the documents that declare a type, which is where Go to Definition opens a type without methods.
    /// </summary>
    /// <param name="type">The type's name.</param>
    /// <returns>The documents' paths in the PDB.</returns>
    internal IReadOnlyList<string> TypeDocuments(string type)
    {
        TypeDefinitionHandle handle = _metadata.TypeDefinitions.Single(handle => _metadata.GetString(_metadata.GetTypeDefinition(handle).Name) == type);
        var documents = new List<string>();
        foreach (CustomDebugInformationHandle information in _pdb.GetCustomDebugInformation(handle))
        {
            CustomDebugInformation value = _pdb.GetCustomDebugInformation(information);
            if (_pdb.GetGuid(value.Kind) == s_typeDefinitionDocuments)
            {
                BlobReader rows = _pdb.GetBlobReader(value.Value);
                while (rows.RemainingBytes > 0)
                {
                    documents.Add(_pdb.GetString(_pdb.GetDocument(MetadataTokens.DocumentHandle(rows.ReadCompressedInteger())).Name));
                }
            }
        }

        return documents;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _symbols.Dispose();
        _image.Dispose();
    }

    private TypeDefinition Type(string name)
        => _metadata.GetTypeDefinition(_metadata.TypeDefinitions.Single(handle => _metadata.GetString(_metadata.GetTypeDefinition(handle).Name) == name));

    private string? Embedded(DocumentHandle document)
    {
        foreach (CustomDebugInformationHandle handle in _pdb.GetCustomDebugInformation(document))
        {
            CustomDebugInformation information = _pdb.GetCustomDebugInformation(handle);
            if (_pdb.GetGuid(information.Kind) != s_embeddedSource)
            {
                continue;
            }

            // The blob starts with the uncompressed size, or zero when the text follows uncompressed.
            byte[] blob = _pdb.GetBlobBytes(information.Value);
            if (BitConverter.ToInt32(blob, 0) == 0)
            {
                return Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
            }

            using var deflate = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
            using var reader = new StreamReader(deflate, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return null;
    }
}
