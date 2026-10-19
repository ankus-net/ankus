namespace Ankus.Build.Tests;

/// <summary>
/// Supplies the Clang the build tests compile with, chosen the way the build tool chooses it.
/// </summary>
internal static class HostCompilers
{
    private static readonly Lazy<Task<string>> s_clang = new(() => ClangSelection.FindAsync(CancellationToken.None));

    /// <summary>
    /// Gets the Clang 20 or later that collects headers: the one on <c>PATH</c> when it is new enough, otherwise
    /// Homebrew's or MacPorts' LLVM, as on a Mac whose <c>PATH</c> starts with Xcode's Apple clang.
    /// </summary>
    /// <returns>The full path of the selected Clang, or <c>clang-cl.exe</c>'s on Windows.</returns>
    internal static Task<string> ClangAsync() => s_clang.Value;
}
