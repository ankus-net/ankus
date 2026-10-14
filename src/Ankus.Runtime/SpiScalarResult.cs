namespace Ankus;

/// <summary>
/// Copies requested first-row values and releases temporary raw storage after conversion.
/// </summary>
/// <param name="managed">The ordinary materialized result, when no mapped or polymorphic values are requested.</param>
/// <param name="raw">The raw result for mapped, mixed or polymorphic result types.</param>
/// <param name="conversions">The provisional native owners during a multi-column conversion.</param>
internal readonly struct SpiScalarResult(SpiResult? managed, SpiRawResult? raw, SpiConversionScope? conversions = null) : IDisposable
{
    /// <summary>
    /// Validates read capability before SQL execution and selects raw mapped or polymorphic transport.
    /// </summary>
    internal static bool RequiresRaw<T>()
    {
        if (PgDatumRegistry.Find(typeof(T)) is { } mapping)
        {
            mapping.RequireRead();
            return true;
        }

        if (PgDatumRegistry.FindArray(typeof(T)) is { } array)
        {
            array.RequireRead();
            return true;
        }

        PgDatumRegistry.RejectOrdinaryResult<T>();
        if (PgArrayViews.Find(typeof(T)) is { } view)
        {
            view.RequireRead();
            return true;
        }

        return PgPolymorphic.Is<T>() || PgBufferViews.Is<T>();
    }

    /// <summary>
    /// Validates both requested types before SQL execution and selects raw transport when either needs it.
    /// </summary>
    internal static bool RequiresRaw<TFirst, TSecond>()
    {
        bool first = RequiresRaw<TFirst>();
        bool second = RequiresRaw<TSecond>();
        return first || second;
    }

    /// <summary>
    /// Validates all three requested types before SQL execution and selects raw transport when any needs it.
    /// </summary>
    internal static bool RequiresRaw<TFirst, TSecond, TThird>()
    {
        bool first = RequiresRaw<TFirst>();
        bool second = RequiresRaw<TSecond>();
        bool third = RequiresRaw<TThird>();
        return first || second || third;
    }

    /// <summary>
    /// Converts selected cells and releases their temporary owner, preserving both failures if necessary.
    /// </summary>
    /// <typeparam name="T">The copied scalar or tuple type.</typeparam>
    /// <param name="read">The synchronous result-only converter.</param>
    /// <returns>The independent result or callback-owned polymorphic values.</returns>
    internal T Read<T>(Func<SpiScalarResult, T> read)
    {
        SpiConversionScope? ownership = null;
        T result;
        try
        {
            ownership = new SpiConversionScope();
            result = read(new SpiScalarResult(managed, raw, ownership));
        }
        catch (Exception primary)
        {
            try
            {
                PgResultCleanup.Dispose(raw, primary);
            }
            finally
            {
                ownership?.ReleaseAfterFailure(primary);
            }

            throw;
        }

        try
        {
            PgResultCleanup.Dispose(raw, null);
        }
        catch (Exception cleanup)
        {
            ownership.ReleaseAfterFailure(cleanup);
            throw;
        }

        ownership.Relinquish();
        return result;
    }

    /// <summary>
    /// Executes SQL using the result representation required by the requested scalar types.
    /// </summary>
    /// <param name="command">The SQL commands.</param>
    /// <param name="parameters">The positional values.</param>
    /// <param name="mode">The first-row column selection.</param>
    /// <param name="polymorphic">Whether any requested column retains its actual native type.</param>
    /// <param name="session">The optional current SPI session.</param>
    /// <returns>The temporary conversion owner.</returns>
    internal static SpiScalarResult Run(string command, ReadOnlySpan<SpiParameter> parameters,
        SpiResultMode mode, bool polymorphic, SpiSession? session = null)
        => polymorphic
            ? new(null, NativeBackend.RunRaw(command, parameters, false, 0, session, mode))
            : new(NativeBackend.Run(command, parameters, false, 0, mode, session), null);

    /// <summary>
    /// Reads a value, copying polymorphic native storage into the callback owner before temporary storage is freed.
    /// </summary>
    /// <typeparam name="T">The requested representation.</typeparam>
    /// <param name="ordinal">The selected column.</param>
    /// <param name="allowMissing">Whether a zero-column scalar result is treated as SQL NULL.</param>
    /// <returns>The copied value, or null for SQL NULL or an empty result.</returns>
    internal T Get<T>(int ordinal, bool allowMissing = false)
    {
        if (raw is null)
        {
            T result = allowMissing && managed!.Columns.Count == 0
                ? SpiRow.Convert<T>(null)
                : managed!.GetFirstValue<T>(ordinal);
            return conversions is null ? result : conversions.Add(result);
        }

        if (raw.Count == 0 || (allowMissing && raw.Columns.Count == 0))
        {
            return PgDatumRegistry.FindArray(typeof(T)) is not null ? default! : raw.Count == 0 ? SpiRow.ConvertMissing<T>() : SpiRow.Convert<T>(null);
        }

        if (ordinal >= raw.Columns.Count)
        {
            throw new InvalidOperationException($"The SPI result has {raw.Columns.Count} columns; column {ordinal + 1} was requested.");
        }

        PgDatum value = raw[0][ordinal];
        if ((PgPolymorphic.Is<T>() || PgBufferViews.Is<T>() || PgArrayViews.Find(typeof(T)) is not null) && !value.IsNull)
        {
            value = value.CopyTo(PgMemoryContext.Callback);
        }

        T converted = value.Read<T>();
        return conversions is null ? converted : conversions.Add(converted);
    }

    /// <summary>
    /// Deletes temporary native rows after requested values have acquired their final owner.
    /// </summary>
    public void Dispose() => raw?.Dispose();
}
