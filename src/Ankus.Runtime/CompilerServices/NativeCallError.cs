using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Carries owned UTF-8 diagnostics and a bounded emergency message across the guarded native boundary.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeCallError
{
    /// <summary>
    /// Contains PostgreSQL's packed SQLSTATE representation.
    /// </summary>
    public int SqlState;

    /// <summary>
    /// Contains a bounded, null-terminated emergency message used if full diagnostic transport fails.
    /// </summary>
    public fixed byte Message[2048];

    /// <summary>
    /// Contains the client query's one-based character position.
    /// </summary>
    internal int _position;

    /// <summary>
    /// Contains the internal query's one-based character position.
    /// </summary>
    internal int _internalPosition;

    /// <summary>
    /// Contains the original source line number.
    /// </summary>
    internal int _line;

    /// <summary>
    /// Contains routing and completeness flags for native errors.
    /// </summary>
    internal NativeErrorFlags _flags;

    /// <summary>
    /// Contains optional full diagnostic strings with allocator-specific release callbacks.
    /// </summary>
    internal NativeErrorFields _fields;

    /// <summary>
    /// Contains an explicit PgLogLevel plus one, or zero for an ordinary ERROR.
    /// </summary>
    internal int _reportLevel;

    /// <summary>
    /// Copies native diagnostics into a managed exception before the native buffer expires.
    /// </summary>
    /// <returns>A managed PostgreSQL exception.</returns>
    internal PgException ToException()
    {
        Span<char> state = stackalloc char[5];
        for (int index = 0; index < state.Length; index++)
        {
            state[index] = (char)(((SqlState >> (index * 6)) & 0x3F) + '0');
        }

        fixed (byte* message = Message)
        {
            return new PgException(new string(state), Read(NativeDiagnosticField.Message) ?? Read(message, 2048),
                Read(NativeDiagnosticField.Detail), Read(NativeDiagnosticField.Hint))
            {
                Context = Read(NativeDiagnosticField.Context),
                SchemaName = Read(NativeDiagnosticField.Schema),
                TableName = Read(NativeDiagnosticField.Table),
                ColumnName = Read(NativeDiagnosticField.Column),
                DataTypeName = Read(NativeDiagnosticField.DataType),
                ConstraintName = Read(NativeDiagnosticField.Constraint),
                InternalQuery = Read(NativeDiagnosticField.InternalQuery),
                File = Read(NativeDiagnosticField.File),
                Routine = Read(NativeDiagnosticField.Routine),
                DetailLog = Read(NativeDiagnosticField.DetailLog),
                Backtrace = Read(NativeDiagnosticField.Backtrace),
                Domain = Read(NativeDiagnosticField.Domain),
                Position = _position,
                InternalPosition = _internalPosition,
                Line = _line,
                NativeFlags = _flags,
            };
        }
    }

    /// <summary>
    /// Converts query cancellation to the managed cancellation contract while retaining its PostgreSQL diagnostics.
    /// </summary>
    /// <returns>The exception to throw after the guarded native call returns.</returns>
    internal Exception ToManagedException()
    {
        PgException diagnostic = ToException();
        Exception exception = diagnostic.SqlState == PgSqlStates.QueryCanceled ? new PgQueryCanceledException(diagnostic) : diagnostic;
        if ((_flags & NativeErrorFlags.Unrecovered) != 0)
        {
            NativeSubtransaction.RecordFailure(exception);
        }

        return exception;
    }

    /// <summary>
    /// Clears every field a failure can write, as the native guard does before each operation. The message is read as a
    /// C string, so an empty first byte clears it without zeroing its whole buffer on every call.
    /// </summary>
    internal void Clear()
    {
        SqlState = 0;
        Message[0] = 0;
        _position = 0;
        _internalPosition = 0;
        _line = 0;
        _flags = 0;
        _fields = default;
        _reportLevel = 0;
    }

    /// <summary>
    /// Releases every owned diagnostic with its originating allocator, including partially populated errors.
    /// </summary>
    internal void Release()
    {
        for (int index = 0; index < NativeErrorFields.Length; index++)
        {
            _fields[index].Release();
        }
    }

    private readonly string? Read(NativeDiagnosticField field) => _fields[(int)field].ReadOptionalString();

    private static string Read(byte* buffer, int capacity)
    {
        ReadOnlySpan<byte> bytes = new(buffer, capacity);
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }
}
