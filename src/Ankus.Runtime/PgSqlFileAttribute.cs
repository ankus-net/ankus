namespace Ankus;

/// <summary>
/// Includes a compiler AdditionalFiles SQL input in the installation script with explicit dependency ordering.
/// </summary>
/// <remarks>
/// Declares a named SQL file without reading files from extension runtime code.
/// Empty files remain dependency anchors, and authored SQL text is preserved.
/// </remarks>
/// <param name="name">The unique, case-sensitive dependency identifier.</param>
/// <param name="path">The project-relative or absolute path of an AdditionalFiles input.</param>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class PgSqlFileAttribute(string name, string path) : Attribute
{
    /// <summary>
    /// Initializes a file block whose dependency identifier is the file name without its final extension,
    /// as pgrx's <c>extension_sql_file!</c> names a file without an explicit name.
    /// </summary>
    /// <param name="path">The project-relative or absolute path of an AdditionalFiles input.</param>
    /// <remarks>
    /// Both <c>/</c> and <c>\</c> separate directories, so the identifier does not depend on the build host.
    /// A name that starts with its only dot, such as <c>.sql</c>, is kept whole.
    /// </remarks>
    public PgSqlFileAttribute(string path)
        : this(FileStem(path), path)
    {
    }

    /// <summary>
    /// Gets the file block's dependency identifier.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the path resolved relative to the extension project directory.
    /// </summary>
    public string Path { get; } = path;

    /// <summary>
    /// Gets or sets identifiers of declarations that must run before this file.
    /// </summary>
    public string[] Requires
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Gets or sets identifiers of declarations that must run after this file.
    /// </summary>
    public string[] Before
    {
        get;
        set;
    } = [];

    /// <summary>
    /// Gets or sets whether this file runs first, last, or according to its explicit dependencies.
    /// </summary>
    public PgSqlOrder Order
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether every object and reference in this file permits extension schema relocation.
    /// The default is false.
    /// </summary>
    public bool Relocatable
    {
        get;
        set;
    }

    /// <summary>
    /// Returns the last path segment without its final extension, as Rust's <c>Path::file_stem</c> does on Windows.
    /// </summary>
    /// <param name="path">The authored path.</param>
    /// <returns>The file stem, or an empty string when the path ends with a separator or is null.</returns>
    private static string FileStem(string? path)
    {
        if (path is null)
        {
            return string.Empty;
        }

        string file = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        int extension = file.LastIndexOf('.');
        return extension > 0 ? file[..extension] : file;
    }
}
