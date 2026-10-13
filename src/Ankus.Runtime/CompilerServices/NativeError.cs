using System.ComponentModel;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Copies managed exception diagnostics into owned transport buffers before returning to PostgreSQL's error boundary.
/// This API is used by generated extension dispatchers.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class NativeError
{
    /// <summary>
    /// Writes complete structured diagnostics with owned buffers and a bounded fallback if allocation or encoding fails.
    /// </summary>
    /// <param name="exception">The caught managed exception.</param>
    /// <param name="error">The native caller-owned diagnostic buffer.</param>
    public static unsafe void Write(Exception exception, NativeCallError* error)
    {
        if (exception is PgQueryCanceledException canceled)
        {
            exception = canceled.Diagnostic;
        }

        *error = default;
        error->SqlState = PackSqlState("38000");
        Write(exception, error->Message, 2048);
        try
        {
            if (exception is PgTerminalException terminal)
            {
                error->_reportLevel = (int)terminal.Level + 1;
                WriteDiagnostic(terminal.Diagnostic, error);
                error->SqlState = PackSqlState(terminal.Diagnostic.SqlState ?? "XX000");
            }
            else if (exception is PgException postgres)
            {
                error->SqlState = PackSqlState(postgres.SqlState);
                error->_position = postgres.Position;
                error->_internalPosition = postgres.InternalPosition;
                error->_line = postgres.Line;
                error->_flags = postgres.NativeFlags;
                WriteField(error, NativeDiagnosticField.Message, postgres.Message);
                WriteField(error, NativeDiagnosticField.Detail, postgres.Detail);
                WriteField(error, NativeDiagnosticField.Hint, postgres.Hint);
                WriteField(error, NativeDiagnosticField.Context, postgres.Context ?? PgErrorContext.Captured(exception));
                WriteField(error, NativeDiagnosticField.Schema, postgres.SchemaName);
                WriteField(error, NativeDiagnosticField.Table, postgres.TableName);
                WriteField(error, NativeDiagnosticField.Column, postgres.ColumnName);
                WriteField(error, NativeDiagnosticField.DataType, postgres.DataTypeName);
                WriteField(error, NativeDiagnosticField.Constraint, postgres.ConstraintName);
                WriteField(error, NativeDiagnosticField.InternalQuery, postgres.InternalQuery);
                WriteField(error, NativeDiagnosticField.File, postgres.File);
                WriteField(error, NativeDiagnosticField.Routine, postgres.Routine);
                WriteField(error, NativeDiagnosticField.DetailLog, postgres.DetailLog);
                WriteField(error, NativeDiagnosticField.Backtrace, postgres.Backtrace);
                WriteField(error, NativeDiagnosticField.Domain, postgres.Domain);
            }
            else
            {
                WriteField(error, NativeDiagnosticField.Message, exception.Message);
                WriteField(error, NativeDiagnosticField.Context, PgErrorContext.Captured(exception));
            }
        }
        catch
        {
            // The primary diagnostic is already available if secondary formatting fails.
            error->_flags |= NativeErrorFlags.Incomplete;
        }
    }

    /// <summary>
    /// Writes a bounded, null-terminated UTF-8 message without allowing a secondary managed exception to escape.
    /// The native caller reports PostgreSQL ERROR only after the managed dispatcher has returned.
    /// </summary>
    /// <param name="exception">The caught managed exception.</param>
    /// <param name="destination">The native caller's writable buffer.</param>
    /// <param name="capacity">The buffer size, including the null terminator.</param>
    public static unsafe void Write(Exception exception, byte* destination, int capacity)
    {
        if (destination is null || capacity <= 0)
        {
            return;
        }

        Span<byte> buffer = new(destination, capacity);
        buffer[0] = 0;
        try
        {
            string message = exception.Message;
            Encoding.UTF8.GetEncoder().Convert(message.AsSpan(), buffer[..^1], flush: true, out _, out int written, out _);
            buffer[written] = 0;
        }
        catch
        {
            ReadOnlySpan<byte> fallback = "Managed extension function failed."u8;
            int length = Math.Min(fallback.Length, capacity - 1);
            fallback[..length].CopyTo(buffer);
            buffer[length] = 0;
        }
    }

    /// <summary>
    /// Packs an already validated five-character SQLSTATE using PostgreSQL's six-bit character representation.
    /// </summary>
    internal static int PackSqlState(string state)
    {
        int code = 0;
        for (int index = 0; index < state.Length; index++)
        {
            code |= ((state[index] - '0') & 0x3F) << (index * 6);
        }

        return code;
    }

    /// <summary>
    /// Copies a managed diagnostic into native transport with allocator-matched UTF-8 field buffers.
    /// </summary>
    /// <param name="diagnostic">The validated diagnostic to report.</param>
    /// <param name="error">The initialized destination, whose buffers the caller must release even after failure.</param>
    internal static unsafe void WriteDiagnostic(PgDiagnostic diagnostic, NativeCallError* error)
    {
        error->SqlState = diagnostic.SqlState is null ? 0 : PackSqlState(diagnostic.SqlState);
        error->_position = diagnostic.Position;
        error->_internalPosition = diagnostic.InternalPosition;
        error->_line = diagnostic.Line;
        WriteField(error, NativeDiagnosticField.Message, diagnostic.Message);
        WriteField(error, NativeDiagnosticField.Detail, diagnostic.Detail);
        WriteField(error, NativeDiagnosticField.Hint, diagnostic.Hint);
        WriteField(error, NativeDiagnosticField.Context, diagnostic.Context);
        WriteField(error, NativeDiagnosticField.Schema, diagnostic.SchemaName);
        WriteField(error, NativeDiagnosticField.Table, diagnostic.TableName);
        WriteField(error, NativeDiagnosticField.Column, diagnostic.ColumnName);
        WriteField(error, NativeDiagnosticField.DataType, diagnostic.DataTypeName);
        WriteField(error, NativeDiagnosticField.Constraint, diagnostic.ConstraintName);
        WriteField(error, NativeDiagnosticField.InternalQuery, diagnostic.InternalQuery);
        WriteField(error, NativeDiagnosticField.File, diagnostic.File);
        WriteField(error, NativeDiagnosticField.Routine, diagnostic.Routine);
        WriteField(error, NativeDiagnosticField.DetailLog, diagnostic.DetailLog);
        WriteField(error, NativeDiagnosticField.Domain, diagnostic.Domain);
    }

    private static unsafe void WriteField(NativeCallError* error, NativeDiagnosticField field, string? text)
    {
        if (text is not null)
        {
            error->_fields[(int)field] = NativeValue.FromString(text);
        }
    }
}
