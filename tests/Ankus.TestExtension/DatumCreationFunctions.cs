using System.Diagnostics;

[assembly: PgSql("datum-creation", "CREATE SCHEMA datum_creation;", Requires = ["mapped-domains"])]

namespace Ankus.TestExtension;

/// <summary>
/// Builds raw datums from managed values, resolves enum labels by type and observes pending interrupts.
/// </summary>
[PgSchema("datum_creation", Create = false)]
public static class DatumCreationFunctions
{
    private static readonly int[] s_numbers = [1, 2];

    /// <summary>
    /// Converts managed values of several representations to datums, including SQL NULL and a domain.
    /// </summary>
    /// <returns>The datums' text, NULL flag, type identities and the domain's rejection.</returns>
    [PgFunction(Name = "create_values", Requires = ["datum-creation"])]
    public static string CreateValues()
    {
        PgMemoryContext context = PgMemoryContext.Current;
        PgDatum text = PgDatum.Create("héllo", context);
        PgDatum number = PgDatum.Create(PgNumeric.Parse("1.50"), context);
        PgDatum array = PgDatum.Create(s_numbers, context);
        PgDatum missing = PgDatum.Create<string?>(null, context);
        PgDatum positive = PgDatum.Create(new MappedPositive(5), context);
        PgDatum copy = PgDatum.Create(SpiParameter.Create(text), context);
        string rejected;
        try
        {
            _ = PgDatum.Create(new MappedPositive(-5), context);
            rejected = "none";
        }
        catch (PgException error)
        {
            rejected = error.SqlState;
        }

        return string.Join("|", text.ToPostgresString(), number.ToPostgresString(), array.ToPostgresString(), missing.IsNull,
            missing.TypeOid, positive.ToPostgresString(), positive.TypeOid == PgTypes.GetOid("datum_mappings.positive"),
            copy.ToPostgresString(), rejected, PgFunctions.Call<int>("pg_catalog.length", PgFunctionArgument.Create(text)));
    }

    /// <summary>
    /// Resolves an enum label to its pg_enum OID by the enum's type name.
    /// </summary>
    /// <param name="type">The enum type name.</param>
    /// <param name="label">The label.</param>
    /// <returns>The pg_enum row OID.</returns>
    [PgFunction(Name = "enum_value_oid", Requires = ["datum-creation"])]
    public static uint EnumValueOid(string type, string label) => PgEnums.GetValueOid(PgTypes.GetOid(type), label);

    /// <summary>
    /// Reports whether an interrupt is pending on entry and after waiting for one, such as a statement timeout, without
    /// entering PostgreSQL in between.
    /// </summary>
    /// <param name="wait">Whether to wait up to ten seconds for an interrupt.</param>
    /// <returns>The pending flags before and after.</returns>
    [PgFunction(Name = "interrupt_pending", Requires = ["datum-creation"])]
    public static string InterruptPending(bool wait)
    {
        bool before = PgInterrupts.IsPending;
        var waited = Stopwatch.StartNew();
        bool after = before;
        while (wait && !after && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            Thread.Sleep(10);
            after = PgInterrupts.IsPending;
        }

        PgLog.Notice($"pending {before} {after}");
        return $"{before}|{after}";
    }
}
