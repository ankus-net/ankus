using System.Text.Json.Serialization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises detached function metadata and actual native default expressions in a published extension.
/// </summary>
[PgSchema("function_catalog")]
public static class FunctionCatalogFunctions
{
    private static PgFunctionInfo? s_saved;

    /// <summary>
    /// Returns every public metadata field through generated AOT JSON metadata.
    /// </summary>
    [PgFunction]
    public static PgJsonb? Describe(uint oid)
        => PgFunctions.GetInfo(oid) is { } info ? Serialize(info) : null;

    /// <summary>
    /// Retains a detached snapshot across subsequent DDL and backend callbacks.
    /// </summary>
    [PgFunction]
    public static bool Save(uint oid)
    {
        s_saved = PgFunctions.GetInfo(oid);
        return s_saved is not null;
    }

    /// <summary>
    /// Reads the retained metadata after its catalog row has changed or disappeared.
    /// </summary>
    [PgFunction]
    public static PgJsonb Saved() => Serialize(s_saved ?? throw new InvalidOperationException("No snapshot."));

    /// <summary>
    /// Parses fresh defaults, checks independent ownership and evaluates them in a selected-header native probe.
    /// </summary>
    [PgFunction]
    public static string?[]? Defaults(uint oid) => Evaluate(PgFunctions.GetInfo(oid)!);

    /// <summary>
    /// Parses captured defaults after the source catalog row has disappeared.
    /// </summary>
    [PgFunction]
    public static string?[]? SavedDefaults() => Evaluate(s_saved ?? throw new InvalidOperationException("No snapshot."));

    /// <summary>
    /// Catches a native expression error, runs finally and proves same-session SQL recovery.
    /// </summary>
    [PgFunction]
    public static string DefaultError(uint oid)
    {
        string state;
        bool cleaned;
        try
        {
            _ = Evaluate(PgFunctions.GetInfo(oid)!);
            state = "missing";
        }
        catch (PgException exception)
        {
            state = exception.SqlState;
        }
        finally
        {
            cleaned = true;
        }

        return $"{state}|{cleaned}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Uses explicit source-generated metadata without reflection or runtime code generation.
    /// </summary>
    private static PgJsonb Serialize(PgFunctionInfo info)
        => PgJsonb.Serialize(info, FunctionCatalogJsonContext.Default.PgFunctionInfo);

    /// <summary>
    /// Gives list containers and all raw nodes one explicit owner and checks reset invalidation before returning copies.
    /// </summary>
    private static unsafe string?[]? Evaluate(PgFunctionInfo info)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("catalog defaults");
        using PgList<nint>? first = info.GetDefaultArguments(owner);
        if (first is null)
        {
            return null;
        }

        using PgList<nint> second = info.GetDefaultArguments(owner)!;
        if (first.Count != info.DefaultArgumentCount || second.Count != first.Count ||
            first.DangerousGetPointer() == second.DangerousGetPointer() ||
            first.Zip(second).Any(pair => pair.First == pair.Second || pair.First == 0) ||
            first.LifetimeContext!.Name != "catalog defaults")
        {
            throw new InvalidOperationException("Default expression ownership mismatch.");
        }

        string?[] values = Spi.ExecuteScalar<string?[]>("SELECT tests.default_values($1)",
            SpiParameter.Create((long)first.DangerousGetPointer()));
        first.Dispose();
        string?[] copied = Spi.ExecuteScalar<string?[]>("SELECT tests.default_values($1)",
            SpiParameter.Create((long)second.DangerousGetPointer()));
        if (values.Length != copied.Length)
        {
            throw new InvalidOperationException("Default order mismatch.");
        }

        owner.Reset();
        try
        {
            _ = second.Count;
            throw new InvalidOperationException("Defaults survived owner reset.");
        }
        catch (ObjectDisposedException)
        {
            // Resetting the owner disposed the copied defaults.
        }

        return values;
    }
}

/// <summary>
/// Supplies finite JSON serialization metadata for detached catalog snapshots.
/// </summary>
[JsonSerializable(typeof(PgFunctionInfo))]
internal sealed partial class FunctionCatalogJsonContext : JsonSerializerContext;
