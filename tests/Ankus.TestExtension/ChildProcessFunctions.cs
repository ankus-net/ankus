namespace Ankus.TestExtension;

/// <summary>
/// Runs programs from a backend through <see cref="PgChildProcess"/>.
/// </summary>
public static class ChildProcessFunctions
{
    /// <summary>
    /// Runs a program and returns its exit code.
    /// </summary>
    /// <param name="path">The program's path.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The exit code.</returns>
    [PgFunction]
    public static int ChildRun(string path, string[] arguments) => PgChildProcess.Run(path, arguments);

    /// <summary>
    /// Runs a program that writes a file, then returns the file's text and deletes it.
    /// </summary>
    /// <param name="path">The program's path.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="output">The file the program writes.</param>
    /// <returns>The exit code and the file's text.</returns>
    [PgFunction]
    public static string ChildRunAndRead(string path, string[] arguments, string output)
    {
        int code = PgChildProcess.Run(path, arguments);
        string text = File.Exists(output) ? File.ReadAllText(output) : "";
        File.Delete(output);
        return code + "|" + text;
    }
}
