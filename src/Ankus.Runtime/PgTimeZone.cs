using System;

namespace Ankus;

/// <summary>
/// Resolves PostgreSQL timezone names and abbreviations using the backend's timezone database.
/// </summary>
public static class PgTimeZone
{
    /// <summary>
    /// Gets the offset east of UTC for a named zone at the current transaction's start instant.
    /// </summary>
    /// <param name="zone">A PostgreSQL timezone name, abbreviation, or POSIX timezone specification.</param>
    /// <returns>The offset, including historical seconds where applicable.</returns>
    /// <remarks>
    /// Requires the active backend thread. Fixed abbreviations retain their fixed offsets;
    /// dynamic abbreviations and full zone names resolve at transaction start.
    /// </remarks>
    public static TimeSpan GetOffset(string zone)
        => TimeSpan.FromSeconds(PgTemporal.Call<int>(TemporalOperation.TimeZoneOffset, PgTemporal.Text(zone)));

    /// <summary>
    /// Gets the offset east of UTC for a named zone at a particular finite instant.
    /// </summary>
    /// <param name="zone">A PostgreSQL timezone name, abbreviation, or POSIX timezone specification.</param>
    /// <param name="instant">The instant at which to resolve historical and daylight-saving rules.</param>
    /// <returns>The offset, including historical seconds where applicable.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The instant is infinite.</exception>
    /// <remarks>
    /// Requires the active backend thread and leaves the session timezone unchanged.
    /// PostgreSQL's native timezone routines determine behavior across its complete timestamp range.
    /// </remarks>
    public static TimeSpan GetOffset(string zone, PgTimestampTz instant)
    {
        SpiParameter name = PgTemporal.Text(zone);
        if (!instant.IsFinite)
        {
            throw new ArgumentOutOfRangeException(nameof(instant), "A timezone offset requires a finite instant.");
        }

        return TimeSpan.FromSeconds(PgTemporal.Call<int>(TemporalOperation.TimeZoneOffset, name, SpiParameter.Create(instant)));
    }
}
