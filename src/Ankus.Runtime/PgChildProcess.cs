using System;
using System.IO;
using System.Text;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Runs a program from a backend or background worker and waits for it, as pglogical runs <c>pg_dump</c> and
/// <c>pg_restore</c> to copy a schema.
/// </summary>
/// <remarks>
/// <para>
/// .NET's <c>System.Diagnostics.Process</c> installs its own <c>SIGCHLD</c> handling, which is not safe inside a
/// PostgreSQL process. This starts the program with <c>posix_spawn</c> on Unix and <c>CreateProcessW</c> on Windows,
/// and waits on the backend's latch, so query cancellation, statement timeouts and termination still work: they stop
/// the program before PostgreSQL raises the interrupt.
/// </para>
/// <para>
/// The program inherits the server's environment, working directory and standard streams, so its output goes to the
/// server log; pass options such as <c>--file</c> to write results to a file. Arguments are passed exactly, including
/// spaces, quotes and non-ASCII text. On Windows, a program with a narrow <c>main</c>, which includes PostgreSQL's own
/// programs, receives them converted to the system's ANSI code page, so non-ASCII text arrives only when that code page
/// contains it.
/// </para>
/// </remarks>
public static unsafe class PgChildProcess
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Runs a program and waits for it to exit.
    /// </summary>
    /// <param name="path">The program's full path, such as <c>pg_dump</c> in the server's bin directory.</param>
    /// <param name="arguments">The arguments, after the program name.</param>
    /// <returns>The exit code, or on Unix 128 plus the signal number when a signal ended the program.</returns>
    /// <exception cref="PgException">The program could not be started, or the wait was interrupted.</exception>
    public static int Run(string path, params ReadOnlySpan<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] program = Encode(path, nameof(path));
        using MemoryStream block = new();
        foreach (string argument in arguments)
        {
            block.Write(Encode(argument, nameof(arguments)));
        }

        byte[] encoded = block.ToArray();
        fixed (byte* programAddress = program, argumentAddress = encoded)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.ChildProcess,
                _pointer = (nint)programAddress,
                _data = (nint)argumentAddress,
                _value = arguments.Length,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return unchecked((int)result._value);
        }
    }

    private static byte[] Encode(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Program text cannot contain a zero character.", name);
        }

        byte[] bytes = new byte[s_utf8.GetByteCount(value) + 1];
        s_utf8.GetBytes(value, bytes);
        return bytes;
    }
}
