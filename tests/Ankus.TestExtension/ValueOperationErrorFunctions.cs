namespace Ankus.TestExtension;

public static partial class NativeRawCallFunctions
{
    /// <summary>
    /// Attempts to swallow or replace a direct value error before optional deliberate rollback.
    /// </summary>
    /// <param name="family">The native value family whose input routine fails.</param>
    /// <param name="mode">Zero for direct invocation, one or two for nested SPI sessions, or three for explicit recovery.</param>
    /// <param name="replace">Whether an unrelated managed exception attempts to replace the native failure.</param>
    /// <returns>Forty-two only after an explicit recovery scope finishes.</returns>
    [PgFunction]
    public static int ValueOperationErrorCaught(string family, int mode, bool replace)
    {
        int Fail() => CatchUnrecovered(() => InvokeValueFailure(family), replace);
        return mode switch
        {
            0 => Fail(),
            1 => Spi.Connect(_ => Fail()),
            2 => Spi.Connect(_ => Spi.Connect(_ => Fail())),
            3 => RecoverUnrecovered(Fail),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    /// <summary>
    /// Invokes an actual PostgreSQL input error without an implicit recovering SPI call.
    /// </summary>
    /// <param name="family">The selected native input routine.</param>
    private static void InvokeValueFailure(string family)
    {
        Action call = family switch
        {
            "numeric" => () => PgNumeric.Parse("not a number"),
            "temporal" => () => PgDate.Parse("not a date"),
            "network" => () => PgInet.Parse("256.0.0.1"),
            "geometry" => () => PgPoint.Parse("not a point"),
            "range" => () => PgRange.Parse<int>("[5,2)"),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        call();
    }
}
