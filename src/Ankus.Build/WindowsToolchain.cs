namespace Ankus.Build;

/// <summary>
/// Derives C compiler include directories from the Visual C++ and Windows SDK selected by Native AOT's toolchain discovery.
/// </summary>
internal static class WindowsToolchain
{
    /// <summary>
    /// Maps architecture-specific MSVC and Windows SDK library directories to their matching headers.
    /// </summary>
    /// <param name="libraryDirectories">The semicolon-separated directories discovered by the Native AOT SDK.</param>
    /// <returns>Existing include directories for that same toolchain version.</returns>
    internal static IEnumerable<string> GetIncludeDirectories(string libraryDirectories)
    {
        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DirectoryInfo? parent in libraryDirectories.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(static library => new DirectoryInfo(library.Trim()).Parent))
        {
            if (parent is { Parent: DirectoryInfo root } && parent.Name.Equals("lib", StringComparison.OrdinalIgnoreCase))
            {
                includes.Add(Path.Join(root.FullName, "include"));
            }
            else if (parent is { Parent: { Parent: { Parent: DirectoryInfo sdk } library } version } &&
                library.Name.Equals("lib", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string component in new[] { "ucrt", "shared", "um", "winrt" })
                {
                    includes.Add(Path.Join(sdk.FullName, "Include", version.Name, component));
                }
            }
        }

        return includes.Where(Directory.Exists);
    }
}
