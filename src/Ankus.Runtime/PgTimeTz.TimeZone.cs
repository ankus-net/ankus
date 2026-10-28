using System;

namespace Ankus;

public readonly partial record struct PgTimeTz
{
    /// <summary>
    /// Constructs a local time with a named zone's offset at the current transaction's start instant.
    /// </summary>
    /// <param name="hour">The local hour, including 24 only for the end-of-day value.</param>
    /// <param name="minute">The local minute.</param>
    /// <param name="second">The fractional seconds, rounded by PostgreSQL.</param>
    /// <param name="zone">A PostgreSQL timezone name, abbreviation, or POSIX specification.</param>
    /// <returns>The requested local clock with the resolved fixed offset.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The resolved offset is not strictly between -57600 and 57600 seconds.</exception>
    /// <remarks>
    /// The supplied clock fields are retained. The zone supplies only the offset, which follows
    /// transaction-start daylight-saving rules because this type stores no date.
    /// </remarks>
    public static PgTimeTz Create(int hour, int minute, double second, string zone)
    {
        _ = PgTemporal.Text(zone);
        PgTime time = PgTime.Create(hour, minute, second);
        TimeSpan offset = PgTimeZone.GetOffset(zone);
        return new PgTimeTz(time, checked((int)offset.TotalSeconds));
    }

    /// <summary>
    /// Shifts this time to a fixed interval offset using PostgreSQL's timezone interval rules.
    /// </summary>
    /// <param name="offset">A finite interval without months or days; sub-second precision follows PostgreSQL.</param>
    /// <returns>The shifted wall-clock time and fixed offset.</returns>
    /// <remarks>
    /// Positive intervals mean east of UTC. Conversion requires the active backend thread.
    /// An offset outside this type's supported range is rejected when the result is read.
    /// </remarks>
    public PgTimeTz AtTimeZone(PgInterval offset)
        => PgTemporal.Call<PgTimeTz>(TemporalOperation.AtTimeZone, SpiParameter.Create(offset), SpiParameter.Create(this));
}
