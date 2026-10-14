namespace Ankus;

/// <summary>
/// Validates a call site's native owner and coordinates rooted managed state until native reset.
/// </summary>
/// <param name="provider">The native extension provider.</param>
/// <param name="identity">The monotonic native call-site identity.</param>
/// <param name="owner">The function cache's registered memory context.</param>
/// <param name="generation">The owner's generation when the call site was captured.</param>
internal sealed class PgFunctionStateScope(nint provider, nint identity, nint owner, nuint generation)
{
    [ThreadStatic]
    private static Dictionary<(nint Provider, nint Identity), PgFunctionStateEntry>? s_states;

    /// <summary>
    /// Gets a fresh borrowed owner handle after checking provider and generation.
    /// </summary>
    /// <returns>The live function-cache memory context.</returns>
    internal PgMemoryContext GetMemoryContext()
    {
        NativeMemoryContext.CheckProvider(provider);
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.CaptureGeneration,
            _context = owner
        };
        try
        {
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            if (unchecked((nuint)result._value) != generation)
            {
                throw new ObjectDisposedException(nameof(PgFunctionContext), "The function state owner has been reset.");
            }
        }
        catch (PgException exception) when (exception.SqlState == "55000")
        {
            throw new ObjectDisposedException(nameof(PgFunctionContext), "The function state owner has been deleted.");
        }

        return PgMemoryContext.FromId(provider, owner);
    }

    /// <summary>
    /// Gets this thread's function states, creating the map on first use.
    /// </summary>
    private static Dictionary<(nint Provider, nint Identity), PgFunctionStateEntry> States() => s_states ??= [];

    /// <summary>
    /// Roots a state entry before user code runs and reuses it until its PostgreSQL owner resets.
    /// </summary>
    /// <typeparam name="T">The exact cached type.</typeparam>
    /// <param name="factory">The synchronous initializer.</param>
    /// <returns>The cached value.</returns>
    internal T GetOrCreate<T>(Func<T> factory)
    {
        PgMemoryContext memory = GetMemoryContext();
        (nint Provider, nint Identity) key = (provider, identity);
        Dictionary<(nint Provider, nint Identity), PgFunctionStateEntry> states = States();
        if (!states.TryGetValue(key, out PgFunctionStateEntry? entry))
        {
            NativeBackend.CheckAccess();
            entry = new PgFunctionStateEntry();
            states.Add(key, entry);
            try
            {
                memory.RegisterResetCallback(() =>
                {
                    try
                    {
                        entry.Release();
                    }
                    finally
                    {
                        states.Remove(key);
                    }
                });
            }
            catch
            {
                states.Remove(key);
                throw;
            }
        }

        if (!entry.IsInitialized)
        {
            NativeBackend.CheckAccess();
        }

        return entry.GetOrCreate(factory);
    }
}
