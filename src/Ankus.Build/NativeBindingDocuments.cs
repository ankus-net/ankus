using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ankus.Build;

/// <summary>
/// Lays out generated binding source as one small document per declaration, so an editor can open a declaration's own
/// source from the companion's PDB. The cached artifact stays one file that compiles as written.
/// </summary>
/// <remarks>
/// Editors open embedded PDB source as a file and refuse files of tens of megabytes; without small documents they fall
/// back to decompiling a class of several hundred thousand lines. Each top-level type compiles from its own document.
/// The members of <c>NativeMethods</c> and <c>NativeGlobals</c> stay in one declaration per class, since thousands of
/// partial declarations of one class make compilation much slower, and <c>#line</c> maps each member to its document.
/// </remarks>
internal static class NativeBindingDocuments
{
    private const string DocumentPrefix = "// ankus-document: ";
    private const string MemberPrefix = "// ankus-member: ";
    private const string Extension = ".g.cs";
    private const string DocumentDirectory = "documents/";
    private const string MemberDirectory = "members/";
    private const int MaximumStemLength = 96;

    // Windows reserves these device names in every directory, with or without an extension. The others it reserves
    // contain characters that a C identifier cannot.
    private static readonly HashSet<string> s_reservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Gets the path of the only file compiled from source that has no markers, such as a companion generated before
    /// documents.
    /// </summary>
    internal static string SingleDocument => "native-binding.g.cs";

    /// <summary>
    /// Gets the line that ends the members of the current class. The class's closing lines follow it.
    /// </summary>
    internal static string EndMarker => "// ankus-end";

    /// <summary>
    /// Formats the comment line that starts the document of a top-level declaration.
    /// </summary>
    /// <param name="name">A document name from <see cref="Name"/>.</param>
    /// <returns>The marker line, without its line ending.</returns>
    internal static string Marker(string name) => DocumentPrefix + name;

    /// <summary>
    /// Formats the comment line that starts the document of a member of the class whose document came last.
    /// </summary>
    /// <param name="name">A document name from <see cref="Name"/>.</param>
    /// <returns>The marker line, without its line ending.</returns>
    internal static string MemberMarker(string name) => MemberPrefix + name;

