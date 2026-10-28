using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Build.Tests;

/// <summary>
/// Opens an assembly's declarations from the sources embedded in its PDB the way Roslyn's Go to Definition does.
/// </summary>
/// <remarks>
/// Roslyn compiles only the documents that the PDB names for a symbol, with the output kind, language version and
/// preprocessor symbols the PDB records, against the consumer's references. It looks the symbol up there by name and
/// signature and opens the declaration it finds, or the first line of the first document when none matches.
/// </remarks>
internal sealed class PdbNavigation : IDisposable
{
    private static readonly Guid s_compilationOptions = new("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8");
    private static readonly Guid s_embeddedSource = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid s_typeDefinitionDocuments = new("932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");
    private static readonly SymbolDisplayFormat s_signature = SymbolDisplayFormat.CSharpErrorMessageFormat
        .RemoveMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private readonly PEReader _image;
    private readonly MetadataReaderProvider _symbols;
    private readonly MetadataReader _metadata;
    private readonly MetadataReader _pdb;
    private readonly CSharpParseOptions _parseOptions;
    private readonly CSharpCompilationOptions _compilationOptions;
    private readonly Dictionary<DocumentHandle, SyntaxTree?> _documents = [];

    /// <summary>
    /// Opens an assembly and its embedded PDB, or the PDB beside it.
    /// </summary>
    /// <param name="assembly">The assembly's path.</param>
    internal PdbNavigation(string assembly)
    {
        _image = new PEReader(File.OpenRead(assembly));
        DebugDirectoryEntry[] embedded = [.. _image.ReadDebugDirectory().Where(static entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)];
        _symbols = embedded.Length == 1
            ? _image.ReadEmbeddedPortablePdbDebugDirectoryData(embedded[0])
            : MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(Path.ChangeExtension(assembly, ".pdb")));
        _metadata = _image.GetMetadataReader();
        _pdb = _symbols.GetMetadataReader();
        Dictionary<string, string> options = ReadOptions(_pdb);
        _parseOptions = new CSharpParseOptions(
            LanguageVersionFacts.TryParse(options.GetValueOrDefault("language-version"), out LanguageVersion version) ? version : LanguageVersion.Default,
            preprocessorSymbols: options.TryGetValue("define", out string? defines) ? defines.Split(',') : null);
        _compilationOptions = new CSharpCompilationOptions(Enum.Parse<OutputKind>(options["output-kind"]));
    }

    /// <summary>
    /// Opens every public declaration of an assembly as Go to Definition does.
    /// </summary>
    /// <param name="assembly">The assembly as a consumer's compilation sees it.</param>
    /// <param name="references">The consumer's references, which Roslyn gives the documents it compiles.</param>
    /// <returns>The declarations opened, and each one that would not open at its own location together with each
    /// name in an opened document that no using directive or reference supplies.</returns>
    internal PdbNavigationResult Open(IAssemblySymbol assembly, ImmutableArray<MetadataReference> references)
    {
        var failures = new SortedSet<string>(StringComparer.Ordinal);
        var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.Ordinal);
        var declarations = new List<string>();
        HashSet<string> members = MemberNames();
        foreach (ISymbol declaration in Declarations(assembly.GlobalNamespace))
        {
            declarations.Add(declaration.ToDisplayString());
            DocumentHandle[] handles = [.. DocumentHandles(MetadataTokens.EntityHandle(declaration.MetadataToken))];
            SyntaxTree[] trees = [.. handles.Select(Document).OfType<SyntaxTree>()];
            if (trees.Length == 0 || trees.Length != handles.Length)
            {
                failures.Add($"{declaration.ToDisplayString()} has a document without embedded source, so the editor decompiles it");
                continue;
            }

            string key = string.Join('|', trees.Select(static tree => tree.FilePath).Order(StringComparer.Ordinal));
            if (!compilations.TryGetValue(key, out CSharpCompilation? compilation))
            {
                compilation = CSharpCompilation.Create(assembly.Identity.Name, trees, references, _compilationOptions);
                compilations.Add(key, compilation);

                // Outside the documents it compiles, Roslyn sees only the library's public surface, so members that
                // another file declares stay out of reach. Any other name that does not resolve lacks a using directive.
                failures.UnionWith(compilation.GetDiagnostics()
                    .Where(diagnostic => diagnostic.Id is "CS0234" or "CS0246" or "CS0400"
                        || diagnostic.Id == "CS0103" && !members.Contains(Identifier(diagnostic.Location)))
                    .Select(static diagnostic => $"{diagnostic.Location.GetLineSpan()}: {diagnostic.GetMessage(CultureInfo.InvariantCulture)}"));
            }

            if (!Declares(compilation, declaration))
            {
                failures.Add($"{declaration.ToDisplayString()} opens at the first line of {Path.GetFileName(trees[0].FilePath)}");
            }
        }

        return new PdbNavigationResult(declarations, failures);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _symbols.Dispose();
        _image.Dispose();
    }

    private static IEnumerable<ISymbol> Declarations(INamespaceOrTypeSymbol container)
    {
        foreach (ISymbol member in container.GetMembers())
        {
            if (member is INamespaceSymbol nested)
            {
                foreach (ISymbol declaration in Declarations(nested))
                {
                    yield return declaration;
                }

                continue;
            }

            // Accessors open through their property or event, and compiler-generated names cannot be referenced.
            if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)
                || member.IsImplicitlyDeclared
                || member.Name.StartsWith('<')
                || member is IMethodSymbol { AssociatedSymbol: not null })
            {
                continue;
            }

            yield return member;
            if (member is INamedTypeSymbol type)
            {
                foreach (ISymbol declaration in Declarations(type))
                {
                    yield return declaration;
                }
            }
        }
    }

    // A parameter type that does not resolve in the documents changes the signature, so the declaration no longer
    // matches. Roslyn compiles them with nullable annotations disabled.
    private static bool Declares(Compilation compilation, ISymbol declaration)
    {
        IEnumerable<ISymbol> candidates = declaration is INamedTypeSymbol
            ? compilation.GetSymbolsWithName(declaration.Name, SymbolFilter.Type)
            : compilation.GetSymbolsWithName(declaration.ContainingType.Name, SymbolFilter.Type)
                .Where(type => Same(type, declaration.ContainingType))
                .SelectMany(type => ((INamedTypeSymbol)type).GetMembers(declaration.Name));
        return candidates.Any(candidate => Same(candidate, declaration));
    }

    private static bool Same(ISymbol source, ISymbol declaration)
        => source.ToDisplayString(s_signature) == declaration.ToDisplayString(s_signature);

    private static string Identifier(Location location)
        => location.SourceTree!.GetRoot().FindNode(location.SourceSpan, getInnermostNodeForTie: true) is SimpleNameSyntax name
            ? name.Identifier.ValueText
            : location.SourceTree.GetText().ToString(location.SourceSpan);

    private HashSet<string> MemberNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (TypeDefinition type in _metadata.TypeDefinitions.Select(_metadata.GetTypeDefinition))
        {
            names.Add(_metadata.GetString(type.Name));
            names.UnionWith(type.GetMethods().Select(handle => _metadata.GetString(_metadata.GetMethodDefinition(handle).Name)));
            names.UnionWith(type.GetFields().Select(handle => _metadata.GetString(_metadata.GetFieldDefinition(handle).Name)));
            names.UnionWith(type.GetProperties().Select(handle => _metadata.GetString(_metadata.GetPropertyDefinition(handle).Name)));
            names.UnionWith(type.GetEvents().Select(handle => _metadata.GetString(_metadata.GetEventDefinition(handle).Name)));
        }

        return names;
    }

    private static Dictionary<string, string> ReadOptions(MetadataReader pdb)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string[] parts in pdb.GetCustomDebugInformation(EntityHandle.ModuleDefinition)
            .Select(pdb.GetCustomDebugInformation)
            .Where(information => pdb.GetGuid(information.Kind) == s_compilationOptions)
            .Select(information => Encoding.UTF8.GetString(pdb.GetBlobBytes(information.Value)).Split('\0')))
        {
            for (int index = 0; index + 1 < parts.Length; index += 2)
            {
                options[parts[index]] = parts[index + 1];
            }
        }

        return options;
    }

    // The documents Roslyn's SymbolSourceDocumentFinder selects for each kind of declaration.
    private HashSet<DocumentHandle> DocumentHandles(EntityHandle handle)
    {
        var documents = new HashSet<DocumentHandle>();
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                AddMethod((MethodDefinitionHandle)handle, documents, declaringType: true);
                break;
            case HandleKind.TypeDefinition:
                AddType((TypeDefinitionHandle)handle, documents, containingType: true);
                break;
            case HandleKind.FieldDefinition:
                AddType(_metadata.GetFieldDefinition((FieldDefinitionHandle)handle).GetDeclaringType(), documents, containingType: true);
                break;
            case HandleKind.PropertyDefinition:
                PropertyAccessors property = _metadata.GetPropertyDefinition((PropertyDefinitionHandle)handle).GetAccessors();
                AddMethods([property.Getter, property.Setter, .. property.Others], documents);
                break;
            case HandleKind.EventDefinition:
                EventAccessors @event = _metadata.GetEventDefinition((EventDefinitionHandle)handle).GetAccessors();
                AddMethods([@event.Adder, @event.Remover, @event.Raiser, .. @event.Others], documents);
                break;
            default:
                break;
        }

        return documents;
    }

    private void AddMethods(IEnumerable<MethodDefinitionHandle> accessors, HashSet<DocumentHandle> documents)
    {
        foreach (MethodDefinitionHandle accessor in accessors.Where(static accessor => !accessor.IsNil))
        {
            AddMethod(accessor, documents, declaringType: true);
        }
    }

    private void AddMethod(MethodDefinitionHandle handle, HashSet<DocumentHandle> documents, bool declaringType)
    {
        MethodDebugInformation information = _pdb.GetMethodDebugInformation(handle);
        if (!information.Document.IsNil)
        {
            documents.Add(information.Document);
            return;
        }

        if (!information.SequencePointsBlob.IsNil)
        {
            foreach (SequencePoint point in information.GetSequencePoints().Where(static point => !point.Document.IsNil))
            {
                documents.Add(point.Document);
                declaringType = false;
            }
        }

        if (declaringType)
        {
            AddType(_metadata.GetMethodDefinition(handle).GetDeclaringType(), documents, containingType: true);
        }
    }

    private void AddType(TypeDefinitionHandle handle, HashSet<DocumentHandle> documents, bool containingType)
    {
        documents.UnionWith(_pdb.GetCustomDebugInformation(handle)
            .Select(_pdb.GetCustomDebugInformation)
            .Where(information => _pdb.GetGuid(information.Kind) == s_typeDefinitionDocuments)
            .SelectMany(information => DocumentRows(_pdb.GetBlobReader(information.Value))));

        TypeDefinition type = _metadata.GetTypeDefinition(handle);
        foreach (MethodDefinitionHandle method in type.GetMethods())
        {
            AddMethod(method, documents, declaringType: false);
        }

        if (containingType && type.IsNested)
        {
            AddType(type.GetDeclaringType(), documents, containingType: true);
        }

        foreach (TypeDefinitionHandle nested in type.GetNestedTypes())
        {
            AddType(nested, documents, containingType: false);
        }
    }

    private static List<DocumentHandle> DocumentRows(BlobReader rows)
    {
        var handles = new List<DocumentHandle>();
        while (rows.RemainingBytes > 0)
        {
            handles.Add(MetadataTokens.DocumentHandle(rows.ReadCompressedInteger()));
        }

        return handles;
    }

    private SyntaxTree? Document(DocumentHandle handle)
    {
        if (!_documents.TryGetValue(handle, out SyntaxTree? tree))
        {
            string? text = EmbeddedText(handle);
            tree = text is null ? null : CSharpSyntaxTree.ParseText(text, _parseOptions, _pdb.GetString(_pdb.GetDocument(handle).Name));
            _documents.Add(handle, tree);
        }

        return tree;
    }

    private string? EmbeddedText(DocumentHandle handle)
    {
        foreach (byte[] blob in _pdb.GetCustomDebugInformation(handle)
            .Select(_pdb.GetCustomDebugInformation)
            .Where(information => _pdb.GetGuid(information.Kind) == s_embeddedSource)
            .Select(information => _pdb.GetBlobBytes(information.Value)))
        {
            // The blob starts with the uncompressed size, or zero when the text follows uncompressed.
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

/// <summary>
/// What opening an assembly's declarations from its PDB found.
/// </summary>
/// <param name="Declarations">The public declarations opened.</param>
/// <param name="Failures">Each declaration that would not open at its own location and each name that resolves to nothing.</param>
internal sealed record PdbNavigationResult(IReadOnlyList<string> Declarations, IReadOnlyCollection<string> Failures);
