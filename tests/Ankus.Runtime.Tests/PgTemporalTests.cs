namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies exact .NET temporal conversions, valid boundaries, and rejection of lossy conversions.
/// </summary>
[TestClass]
public sealed class PgTemporalTests
{
    /// <summary>
    /// Checks value ordering can be used outside PostgreSQL, including extreme ranges and equal instants.
    /// </summary>
    [TestMethod]
    public void TemporalOrderingWorksWithoutBackendAccess()
    {
        PgDate[] dates = [PgDate.PositiveInfinity, new(-2451545), PgDate.NegativeInfinity, new(2145031948)];
        Array.Sort(dates);
        Assert.AreSequenceEqual([PgDate.NegativeInfinity, new(-2451545), new(2145031948), PgDate.PositiveInfinity], dates);
        Assert.IsTrue(PgDate.NegativeInfinity < new PgDate(-2451545));
        Assert.IsTrue(PgDate.PositiveInfinity > new PgDate(2145031948));
        Assert.IsTrue(default(PgDate) <= new PgDate(0));
        Assert.IsTrue(default(PgDate) >= new PgDate(0));

        Assert.IsGreaterThan(0, PgTime.EndOfDay.CompareTo(default));
        Assert.IsTrue(new PgTime(0) < PgTime.EndOfDay);
        Assert.IsTrue(PgTime.EndOfDay > new PgTime(0));
        Assert.IsTrue(default(PgTime) <= new PgTime(0));
        Assert.IsTrue(default(PgTime) >= new PgTime(0));

        Assert.IsLessThan(0, PgTimestamp.NegativeInfinity.CompareTo(new PgTimestamp(-211813488000000000)));
        Assert.IsTrue(new PgTimestamp(9223371331199999999) < PgTimestamp.PositiveInfinity);
        Assert.IsTrue(new PgTimestamp(0) > new PgTimestamp(-1));
        Assert.IsTrue(default(PgTimestamp) <= new PgTimestamp(0));
        Assert.IsTrue(default(PgTimestamp) >= new PgTimestamp(0));

        Assert.IsLessThan(0, PgTimestampTz.NegativeInfinity.CompareTo(new PgTimestampTz(-211813488000000000)));
        Assert.IsTrue(new PgTimestampTz(9223371331199999999) < PgTimestampTz.PositiveInfinity);
        Assert.IsTrue(new PgTimestampTz(0) > new PgTimestampTz(-1));
        Assert.IsTrue(default(PgTimestampTz) <= new PgTimestampTz(0));
        Assert.IsTrue(default(PgTimestampTz) >= new PgTimestampTz(0));
    }

    /// <summary>
    /// Checks timetz ordering retains the day boundary and breaks UTC-time ties by offset.
    /// </summary>
    [TestMethod]
    public void OffsetTimeOrderingMatchesPostgresTieBreaking()
    {
        var noonEast = new PgTimeTz(new PgTime(43_200_000_000), 7200);
        var tenUtc = new PgTimeTz(new PgTime(36_000_000_000), 0);
        Assert.IsLessThan(0, noonEast.CompareTo(tenUtc));
        Assert.IsTrue(noonEast < tenUtc);
        Assert.IsTrue(tenUtc > noonEast);
        Assert.IsTrue(noonEast <= new PgTimeTz(new PgTime(43_200_000_000), 7200));
        Assert.IsTrue(noonEast >= new PgTimeTz(new PgTime(43_200_000_000), 7200));
        Assert.AreNotEqual(noonEast, tenUtc);
        Assert.AreEqual(0, noonEast.CompareTo(new PgTimeTz(new PgTime(43_200_000_000), 7200)));
        Assert.IsGreaterThan(0, new PgTimeTz(PgTime.EndOfDay, 0).CompareTo(default));
        Assert.IsLessThan(0, new PgTimeTz(default, 57599).CompareTo(new PgTimeTz(PgTime.EndOfDay, -57599)));
    }

