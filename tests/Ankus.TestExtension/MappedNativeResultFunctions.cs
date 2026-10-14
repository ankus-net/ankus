using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Ankus.TestExtension;

/// <summary>
/// Calls source-verified native addresses with selected mapped readers and genuine raw arguments.
/// </summary>
[PgSchema("mapped_native")]
public static class MappedNativeResultFunctions
{
    /// <summary>
    /// Selects integer, alias, enum, required, or failing-factory result readers.
    /// </summary>
    [PgFunction(Name = "number")]
    public static int? Number(long address, string sql, int kind) => kind switch
    {
        0 => Invoke<ResultInt?>((nint)address, 0, sql)?.Value,
        1 => Invoke<ReadMappedInt?>((nint)address, 0, sql)?.Value,
        2 => (int?)Invoke<MappedSign?>((nint)address, 0, sql),
        3 => Invoke<ResultInt>((nint)address, 0, sql).Value,
        4 => Invoke<ResultFactoryValue?>((nint)address, 0, sql)?.Value,
        _ => Invoke<ResultOrdinaryFactoryValue?>((nint)address, 0, sql)?.Value,
    };

    /// <summary>
    /// Keeps complete text after native result and original input owners are deleted.
    /// </summary>
    [PgFunction(Name = "text")]
    public static string? Text(long address, string sql)
    {
        ResultText? value = Invoke<ResultText?>((nint)address, 0, sql);
        Spi.Execute("SELECT repeat('disturb native text',10000)");
        return value?.Value;
    }

    /// <summary>
    /// Forwards the supplied collation to a source-verified native text comparison function.
    /// </summary>
    [PgFunction(Name = "compare")]
    public static int Compare(long address, uint collation, string sql)
        => Invoke<ResultInt>((nint)address, collation, sql).Value;

    /// <summary>
    /// Reads vectors through value, read-only alias, or enum element readers.
    /// </summary>
    [PgFunction(Name = "vector")]
    public static int?[]? Vector(long address, string sql, int kind)
    {
        ArrayValueConverter.Reset();
        return kind switch
        {
            0 => Invoke<ArrayValue?[]?>((nint)address, 0, sql)?.Select(static item => item is { } present ? present.Value : (int?)null).ToArray(),
            1 => Invoke<ReadMappedInt?[]?>((nint)address, 0, sql)?.Select(static item => item is { } present ? present.Value : (int?)null).ToArray(),
            _ => Invoke<MappedSign?[]?>((nint)address, 0, sql)?.Select(static item => (int?)item).ToArray(),
        };
    }