    /// <summary>
    /// Chooses a document file name for a declaration that every supported file system accepts and that differs from
    /// the names already used, ignoring case.
    /// </summary>
    /// <param name="used">The names chosen so far, compared without regard to case.</param>
    /// <param name="stem">The declaration's name, optionally qualified by its containing class with a period.</param>
    /// <returns>The chosen name, which is also added to <paramref name="used"/>.</returns>
    internal static string Name(HashSet<string> used, string stem)
    {
        if (!IsStem(stem))
        {
            throw new FormatException("A generated binding document needs a C identifier name.");
        }

        if (s_reservedNames.Contains(stem.Split('.')[0]))
        {
            stem += "_";
        }

        if (stem.Length > MaximumStemLength)
        {
            // Keep the readable start, and tell long names with the same start apart by the hash of the whole name.
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stem)))[..16];
            stem = stem[..(MaximumStemLength - hash.Length - 1)] + "_" + hash;
        }

        string name = stem + Extension;
        int suffix = 1;
        while (!used.Add(name))
        {
            name = stem + "_" + (++suffix).ToString(CultureInfo.InvariantCulture) + Extension;
        }

        return name;
    }

    /// <summary>
    /// Lays out generated source as the files to compile and the documents to embed in the PDB. Source without markers
    /// compiles as one file and embeds nothing.
    /// </summary>
    /// <param name="source">The complete generated source.</param>
    /// <returns>The files in source order, with paths relative to the compilation's directory.</returns>
    internal static NativeBindingDocumentLayout Split(string source)
    {
        source = source.ReplaceLineEndings("\n");
        List<(string? Prefix, string Name, int Marker, int Start, int End)> regions = Regions(source);
        if (regions.Count == 0)
        {
            return new([new(SingleDocument, source)], []);
        }

        string preamble = source[..regions[0].Marker];
        int preambleLines = Lines(preamble);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var compiled = new List<NativeBindingDocument?>();
        var embedded = new List<NativeBindingDocument?>();
        string? container = null;
        string header = "";
        string wrapper = "";
        int compiledSlot = -1;
        int embeddedSlot = -1;
        StringBuilder? mapped = null;
        foreach ((string? prefix, string name, _, int start, int end) in regions)
        {
            string text = source[start..end];
            if (prefix is not null && (!name.EndsWith(Extension, StringComparison.Ordinal) || !IsStem(name[..^Extension.Length]) ||
                s_reservedNames.Contains(name.Split('.')[0]) || !names.Add(name)))
            {
                throw new FormatException($"The generated binding document name '{name}' is invalid or repeated.");
            }

            if (prefix == DocumentPrefix)
            {
                if (container is not null)
                {
                    throw new FormatException($"The members of '{container}' have no end marker.");
                }

                compiled.Add(new(DocumentDirectory + name, preamble + text));
                embedded.Add(new(DocumentDirectory + name, preamble + text));
                header = text;
                continue;
            }

            if (prefix == MemberPrefix)
            {
                if (container is null)
                {
                    // The previous document declares the class that holds these members. It compiles with them, mapped
                    // to its own document, which receives the class's closing lines at the end marker.
                    if (compiled.Count == 0 || compiled[^1]!.Path.StartsWith(MemberDirectory, StringComparison.Ordinal) ||
                        !header.EndsWith("{\n", StringComparison.Ordinal))
                    {
                        throw new FormatException($"The member document '{name}' does not follow a class declaration.");
                    }

                    container = compiled[^1]!.Path[DocumentDirectory.Length..];
                    wrapper = string.Concat(header.Split('\n').Where(static line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                        .Select(static line => line + "\n"))[..^1];
                    compiledSlot = compiled.Count - 1;
                    embeddedSlot = embedded.Count - 1;
                    compiled[compiledSlot] = null;
                    embedded[embeddedSlot] = null;
                    mapped = new StringBuilder(preamble).Append(Line(preambleLines + 1, container)).Append(header);
                }

                mapped!.Append(Line(preambleLines + Lines(wrapper) + 1, name)).Append(text);
                embedded.Add(new(DocumentDirectory + name, preamble + wrapper + text + "}\n"));
                continue;
            }

            if (container is null)
            {
                // A class without members keeps its closing lines in its own document.
                if (compiled.Count == 0 || compiled[^1]!.Path.StartsWith(MemberDirectory, StringComparison.Ordinal) ||
                    !header.EndsWith("{\n", StringComparison.Ordinal))
                {
                    throw new FormatException("An end marker does not follow a class declaration.");
                }

                compiled[^1] = embedded[^1] = new(compiled[^1]!.Path, compiled[^1]!.Text + text);
                header = "";
                continue;
            }

            mapped!.Append(Line(preambleLines + Lines(header) + 1, container)).Append(text);
            compiled[compiledSlot] = new(MemberDirectory + container, mapped.ToString());
            embedded[embeddedSlot] = new(DocumentDirectory + container, preamble + header + text);
            container = null;
            header = "";
            mapped = null;
        }

        if (container is not null)
        {
            throw new FormatException($"The members of '{container}' have no end marker.");
        }

        return new([.. compiled.Select(static document => document!)], [.. embedded.Select(static document => document!)]);
    }

    /// <summary>
    /// Finds every marker line and the text that follows it, up to the next marker. An end marker has no prefix or name.
    /// </summary>
    private static List<(string? Prefix, string Name, int Marker, int Start, int End)> Regions(string source)
    {
        var regions = new List<(string? Prefix, string Name, int Marker, int Start, int End)>();
        int marker = -1;
        string? prefix = null;
        string name = "";
        int content = 0;
        for (int line = 0; line < source.Length;)
        {
            int next = source.IndexOf('\n', line);
            next = next < 0 ? source.Length : next + 1;
            ReadOnlySpan<char> text = source.AsSpan(line, next - line).TrimEnd('\n');
            string? found = text.StartsWith(DocumentPrefix, StringComparison.Ordinal) ? DocumentPrefix
                : text.StartsWith(MemberPrefix, StringComparison.Ordinal) ? MemberPrefix : null;
            if (found is not null || text.SequenceEqual(EndMarker))
            {
                if (next == source.Length && !source.EndsWith('\n'))
                {
                    throw new FormatException("A generated binding document marker has no content.");
                }

                if (marker >= 0)
                {
                    regions.Add((prefix, name, marker, content, line));
                }

                marker = line;
                prefix = found;
                name = found is null ? "" : text[found.Length..].ToString();
                content = next;
            }

            line = next;
        }

        if (marker >= 0)
        {
            regions.Add((prefix, name, marker, content, source.Length));
        }

        return regions;
    }

    private static string Line(int line, string document)
        => "#line " + line.ToString(CultureInfo.InvariantCulture) + " \"../" + DocumentDirectory + document + "\"\n";

    private static int Lines(string text) => text.Count(static character => character == '\n');

    private static bool IsStem(string stem)
        => stem.Length != 0 && stem.Split('.').All(static segment => segment.Length != 0 &&
            !char.IsAsciiDigit(segment[0]) && segment.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_'));
}

/// <summary>
/// Holds the files that compile a generated binding and the documents its PDB embeds.
/// </summary>
/// <param name="Compiled">The files to compile, in source order.</param>
/// <param name="Embedded">The documents to embed in the PDB, in source order. A top-level declaration's document is also
/// compiled.</param>
internal sealed record NativeBindingDocumentLayout(IReadOnlyList<NativeBindingDocument> Compiled, IReadOnlyList<NativeBindingDocument> Embedded)
{
    /// <summary>
    /// Gets every distinct file to write before compiling.
    /// </summary>
    internal IEnumerable<NativeBindingDocument> Files => Embedded.Concat(Compiled).DistinctBy(static document => document.Path, StringComparer.Ordinal);
}

/// <summary>
/// Holds one generated binding file.
/// </summary>
/// <param name="Path">The file's path relative to the compilation's directory, with forward slashes.</param>
/// <param name="Text">The file's complete source.</param>
internal sealed record NativeBindingDocument(string Path, string Text);