    /// <summary>
    /// Checks temporal input validation and ensures TryParse does not hide missing backend access.
    /// </summary>
    [TestMethod]
    public void TemporalParsingValidatesTextAndPreservesAccessErrors()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => PgDate.Parse(null!));
        Assert.ThrowsExactly<ArgumentException>(() => PgTime.Parse("12:00\0ignored"));
        Assert.ThrowsExactly<ArgumentException>(() => default(PgTimestamp).AtTimeZone("UTC\0ignored"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => default(PgInterval).GetPart((PgDateTimePart)(-1)));
        Assert.IsFalse(PgDate.TryParse(null, out PgDate date));
        Assert.AreEqual(default, date);
        Assert.IsFalse(PgTime.TryParse("\0", out PgTime time));
        Assert.AreEqual(default, time);
        Assert.IsFalse(PgTimeTz.TryParse(null, out PgTimeTz timeTz));
        Assert.AreEqual(default, timeTz);
        Assert.IsFalse(PgTimestamp.TryParse(null, out PgTimestamp timestamp));
        Assert.AreEqual(default, timestamp);
        Assert.IsFalse(PgTimestampTz.TryParse(null, out PgTimestampTz instant));
        Assert.AreEqual(default, instant);
        Assert.IsFalse(PgInterval.TryParse(null, out PgInterval interval));
        Assert.AreEqual(default, interval);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDate.TryParse("2024-01-01", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTime.TryParse("12:00", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTimeTz.TryParse("12:00+00", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTimestamp.TryParse("2024-01-01", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTimestampTz.TryParse("2024-01-01+00", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgInterval.TryParse("1 day", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(PgDate).AddDays(1));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(PgTime).ToPostgresString());
    }

    /// <summary>
    /// Checks date conversion independently at the epoch, leap day, and .NET boundaries.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <param name="offset">The PostgreSQL day offset.</param>
    [TestMethod]
    [DataRow(1, 1, 1, -730119)]
    [DataRow(1999, 12, 31, -1)]
    [DataRow(2000, 1, 1, 0)]
    [DataRow(2000, 2, 29, 59)]
    [DataRow(9999, 12, 31, 2921939)]
    public void DateOnlyUsesPostgresEpoch(int year, int month, int day, int offset)
    {
        var value = new DateOnly(year, month, day);
        Assert.AreEqual(offset, PgDate.FromDateOnly(value).DaysSinceEpoch);
        Assert.AreEqual(value, new PgDate(offset).ToDateOnly());
        Assert.IsTrue(PgDate.FromDateOnly(value).IsFinite);
    }

    /// <summary>
    /// Checks inclusive/exclusive PostgreSQL date range boundaries without relying on DateOnly's narrow range.
    /// </summary>
    [TestMethod]
    public void DateRangeRetainsFiniteExtremesAndInfinities()
    {
        Assert.IsTrue(new PgDate(-2451545).IsFinite);
        Assert.IsTrue(new PgDate(2145031948).IsFinite);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgDate(-2451546));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgDate(2145031949));
        Assert.IsFalse(PgDate.PositiveInfinity.IsFinite);
        Assert.IsFalse(PgDate.NegativeInfinity.IsFinite);
        Assert.AreEqual(int.MaxValue, PgDate.PositiveInfinity.DaysSinceEpoch);
        Assert.AreEqual(int.MinValue, PgDate.NegativeInfinity.DaysSinceEpoch);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDate.PositiveInfinity.ToDateOnly());
        Assert.ThrowsExactly<InvalidOperationException>(() => PgDate.NegativeInfinity.ToDateOnly());
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgDate(-730120).ToDateOnly());
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgDate(2921940).ToDateOnly());
    }

    /// <summary>
    /// Checks midnight, microsecond precision, final instant, and PostgreSQL's distinct end-of-day value.
    /// </summary>
    [TestMethod]
    public void TimeOnlyRejectsEndOfDayAndSubMicroseconds()
    {
        Assert.AreEqual(TimeOnly.MinValue, default(PgTime).ToTimeOnly());
        Assert.AreEqual(1L, PgTime.FromTimeOnly(new TimeOnly(10)).Microseconds);
        Assert.AreEqual(new TimeOnly(863999999990), new PgTime(86399999999).ToTimeOnly());
        Assert.AreEqual(86400000000L, PgTime.EndOfDay.Microseconds);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTime.EndOfDay.ToTimeOnly());
        Assert.ThrowsExactly<ArgumentException>(() => PgTime.FromTimeOnly(new TimeOnly(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTime(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTime(86400000001));
    }

    /// <summary>
    /// Checks second-resolution offsets, including PostgreSQL's wider range than DateTimeOffset.
    /// </summary>
    /// <param name="seconds">The offset east of UTC.</param>
    [TestMethod]
    [DataRow(-57599)]
    [DataRow(-19817)]
    [DataRow(0)]
    [DataRow(19817)]
    [DataRow(57599)]
    public void TimeZoneOffsetsKeepDirectionAndSeconds(int seconds)
    {
        var value = new PgTimeTz(PgTime.EndOfDay, seconds);
        Assert.AreEqual(seconds, value.OffsetSeconds);
        Assert.AreEqual(TimeSpan.FromSeconds(seconds), value.Offset);
        Assert.AreEqual(PgTime.EndOfDay, value.Time);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTimeTz(default, -57600));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTimeTz(default, 57600));
    }

    /// <summary>
    /// Checks pre-epoch precision and prevents implicit timezone conversion for wall-clock timestamps.
    /// </summary>
    [TestMethod]
    public void TimestampConversionPreservesKindAndMicroseconds()
    {
        var epoch = new DateTime(2000, 1, 1);
        Assert.AreEqual(epoch, default(PgTimestamp).ToDateTime());
        Assert.AreEqual(-1L, PgTimestamp.FromDateTime(epoch.AddTicks(-10)).MicrosecondsSinceEpoch);
        Assert.AreEqual(epoch.AddTicks(-10), new PgTimestamp(-1).ToDateTime());
        Assert.AreEqual(DateTimeKind.Unspecified, new PgTimestamp(-1).ToDateTime().Kind);
        Assert.ThrowsExactly<ArgumentException>(() => PgTimestamp.FromDateTime(DateTime.SpecifyKind(epoch, DateTimeKind.Utc)));
        Assert.ThrowsExactly<ArgumentException>(() => PgTimestamp.FromDateTime(DateTime.SpecifyKind(epoch, DateTimeKind.Local)));
        Assert.ThrowsExactly<ArgumentException>(() => PgTimestamp.FromDateTime(epoch.AddTicks(-1)));
        Assert.ThrowsExactly<ArgumentException>(() => PgTimestamp.FromDateTime(epoch.AddTicks(1)));
    }

    /// <summary>
    /// Checks full PostgreSQL timestamp boundaries and infinity while .NET conversions refuse unrepresentable values.
    /// </summary>
    /// <param name="offset">The finite timestamp offset.</param>
    [TestMethod]
    [DataRow(-211813488000000000L)]
    [DataRow(9223371331199999999L)]
    public void TimestampRangePreservesValuesBeyondDotnet(long offset)
    {
        Assert.IsTrue(new PgTimestamp(offset).IsFinite);
        Assert.IsTrue(new PgTimestampTz(offset).IsFinite);
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgTimestamp(offset).ToDateTime());
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgTimestampTz(offset).ToDateTimeOffset());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTimestamp(-211813488000000001L));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTimestamp(9223371331200000000L));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTimestampTz(-211813488000000001L));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgTimestampTz(9223371331200000000L));
        Assert.IsFalse(PgTimestamp.PositiveInfinity.IsFinite);
        Assert.IsFalse(PgTimestamp.NegativeInfinity.IsFinite);
        Assert.IsFalse(PgTimestampTz.PositiveInfinity.IsFinite);
        Assert.IsFalse(PgTimestampTz.NegativeInfinity.IsFinite);
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTimestamp.PositiveInfinity.ToDateTime());
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTimestamp.NegativeInfinity.ToDateTime());
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTimestampTz.PositiveInfinity.ToDateTimeOffset());
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTimestampTz.NegativeInfinity.ToDateTimeOffset());
    }

    /// <summary>
    /// Checks UTC normalization without server/local timezone dependence and without sentinel substitution.
    /// </summary>
    [TestMethod]
    public void DateTimeOffsetNormalizesToUtcExactly()
    {
        var local = new DateTimeOffset(2000, 1, 1, 5, 30, 0, TimeSpan.FromMinutes(330));
        Assert.AreEqual(0L, PgTimestampTz.FromDateTimeOffset(local).MicrosecondsSinceEpoch);
        DateTimeOffset converted = new PgTimestampTz(1).ToDateTimeOffset();
        Assert.AreEqual(local.UtcTicks + 10, converted.Ticks);
        Assert.AreEqual(TimeSpan.Zero, converted.Offset);
        Assert.ThrowsExactly<ArgumentException>(() => PgTimestampTz.FromDateTimeOffset(local.AddTicks(1)));
        Assert.AreEqual(DateTimeOffset.MinValue, PgTimestampTz.FromDateTimeOffset(DateTimeOffset.MinValue).ToDateTimeOffset());
        DateTimeOffset maximum = DateTimeOffset.MaxValue.AddTicks(-9);
        Assert.IsTrue(PgTimestampTz.FromDateTimeOffset(maximum).IsFinite);
        Assert.AreEqual(maximum, PgTimestampTz.FromDateTimeOffset(maximum).ToDateTimeOffset());
    }

    /// <summary>
    /// Checks interval component identity and refusal to approximate calendar units as elapsed time.
    /// </summary>
    [TestMethod]
    public void IntervalKeepsCalendarComponentsAndExplicitInfinity()
    {
        var mixed = new PgInterval(1, -2, 3);
        Assert.AreEqual(1, mixed.Months);
        Assert.AreEqual(-2, mixed.Days);
        Assert.AreEqual(3L, mixed.Microseconds);
        var month = new PgInterval(1, 0, 0);
        var days = new PgInterval(0, 30, 0);
        Assert.AreEqual(days, month);
        Assert.AreEqual((1, 0, 0L), (month.Months, month.Days, month.Microseconds));
        Assert.AreEqual((0, 30, 0L), (days.Months, days.Days, days.Microseconds));
        Assert.IsTrue(mixed.IsFinite);
        Assert.IsFalse(PgInterval.PositiveInfinity.IsFinite);
        Assert.IsFalse(PgInterval.NegativeInfinity.IsFinite);
        Assert.AreNotEqual(new PgInterval(int.MaxValue, int.MaxValue, long.MaxValue), PgInterval.PositiveInfinity);
        Assert.AreNotEqual(new PgInterval(int.MinValue, int.MinValue, long.MinValue), PgInterval.NegativeInfinity);
        Assert.ThrowsExactly<InvalidOperationException>(() => mixed.ToTimeSpan());
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgInterval(0, 1, 0).ToTimeSpan());
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgInterval(1, 0, 0).ToTimeSpan());
        Assert.ThrowsExactly<InvalidOperationException>(() => PgInterval.PositiveInfinity.ToTimeSpan());
        Assert.ThrowsExactly<InvalidOperationException>(() => PgInterval.NegativeInfinity.ToTimeSpan());
    }

    /// <summary>
    /// Checks signed elapsed microseconds without introducing a calendar-day component.
    /// </summary>
    /// <param name="ticks">The exact elapsed .NET ticks.</param>
    [TestMethod]
    [DataRow(0L)]
    [DataRow(-10L)]
    [DataRow(1764000000010L)]
    [DataRow(9223372036854775800L)]
    [DataRow(-9223372036854775800L)]
    public void TimeSpanConversionIsExactAndNeverNormalizesDays(long ticks)
    {
        PgInterval value = PgInterval.FromTimeSpan(new TimeSpan(ticks));
        Assert.AreEqual(0, value.Months);
        Assert.AreEqual(0, value.Days);
        Assert.AreEqual(ticks / 10, value.Microseconds);
        Assert.AreEqual(ticks, value.ToTimeSpan().Ticks);
        Assert.ThrowsExactly<ArgumentException>(() => PgInterval.FromTimeSpan(new TimeSpan(1)));
        Assert.ThrowsExactly<ArgumentException>(() => PgInterval.FromTimeSpan(new TimeSpan(-1)));
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(0, 0, long.MaxValue).ToTimeSpan());
        Assert.ThrowsExactly<OverflowException>(() => new PgInterval(0, 0, long.MinValue).ToTimeSpan());
    }

    /// <summary>
    /// Checks nullable typed row conversions in both directions and prevents conversion between unrelated temporal types.
    /// </summary>
    [TestMethod]
    public void RowConversionsAreExactAndPreserveNullSemantics()
    {
        var row = new SpiRow([default(PgDate), default(PgTime), default(PgTimestamp), default(PgTimestampTz), default(PgInterval)],
            [new("d", 1082), new("t", 1083), new("s", 1114), new("z", 1184), new("i", 1186)]);
        Assert.AreEqual(new DateOnly(2000, 1, 1), row.Get<DateOnly?>(0));
        Assert.AreEqual(TimeOnly.MinValue, row.Get<TimeOnly?>(1));
        Assert.AreEqual(new DateTime(2000, 1, 1), row.Get<DateTime?>(2));
        Assert.AreEqual(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), row.Get<DateTimeOffset?>(3));
        Assert.AreEqual(TimeSpan.Zero, row.Get<TimeSpan?>(4));
        row.Set(0, new DateOnly(1999, 12, 31));
        row.Set(1, new TimeOnly(10));
        row.Set(2, new DateTime(2000, 1, 1).AddTicks(-10));
        row.Set(3, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(10));
        row.Set(4, new TimeSpan(-10));
        Assert.AreEqual(new PgDate(-1), row.Get<PgDate?>(0));
        Assert.AreEqual(new PgTime(1), row.Get<PgTime?>(1));
        Assert.AreEqual(new PgTimestamp(-1), row.Get<PgTimestamp?>(2));
        Assert.AreEqual(new PgTimestampTz(1), row.Get<PgTimestampTz?>(3));
        Assert.AreEqual(new PgInterval(0, 0, -1), row.Get<PgInterval?>(4));
        Assert.AreEqual(1082U, row.GetTypeOid(0));
        Assert.AreEqual(1083U, row.GetTypeOid(1));
        Assert.AreEqual(1114U, row.GetTypeOid(2));
        Assert.AreEqual(1184U, row.GetTypeOid(3));
        Assert.AreEqual(1186U, row.GetTypeOid(4));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<DateTime>(0));
        row.Set<DateTime?>(2, null);
        Assert.IsNull(row.Get<PgTimestamp?>(2));
        Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<PgTimestamp>(2));
        row.Set(0, PgDate.PositiveInfinity);
        Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<DateOnly>(0));
    }
}
