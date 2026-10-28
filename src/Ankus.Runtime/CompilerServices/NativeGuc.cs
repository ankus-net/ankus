using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.CompilerServices;

/// <summary>
/// Provides generated configuration getters and hook dispatchers with scoped native reads and owned value transport.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class NativeGuc
{
    /// <summary>
    /// Makes the native read binding define the setting; other kinds read it. It matches ANKUS_GUC_DEFINE_KIND.
    /// </summary>
    private const int DefineKind = 100;

    /// <summary>
    /// Requests a declared scalar's variable address; other reads return its value. It matches ANKUS_GUC_LOCATE_KIND.
    /// </summary>
    private const int LocateKind = 200;

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [ThreadStatic]
    private static nint s_read;

    [ThreadStatic]
    private static int s_scopeDepth;

    /// <summary>
    /// Enters a configuration hook's native read capability without enabling transaction-only PostgreSQL APIs.
    /// </summary>
    /// <param name="read">The native read entry point, or zero for an explicitly disabled scope.</param>
    /// <returns>The previous read binding to restore in the matching finally block.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use the matching dispatcher's live binding and restore the exact previous entry in reverse order.
    /// </remarks>
    [NativeUnsafeAccess]
    public static nint Enter(nint read)
    {
        nint previous = s_read;
        s_read = read;
        s_scopeDepth++;
        return previous;
    }

    /// <summary>
    /// Restores the enclosing configuration hook read capability.
    /// </summary>
    /// <param name="previous">The binding saved by the matching Enter call.</param>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use the matching dispatcher's live binding and restore the exact previous entry in reverse order.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void Exit(nint previous)
    {
        s_read = previous;
        s_scopeDepth--;
    }

    /// <summary>
    /// Reads the current native Boolean backing value, independently of any display hook.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <returns>The current Boolean value.</returns>
    public static bool ReadBoolean(string name)
    {
        NativeValue value = Read(name, 0);
        try
        {
            return value.ReadGucScalar() switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidOperationException("A native Boolean configuration value must be zero or one."),
            };
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Reads the current native signed integer backing value.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <returns>The current signed integer.</returns>
    public static int ReadInt32(string name) => ReadOrdinal(name, 1);

    /// <summary>
    /// Reads the current native real backing value without losing its floating-point bit representation.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <returns>The current floating-point value.</returns>
    public static double ReadDouble(string name)
    {
        NativeValue value = Read(name, 2);
        try
        {
            return BitConverter.Int64BitsToDouble(value.ReadGucScalar());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Copies the current native string backing value, preserving absent and empty strings.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <returns>The owned current string, or null when the native value is absent.</returns>
    public static string? ReadString(string name)
    {
        NativeValue value = Read(name, 3);
        try
        {
            _ = value.ReadGucBuffer(out bool isNull);
            if (isNull)
            {
                return null;
            }

            string text = value.ReadString();
            return ValidateText(text, nameof(value));
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Copies a nonnullable string setting, rejecting an unexpected absent native value.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <returns>The owned nonnull current string.</returns>
    public static string ReadRequiredString(string name)
        => ReadString(name) ?? throw new InvalidOperationException("A nonnullable configuration setting has an absent native value.");

    /// <summary>
    /// Reads the dense native enum ordinal for conversion by the generated C# enum mapping.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <returns>The current native ordinal.</returns>
    public static int ReadEnum(string name) => ReadOrdinal(name, 4);

    /// <summary>
    /// Reads a declared string setting, converting it from the server encoding only when its bytes changed since the
    /// last read.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <param name="variable">The generated property's cached variable address, zero until located.</param>
    /// <param name="cache">The generated property's last conversion.</param>
    /// <returns>The current string, or null when the setting has no value.</returns>
    /// <remarks>
    /// The variable holds a pointer PostgreSQL replaces whenever it assigns the setting, so the comparison reads the
    /// bytes it points to; a new value can reuse the same pointer. Before PostgreSQL registers the setting the
    /// variable is null and the declared default is read by name.
    /// </remarks>
    public static string? ReadString(string name, ref nint variable, NativeGucText cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        byte* text = *(byte**)Locate(name, 3, ref variable);
        if (text == null)
        {
            return ReadString(name);
        }

        ReadOnlySpan<byte> current = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text);
        if (cache.TryGet(current, out string? value))
        {
            return value;
        }

        value = ReadString(name);
        cache.Set(current, value);
        return value;
    }

    /// <summary>
    /// Reads a declared nonnullable string setting, converting it only when its bytes changed since the last read.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <param name="variable">The generated property's cached variable address, zero until located.</param>
    /// <param name="cache">The generated property's last conversion.</param>
    /// <returns>The current string.</returns>
    public static string ReadRequiredString(string name, ref nint variable, NativeGucText cache)
        => ReadString(name, ref variable, cache) ??
            throw new InvalidOperationException("A nonnullable configuration setting has an absent native value.");

    /// <summary>
    /// Reads a declared Boolean setting from the native variable PostgreSQL assigns, locating it on the first read.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <param name="variable">The generated property's cached variable address, zero until located.</param>
    /// <returns>The current Boolean value.</returns>
    public static bool ReadBoolean(string name, ref nint variable) => *(byte*)Locate(name, 0, ref variable) != 0;

    /// <summary>
    /// Reads a declared integer setting from the native variable PostgreSQL assigns, locating it on the first read.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <param name="variable">The generated property's cached variable address, zero until located.</param>
    /// <returns>The current signed integer.</returns>
    public static int ReadInt32(string name, ref nint variable) => *(int*)Locate(name, 1, ref variable);

    /// <summary>
    /// Reads a declared real setting from the native variable PostgreSQL assigns, locating it on the first read.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <param name="variable">The generated property's cached variable address, zero until located.</param>
    /// <returns>The current floating-point value.</returns>
    public static double ReadDouble(string name, ref nint variable) => *(double*)Locate(name, 2, ref variable);

    /// <summary>
    /// Reads a declared enum setting's ordinal from the native variable PostgreSQL assigns, locating it on the first read.
    /// </summary>
    /// <param name="name">The generated setting's qualified name.</param>
    /// <param name="variable">The generated property's cached variable address, zero until located.</param>
    /// <returns>The current native ordinal.</returns>
    public static int ReadEnum(string name, ref nint variable) => *(int*)Locate(name, 4, ref variable);

    /// <summary>
    /// Runs the hook registered for a run-time definition's slot, which the native hook passes as its third argument.
    /// </summary>
    /// <param name="phase">0 for check, 1 for assign and 2 for show.</param>
    /// <param name="arguments">The current or proposed value, its extra data and the slot.</param>
    /// <param name="results">The accepted value and extra data, or the display text.</param>
    /// <param name="source">The <see cref="PgGucSource"/> of a proposed value.</param>
    /// <param name="error">The native diagnostic buffer for a rejected check.</param>
    /// <returns>0 when the hook completed, or 2 when the check rejected the value.</returns>
    public static int DispatchRuntimeHook(int phase, NativeValue* arguments, NativeValue* results, int source, NativeCallError* error)
    {
        RuntimeGucHooks.Entry entry = RuntimeGucHooks.Get(checked((int)arguments[2].Integral));
        return entry.Kind switch
        {
            0 => Dispatch((PgGucHooks<bool>)entry.Hooks, phase, arguments, results, source, error,
                static value => value.Integral != 0, static value => new NativeValue { Integral = value ? 1 : 0 }),
            1 => Dispatch((PgGucHooks<int>)entry.Hooks, phase, arguments, results, source, error,
                static value => checked((int)value.Integral), static value => new NativeValue { Integral = value }),
            2 => Dispatch((PgGucHooks<double>)entry.Hooks, phase, arguments, results, source, error,
                static value => BitConverter.Int64BitsToDouble(value.Integral),
                static value => new NativeValue { Integral = BitConverter.DoubleToInt64Bits(value) }),
            3 => Dispatch((PgGucHooks<string?>)entry.Hooks, phase, arguments, results, source, error,
                static value => value.IsNull != 0 ? null : value.ReadString(),
                static value => value is null ? new NativeValue { IsNull = 1 } : NativeValue.FromString(value)),
            _ => Dispatch((PgGucHooks<int>)entry.Hooks, phase, arguments, results, source, error,
                static value => checked((int)value.Integral),
                value => entry.Options!.Any(option => option.Value == value) ? new NativeValue { Integral = value }
                    : throw new InvalidOperationException("A GUC check returned a value that is not one of the parameter's labels.")),
        };
    }

    /// <summary>
    /// Runs one typed hook phase.
    /// </summary>
    private static int Dispatch<T>(PgGucHooks<T> hooks, int phase, NativeValue* arguments, NativeValue* results, int source,
        NativeCallError* error, Func<NativeValue, T> read, Func<T, NativeValue> write)
    {
        T value = read(arguments[0]);
        switch (phase)
        {
            case 0 when hooks.Check is { } check:
            {
                PgGucCheckResult<T> result = check(value, (PgGucSource)source)
                    ?? throw new InvalidOperationException("A GUC check hook returned null.");
                if (!result.IsAccepted)
                {
                    WriteCheckError(result.Error, error);
                    return 2;
                }

                results[0] = write(result.Value);
                results[1] = FromExtra(result.Extra);
                return 0;
            }

            case 1:
                hooks.Assign?.Invoke(value, ReadExtra(arguments[1]));
                return 0;
            case 2 when hooks.Show is { } show:
                results[0] = NativeValue.FromString(show(value, ReadExtra(arguments[1]))
                    ?? throw new InvalidOperationException("A GUC show hook returned null."));
                return 0;
            default:
                throw new InvalidOperationException("The native GUC hook phase is invalid.");
        }
    }

    /// <summary>
    /// Copies borrowed native hook extra bytes into immutable managed ownership.
    /// </summary>
    /// <param name="value">The borrowed extra transport; its owner remains responsible for release.</param>
    /// <returns>The owned bytes, including an empty instance, or null for absent extra.</returns>
    public static PgGucExtra? ReadExtra(NativeValue value)
    {
        ReadOnlySpan<byte> bytes = value.ReadGucBuffer(out bool isNull);
        return isNull ? null : new PgGucExtra(bytes);
    }

    /// <summary>
    /// Copies immutable extra bytes into an owned native transport with a matching allocator callback.
    /// </summary>
    /// <param name="extra">The managed bytes, or null for absent extra.</param>
    /// <returns>The owned native result, released by the native hook wrapper after copying.</returns>
    public static NativeValue FromExtra(PgGucExtra? extra)
        => extra is null ? new NativeValue { IsNull = 1 } : NativeValue.FromBytes(extra.AsSpan());

    /// <summary>
    /// Writes owned check-rejection diagnostics without choosing a PostgreSQL reporting severity.
    /// </summary>
    /// <param name="diagnostic">The validated optional diagnostic fields.</param>
    /// <param name="error">The zero-initialized native diagnostic buffer, released by the native caller.</param>
    public static void WriteCheckError(PgGucCheckError diagnostic, NativeCallError* error)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (error is null)
        {
            throw new ArgumentNullException(nameof(error));
        }

        *error = default;
        error->SqlState = NativeError.PackSqlState(diagnostic.SqlState ?? "22023");
        try
        {
            if (diagnostic.Message is string message)
            {
                error->_fields[(int)NativeDiagnosticField.Message] = NativeValue.FromString(message);
            }

            if (diagnostic.Detail is string detail)
            {
                error->_fields[(int)NativeDiagnosticField.Detail] = NativeValue.FromString(detail);
            }

            if (diagnostic.Hint is string hint)
            {
                error->_fields[(int)NativeDiagnosticField.Hint] = NativeValue.FromString(hint);
            }
        }
        catch
        {
            error->Release();
            *error = default;
            throw;
        }
    }

    /// <summary>
    /// Validates text before pinning, allocation, or transfer to PostgreSQL's C-string interfaces.
    /// </summary>
    internal static string? ValidateText(string? value, string parameterName)
    {
        if (value is not null)
        {
            if (value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("PostgreSQL configuration text cannot contain a zero character.", parameterName);
            }

            _ = s_utf8.GetByteCount(value);
        }

        return value;
    }

    /// <summary>
    /// Copies optional text to terminated UTF-8 in native memory for a definition request.
    /// </summary>
    /// <param name="value">The text, or null.</param>
    /// <param name="parameterName">The argument reported when the text is invalid.</param>
    /// <returns>The native copy to free with <see cref="NativeMemory.Free(void*)"/>, or null.</returns>
    internal static byte* AllocateText(string? value, string parameterName)
    {
        string? validated = ValidateText(value, parameterName);
        if (validated is null)
        {
            return null;
        }

        int length = s_utf8.GetByteCount(validated);
        byte* text = (byte*)NativeMemory.Alloc((nuint)length + 1);
        s_utf8.GetBytes(validated, new Span<byte>(text, length));
        text[length] = 0;
        return text;
    }

    /// <summary>
    /// Encodes a setting name as a terminated UTF-8 string for either native read route.
    /// </summary>
    internal static byte[] EncodeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateText(name, nameof(name));
        byte[] bytes = new byte[s_utf8.GetByteCount(name) + 1];
        s_utf8.GetBytes(name, bytes);
        return bytes;
    }

    /// <summary>
    /// Returns a declared scalar's native variable after the same access checks as a read by name.
    /// </summary>
    /// <remarks>
    /// PostgreSQL assigns a scalar setting by storing into the variable it was given, so reading that memory sees every
    /// change, including reverts at transaction end. The first read validates the name and type through the native
    /// binding; later reads only repeat the managed access checks, avoiding a backend call per read.
    /// </remarks>
    private static nint Locate(string name, int kind, ref nint variable)
    {
        if (variable == 0)
        {
            NativeValue value = Read(name, LocateKind + kind);
            try
            {
                variable = checked((nint)value.ReadGucScalar());
            }
            finally
            {
                value.Release();
            }

            return variable;
        }

        if (s_scopeDepth == 0)
        {
            NativeBackend.CheckAccess();
        }
        else
        {
            NativeBorrowScope.CheckBackendAccess();
            if (s_read == 0)
            {
                throw new InvalidOperationException("Configuration reads are unavailable in this callback scope.");
            }
        }

        return variable;
    }

    private static int ReadOrdinal(string name, int kind)
    {
        NativeValue value = Read(name, kind);
        try
        {
            return checked((int)value.ReadGucScalar());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Defines a run-time configuration parameter through the same route as reads: the callback's configuration binding
    /// while a library loads, including outside a transaction, otherwise the guarded backend.
    /// </summary>
    /// <param name="name">The terminated UTF-8 qualified name.</param>
    /// <param name="definition">The definition, whose text remains valid for the call.</param>
    internal static void Define(byte[] name, NativeGucDefinition* definition)
    {
        NativeBorrowScope.CheckBackendAccess();
        if (s_scopeDepth == 0)
        {
            NativeBackend.DefineGuc(name, definition);
            return;
        }

        if (s_read == 0)
        {
            throw new InvalidOperationException("Configuration definitions are unavailable in this callback scope.");
        }

        NativeCallError error = default;
        try
        {
            fixed (byte* text = name)
            {
                var read = (delegate* unmanaged[Cdecl]<byte*, int, NativeValue*, NativeCallError*, int>)s_read;
                if (read(text, DefineKind, (NativeValue*)definition, &error) != 0)
                {
                    throw error.ToManagedException();
                }
            }
        }
        finally
        {
            error.Release();
        }
    }

    private static NativeValue Read(string name, int kind)
    {
        NativeBorrowScope.CheckBackendAccess();
        if (s_scopeDepth == 0)
        {
            return NativeBackend.ReadGuc(name, kind);
        }

        if (s_read == 0)
        {
            throw new InvalidOperationException("Configuration reads are unavailable in this callback scope.");
        }

        byte[] bytes = EncodeName(name);
        NativeValue value = default;
        NativeCallError error = default;
        bool transferred = false;
        try
        {
            fixed (byte* text = bytes)
            {
                var read = (delegate* unmanaged[Cdecl]<byte*, int, NativeValue*, NativeCallError*, int>)s_read;
                if (read(text, kind, &value, &error) != 0)
                {
                    throw error.ToManagedException();
                }
            }

            transferred = true;
            return value;
        }
        finally
        {
            if (!transferred)
            {
                value.Release();
            }

            error.Release();
        }
    }
}