    /// <summary>
    /// Returns exact mapped array identity and independently inspectable dimensions, bounds and cells.
    /// </summary>
    [PgFunction(Name = "shape")]
    public static string Shape(long address, string sql)
    {
        ArrayValueConverter.Reset();
        PgArray<ArrayValue?>? values = Invoke<PgArray<ArrayValue?>?>((nint)address, 0, sql);
        return Describe(values, static item => item is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL");
    }

    /// <summary>
    /// Detaches reference elements even when the native function directly returns an original argument pointer.
    /// </summary>
    [PgFunction(Name = "text_array")]
    public static string?[]? TextArray(long address, string sql)
    {
        ArrayTextConverter.Inputs.Clear();
        ArrayText?[]? values = Invoke<ArrayText?[]?>((nint)address, 0, sql);
        Spi.Execute("SELECT repeat('disturb native array',10000)");
        return values?.Select(static item => item?.Value).ToArray();
    }

    /// <summary>
    /// Exercises present, empty and whole-NULL arrays without constructing a failing element factory.
    /// </summary>
    [PgFunction(Name = "factory_array")]
    public static int? FactoryArray(long address, string sql, bool shaped)
        => shaped ? Invoke<PgArray<ResultFactoryValue?>?>((nint)address, 0, sql)?.Count
            : Invoke<ResultFactoryValue?[]?>((nint)address, 0, sql)?.Length;

    /// <summary>
    /// Rejects writer-only vector or shaped result contracts using correctly represented array arguments.
    /// </summary>
    [PgFunction(Name = "denied_array")]
    public static int DeniedArray(long address, string sql, bool shaped)
        => shaped ? Invoke<PgArray<WriteMappedInt?>?>((nint)address, 0, sql)?.Count ?? -1
            : Invoke<WriteMappedInt?[]?>((nint)address, 0, sql)?.Length ?? -1;

    /// <summary>
    /// Creates a real large object, then catches only deliberately selected conversion or capability errors.
    /// </summary>
    [PgFunction(Name = "create_object")]
    public static string Create(long address, uint requested, int mode)
    {
        NativeOidConverter.Mode = mode;
        PgDatum argument;
        unsafe
        {
            argument = PgDatum.DangerousCreate(requested, 26, PgMemoryContext.Current);
        }

        try
        {
            uint value = mode switch
            {
                3 => CallChecked<NativeOidFactory>((nint)address, 0, [argument]).Value,
                4 => CallChecked<NativeWriteOid?>((nint)address, 0, [argument])?.Value ?? 0,
                _ => CallChecked<NativeOid>((nint)address, 0, [argument]).Value,
            };
            return value.ToString(CultureInfo.InvariantCulture);
        }
        catch (PgException error)
        {
            return $"{error.SqlState}|{error.Message}|{error.Detail}|{error.Hint}";
        }
        catch (FormatException error)
        {
            return $"{error.GetType().Name}|{error.Message}";
        }
        catch (NotSupportedException error)
        {
            return $"{error.GetType().Name}|{error.Message}";
        }
    }

    /// <summary>
    /// Rejects a stale argument before a native object can be created and preserves a separate live owner.
    /// </summary>
    [PgFunction(Name = "stale")]
    public static string Stale(long address, uint requested)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("native stale argument");
        PgDatum stale;
        PgDatum live;
        unsafe
        {
            stale = PgDatum.DangerousCreate(requested, 26, owner);
            live = PgDatum.DangerousCreate(73, 26, PgMemoryContext.Current);
        }

        owner.Reset();
        try
        {
            unsafe
            {
                _ = PgFunctions.DangerousCall<NativeOid>((nint)address, 0, stale);
            }

            throw new InvalidOperationException("An expired native argument was accepted.");
        }
        catch (ObjectDisposedException)
        {
            return live.DangerousGetBits().ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Preserves a stored OID domain argument so actual parameter assignment still enforces its current constraints.
    /// </summary>
    [PgFunction(Name = "create_from_sql")]
    public static uint CreateFromSql(long address, string sql) => Invoke<NativeOid>((nint)address, 0, sql).Value;

    /// <summary>
    /// Selects a live external domain-array contract without inventing a catalog declaration for its native target.
    /// </summary>
    [PgFunction(Name = "live")]
    public static string Live(long address, string sql)
    {
        PgArray<NativeLive?>? values = Invoke<PgArray<NativeLive?>?>((nint)address, 0, sql);
        return Describe(values, static item => item is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL");
    }

    /// <summary>
    /// Reports exact current element and array identity selected from a live result contract.
    /// </summary>
    [PgFunction(Name = "live_oid")]
    public static uint LiveOid() => NativeLiveConverter.LastOid;

    /// <summary>
    /// Reports constructor/read counters without touching native result storage.
    /// </summary>
    [PgFunction(Name = "counts")]
    public static string Counts() => string.Create(CultureInfo.InvariantCulture,
        $"{NativeOidConverter.Constructions}|{NativeOidConverter.Reads}|{NativeOidFactoryConverter.Constructions}|{NativeOidWriter.Constructions}");

    /// <summary>
    /// Reports exact array conversion counts, independently of returned cell values.
    /// </summary>
    [PgFunction(Name = "array_counts")]
    public static string ArrayCounts() => string.Create(CultureInfo.InvariantCulture,
        $"{ArrayValueConverter.Constructions}|{ArrayValueConverter.Reads}|{ArrayValueConverter.Writes}|{ArrayValueConverter.Inputs.Count}");

    /// <summary>
    /// Requires all deliberately retained reader inputs to have expired while preserving their nominal identities.
    /// </summary>
    [PgFunction(Name = "captures")]
    public static string Captures(int kind)
    {
        IEnumerable<PgDatum> values = kind switch
        {
            0 => [ResultIntConverter.Captured ?? throw new InvalidOperationException("No integer was captured.")],
            1 => [ResultTextConverter.Captured ?? throw new InvalidOperationException("No text was captured.")],
            2 => ArrayValueConverter.Inputs,
            3 => ArrayTextConverter.Inputs,
            _ => [NativeOidConverter.Captured ?? throw new InvalidOperationException("No OID was captured.")],
        };
        return string.Join(',', values.Select(static value =>
        {
            RequireExpired(value);
            return value.TypeOid.ToString(CultureInfo.InvariantCulture);
        }));
    }

    /// <summary>
    /// Copies input handles without consuming their owners and disposes every source before returning managed data.
    /// </summary>
    private static T Invoke<T>(nint address, uint collation, string sql)
    {
        PgDatum[] arguments;
        T value;
        using (SpiRawResult source = Spi.QueryRaw(sql))
        {
            arguments = [.. source[0]];
            value = CallChecked<T>(address, collation, arguments);
        }

        foreach (PgDatum argument in arguments)
        {
            RequireExpired(argument);
        }

        return value;
    }

    /// <summary>
    /// Checks preserved argument ownership outside cleanup and retains both errors if a call and invariant fail.
    /// </summary>
    private static T CallChecked<T>(nint address, uint collation, PgDatum[] arguments)
    {
        nuint[] original = [.. arguments.Select(static argument => argument.DangerousGetBits())];
        T value = default!;
        Exception? primary = null;
        try
        {
            unsafe
            {
                value = PgFunctions.DangerousCall<T>(address, collation, arguments);
            }
        }
        // codeql[cs/catch-of-all-exceptions] The caller's arguments are checked whatever the call threw, and that failure is rethrown afterwards.
        catch (Exception error)
        {
            primary = error;
        }

        try
        {
            for (int index = 0; index < arguments.Length; index++)
            {
                if (arguments[index].DangerousGetBits() != original[index])
                {
                    throw new InvalidOperationException("A native call changed its caller-owned argument handle.");
                }
            }
        }
        catch (Exception ownership)
        {
            if (primary is not null)
            {
                throw new AggregateException(primary, ownership);
            }

            throw;
        }

        if (primary is not null)
        {
            ExceptionDispatchInfo.Capture(primary).Throw();
        }

        return value;
    }

    /// <summary>
    /// Describes preserved dimensions independently of PostgreSQL's array output formatter.
    /// </summary>
    private static string Describe<T>(PgArray<T>? values, Func<T, string> format)
        => values is null ? "NULL" : string.Create(CultureInfo.InvariantCulture,
            $"{values.ElementTypeOid}|{values.Rank}|{values.Count}|{string.Join(',', values.Lengths.ToArray())}|{string.Join(',', values.LowerBounds.ToArray())}|{string.Join(',', values.Select(format))}");

    /// <summary>
    /// Tests checked invalidation without dereferencing freed native storage.
    /// </summary>
    private static void RequireExpired(PgDatum datum)
    {
        try
        {
            _ = datum.DangerousGetBits();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new InvalidOperationException("A temporary native owner remained live.");
    }
}
