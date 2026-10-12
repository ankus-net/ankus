using System.Globalization;
using System.Text;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises configuration storage, hooks, source priority, and owned hook data in the backend.
/// </summary>
public static partial class GucFunctions
{
    private static readonly List<string> s_events = [];
    private static readonly List<PgGucExtra> s_retained = [];
    private static string? s_retainedText;

    /// <summary>
    /// Catches configuration API preconditions without recovering a PostgreSQL transaction.
    /// </summary>
    /// <param name="mode">The missing name or incompatible typed reader to exercise.</param>
    /// <returns>The original diagnostic and subsequent successful getter/query results.</returns>
    [PgFunction]
    public static string GucValidationRecovery(int mode)
    {
        try
        {
            switch (mode)
            {
                case 0:
                    _ = NativeGuc.ReadInt32("ankus_guc.missing");
                    break;
                case 1:
                    _ = NativeGuc.ReadBoolean("ankus_guc.limit");
                    break;
                case 2:
                    _ = NativeGuc.ReadInt32("ankus_guc.enabled");
                    break;
                case 3:
                    _ = NativeGuc.ReadString("ankus_guc.limit");
                    break;
                case 4:
                    _ = NativeGuc.ReadEnum("ankus_guc.limit");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
        catch (PgException error)
        {
            PgInterrupts.Check();
            return string.Create(CultureInfo.InvariantCulture,
                $"{error.SqlState}|{error.Message}|{Limit}|{Spi.ExecuteScalar<int>("SELECT 42")}");
        }

        throw new InvalidOperationException("The invalid configuration read unexpectedly succeeded.");
    }

    /// <summary>
    /// Reads scalar settings through their located native variables across a change, then inside a memory reset
    /// callback, where configuration reads are unavailable whether or not a setting was located first.
    /// </summary>
    /// <returns>The values before and after <c>SET</c>, each callback read's outcome, and the value afterwards.</returns>
    [PgFunction]
    public static string GucLocatedReads()
    {
        int before = Limit;
        Spi.Execute("SET \"ankus_guc.limit\" = '31'");
        int changed = Limit;
        string located = "none";
        string unlocated = "none";
        using (PgMemoryContext owner = PgMemoryContext.Create("located configuration reads"))
        {
            using PgMemoryCallback callback = owner.RegisterResetCallback(() =>
            {
                located = Attempt(() => Limit);
                unlocated = Attempt(() => Ratio);
            });
            owner.Reset();
        }

        return string.Create(CultureInfo.InvariantCulture, $"{before}|{changed}|{located}|{unlocated}|{Limit}");

        static string Attempt<T>(Func<T> read)
        {
            try
            {
                return string.Create(CultureInfo.InvariantCulture, $"read {read()}");
            }
            catch (InvalidOperationException error)
            {
                return error.Message;
            }
        }
    }

    /// <summary>
    /// Gets the mode used to exercise individual hook contracts.
    /// </summary>
    [PgGucString("ankus_guc.control", "", "Hook test mode")]
    public static partial string Control { get; }

    /// <summary>
    /// Gets an ordinary Boolean setting.
    /// </summary>
    [PgGucBool("ankus_guc.enabled", true, "Whether the example is enabled", LongDescription = "A retained description with café.")]
    public static partial bool Enabled { get; }

    /// <summary>
    /// Gets an ordinary bounded integer setting.
    /// </summary>
    [PgGucInt("ankus_guc.limit", 10, "Example limit", Minimum = -50, Maximum = 100)]
    public static partial int Limit { get; }

    /// <summary>
    /// Gets an ordinary double setting.
    /// </summary>
    [PgGucReal("ankus_guc.ratio", -0.0, "Example ratio", Minimum = -100, Maximum = 100)]
    public static partial double Ratio { get; }

    /// <summary>
    /// Gets a nullable string setting.
    /// </summary>
    [PgGucString("ankus_guc.text", null, "Optional example text")]
    public static partial string? Text { get; }

    /// <summary>
    /// Gets a wide enumeration setting whose aliases share a native ordinal.
    /// </summary>
    [PgGucEnum("ankus_guc.mode", GucMode.Idle, "Example mode")]
    public static partial GucMode Mode { get; }

    /// <summary>
    /// Gets a Boolean with all three managed hooks.
    /// </summary>
    [PgGucBool("ankus_guc.hook_bool", false, "Hooked Boolean", Check = nameof(CheckBoolean), Assign = nameof(AssignBoolean), Show = nameof(ShowBoolean))]
    public static partial bool HookBoolean { get; }

    /// <summary>
    /// Gets an integer with normalization, diagnostics, and owned extra data.
    /// </summary>
    [PgGucInt("ankus_guc.hook_int", 10, "Hooked integer", Minimum = -50, Maximum = 100,
        Check = nameof(CheckInteger), Assign = nameof(AssignInteger), Show = nameof(ShowInteger))]
    public static partial int HookInteger { get; }

    /// <summary>
    /// Gets a double with all three managed hooks.
    /// </summary>
    [PgGucReal("ankus_guc.hook_real", 1.25, "Hooked real", Check = nameof(CheckReal), Assign = nameof(AssignReal), Show = nameof(ShowReal))]
    public static partial double HookReal { get; }

    /// <summary>
    /// Gets a nullable string with all three managed hooks.
    /// </summary>
    [PgGucString("ankus_guc.hook_text", null, "Hooked text", Check = nameof(CheckText), Assign = nameof(AssignText), Show = nameof(ShowText))]
    public static partial string? HookText { get; }

    /// <summary>
    /// Gets an enum with all three managed hooks.
    /// </summary>
    [PgGucEnum("ankus_guc.hook_mode", GucMode.Idle, "Hooked mode", Check = nameof(CheckMode), Assign = nameof(AssignMode), Show = nameof(ShowMode))]
    public static partial GucMode HookMode { get; }

    /// <summary>
    /// Gets a setting omitted from SHOW ALL.
    /// </summary>
    [PgGucInt("ankus_guc.hidden", 1, "Hidden setting", Flags = PgGucOptions.NoShowAll)]
    public static partial int Hidden { get; }

    /// <summary>
    /// Gets a setting retained by RESET ALL.
    /// </summary>
    [PgGucInt("ankus_guc.retained", 1, "Retained setting", Flags = PgGucOptions.NoResetAll)]
    public static partial int Retained { get; }

    /// <summary>
    /// Gets an identifier-sized string.
    /// </summary>
    [PgGucString("ankus_guc.identifier", "", "Identifier setting", Flags = PgGucOptions.IsName)]
    public static partial string Identifier { get; }

    /// <summary>
    /// Gets a setting that PostgreSQL reports through ParameterStatus.
    /// </summary>
    [PgGucInt("ankus_guc.reported", 1, "Reported setting", Flags = PgGucOptions.Report, Show = nameof(ShowReported))]
    public static partial int Reported { get; }

    /// <summary>
    /// Gets a setting requiring native SET privilege.
    /// </summary>
    [PgGucInt("ankus_guc.privileged", 1, "Privileged setting", Context = PgGucContext.SuperuserSet)]
    public static partial int Privileged { get; }

    /// <summary>
    /// Gets a setting with restricted visibility.
    /// </summary>
    [PgGucInt("ankus_guc.secret", 1, "Secret setting", Flags = PgGucOptions.SuperuserOnly)]
    public static partial int Secret { get; }

    /// <summary>
    /// Gets a setting forbidden in security-definer execution.
    /// </summary>
    [PgGucInt("ankus_guc.restricted", 1, "Security restricted setting", Flags = PgGucOptions.NotWhileSecurityRestricted)]
    public static partial int Restricted { get; }

    /// <summary>
    /// Gets a setting forbidden in ALTER SYSTEM.
    /// </summary>
    [PgGucInt("ankus_guc.no_auto", 1, "No automatic file", Flags = PgGucOptions.DisallowInAutoFile)]
    public static partial int NoAutoFile { get; }

    /// <summary>
    /// Gets a setting included in EXPLAIN SETTINGS.
    /// </summary>
    [PgGucInt("ankus_guc.explain", 1, "Explain setting", Flags = PgGucOptions.Explain)]
    public static partial int Explain { get; }

    /// <summary>
    /// Reads native storage independently of display hooks.
    /// </summary>
    /// <returns>An invariant snapshot of all five ordinary setting types.</returns>
    [PgFunction]
    public static string GucValues() => string.Create(CultureInfo.InvariantCulture,
        $"{Enabled}|{Limit}|{BitConverter.DoubleToInt64Bits(Ratio)}|{Text ?? "<null>"}|{(ulong)Mode}");

    /// <summary>
    /// Reads all five hook-backed native values without calling show hooks.
    /// </summary>
    /// <returns>The current typed values.</returns>
    [PgFunction]
    public static string GucHookValues() => string.Create(CultureInfo.InvariantCulture,
        $"{HookBoolean}|{HookInteger}|{HookReal:R}|{HookText ?? "<null>"}|{(ulong)HookMode}");

    /// <summary>
    /// Reads recorded hook events and optionally clears them.
    /// </summary>
    /// <param name="clear">Whether to clear the events after reading.</param>
    /// <returns>The events in callback order.</returns>
    [PgFunction]
    public static string GucEvents(bool clear)
    {
        string events = string.Join('\n', s_events);
        if (clear)
        {
            s_events.Clear();
        }

        return events;
    }

    /// <summary>
    /// Keeps an owned setting string across subsequent native changes.
    /// </summary>
    /// <param name="remember">Whether to replace the retained value.</param>
    /// <returns>The retained string after a full managed collection.</returns>
    [PgFunction]
    public static string? GucRetainedText(bool remember)
    {
        if (remember)
        {
            s_retainedText = Text;
        }

        GC.Collect();
        return s_retainedText;
    }

    /// <summary>
    /// Reads copies of integer hook data after the corresponding native allocations are freed.
    /// </summary>
    /// <returns>The retained byte snapshots.</returns>
    [PgFunction]
    public static string GucRetainedExtras()
    {
        GC.Collect();
        return string.Join(',', s_retained.Select(extra => Convert.ToHexString(extra.AsSpan())));
    }

    /// <summary>
    /// Validates an integer and prepares native-owned extra data.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="source">The native configuration source.</param>
    /// <returns>An accepted value or a diagnostic rejection.</returns>
    internal static PgGucCheckResult<int> CheckInteger(int value, PgGucSource source)
    {
        s_events.Add($"check:{value}:{source}");
        if (Control == "read-validation")
        {
            s_events.Add("validation:" + GucValidationRecovery(value));
        }

        if (Control is "check-log-fatal" or "check-log-fatal-unrepresentable")
        {
            try
            {
                PgLog.Write(PgLogLevel.Fatal, new PgDiagnostic(Control == "check-log-fatal-unrepresentable" ?
                    "Unrepresentable 🐘" : "Check requested termination.")
                {
                    SqlState = "P0001"
                });
            }
            finally
            {
                PgLog.Write(PgLogLevel.Notice, "Check finally.");
            }
        }

        if (source == PgGucSource.Default && Control == "reject-boot")
        {
            return new(new PgGucCheckError("Rejected boot integer."));
        }

        if (source == PgGucSource.Default && Control == "throw-boot")
        {
            throw new InvalidOperationException("Boot check failure.");
        }

        return value switch
        {
            13 => new(new PgGucCheckError("Rejected 100% café 🐘", "Owned detail", "Choose another integer.", "22003")),
            14 => new(new PgGucCheckError(detail: "Default native message")),
            15 => throw new InvalidOperationException("Unexpected check failure."),
            16 => throw new PgException("22012", "Owned check exception.", "Check detail", "Check hint"),
            17 => new(Spi.ExecuteScalar<int>("SELECT 42"), new PgGucExtra([0, 255, 0, 42])),
            21 => new(22, new PgGucExtra([0, 255, 0, 22])),
            23 => new(23),
            24 => new(24, new PgGucExtra([])),
            25 => new(1000),
            _ => new(value, new PgGucExtra(BitConverter.GetBytes(value))),
        };
    }

    /// <summary>
    /// Observes a newly accepted integer before native storage changes.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    internal static void AssignInteger(int value, PgGucExtra? extra)
    {
        s_events.Add($"assign:{value}:old={HookInteger}:extra={ExtraText(extra)}");
        if (extra is not null)
        {
            s_retained.Add(extra);
        }

        if (value == 66)
        {
            throw new InvalidOperationException("Assign must not fail.");
        }

        if (Control == "probe")
        {
            s_events.Add("assign-sql:" + ProbeSql());
        }
    }

    /// <summary>
    /// Formats integer display independently from typed storage.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    /// <returns>The display text.</returns>
    internal static string ShowInteger(int value, PgGucExtra? extra)
    {
        if (Control == "log-unrepresentable")
        {
            PgLog.Write(PgLogLevel.Notice, "Unrepresentable 🐘");
        }

        if (Control == "show-error")
        {
            throw new PgException("22023", "Show failed safely.");
        }

        return $"integer={value};extra={ExtraText(extra)}";
    }

    /// <summary>
    /// Normalizes a Boolean and retains its source.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="source">The native configuration source.</param>
    /// <returns>The accepted value and source bytes.</returns>
    internal static PgGucCheckResult<bool> CheckBoolean(bool value, PgGucSource source)
        => value && Rejection("Boolean") is PgGucCheckError error
            ? new(error)
            : new(Control == "normalize" ? !value : value, new PgGucExtra([(byte)source]));

    /// <summary>
    /// Observes Boolean assignment and its prior native value.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    internal static void AssignBoolean(bool value, PgGucExtra? extra)
        => s_events.Add($"bool:{value}:old={HookBoolean}:extra={ExtraText(extra)}");

    /// <summary>
    /// Formats the Boolean and copied hook data.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    /// <returns>The display text.</returns>
    internal static string ShowBoolean(bool value, PgGucExtra? extra) => $"boolean={value};extra={ExtraText(extra)}";

    /// <summary>
    /// Normalizes a fractional setting and retains its source.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="source">The native configuration source.</param>
    /// <returns>The accepted value and source bytes.</returns>
    internal static PgGucCheckResult<double> CheckReal(double value, PgGucSource source)
        => value > 100 && Rejection("real") is PgGucCheckError error
            ? new(error)
            : new(Control == "normalize_nan" ? double.NaN : Control == "normalize" ? Math.Round(value) : value,
                new PgGucExtra([(byte)source]));

    /// <summary>
    /// Observes real assignment and its prior native value.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    internal static void AssignReal(double value, PgGucExtra? extra)
        => s_events.Add(string.Create(CultureInfo.InvariantCulture, $"real:{value:R}:old={HookReal:R}:extra={ExtraText(extra)}"));

    /// <summary>
    /// Formats a real setting and copied hook data.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    /// <returns>The display text.</returns>
    internal static string ShowReal(double value, PgGucExtra? extra)
        => string.Create(CultureInfo.InvariantCulture, $"real={value:R};extra={ExtraText(extra)}");

    /// <summary>
    /// Trims a nullable proposed string and copies its original bytes.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="source">The native configuration source.</param>
    /// <returns>The normalized text and original bytes.</returns>
    internal static PgGucCheckResult<string?> CheckText(string? value, PgGucSource source)
    {
        if (!Enum.IsDefined(source))
        {
            throw new InvalidOperationException("Text check received an unknown native GUC source.");
        }

        if (value == "rejected" && Rejection("text") is PgGucCheckError error)
        {
            return new(error);
        }

        return new(Control == "unrepresentable" ? "🐘" : value?.Trim(), value is null ? null : new PgGucExtra(Encoding.UTF8.GetBytes(value)));
    }

    /// <summary>
    /// Observes nullable string assignment before native storage changes.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    internal static void AssignText(string? value, PgGucExtra? extra)
        => s_events.Add($"text:{value ?? "<null>"}:old={HookText ?? "<null>"}:extra={ExtraText(extra)}");

    /// <summary>
    /// Formats nullable text and copied hook data.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    /// <returns>The display text.</returns>
    internal static string ShowText(string? value, PgGucExtra? extra) => $"text={value ?? "<null>"};extra={ExtraText(extra)}";

    /// <summary>
    /// Normalizes a wide enum and copies the native source.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="source">The native configuration source.</param>
    /// <returns>The accepted enum and source bytes.</returns>
    internal static PgGucCheckResult<GucMode> CheckMode(GucMode value, PgGucSource source)
        => value == GucMode.Fast && Rejection("mode") is PgGucCheckError error
            ? new(error)
            : new(Control == "normalize" ? GucMode.Fast : value, new PgGucExtra([(byte)source]));

    /// <summary>
    /// Returns the rejection a <c>reject-</c> control selects, or null to accept.
    /// </summary>
    /// <param name="type">The setting type named in custom diagnostics.</param>
    /// <returns>PostgreSQL's standard message, a custom message with detail, a custom message with hint, or null.</returns>
    private static PgGucCheckError? Rejection(string type) => Control switch
    {
        "reject-default" => new PgGucCheckError(),
        "reject-message" => new PgGucCheckError($"Rejected {type}.", $"{type} detail"),
        "reject-hint" => new PgGucCheckError($"Rejected {type}.", hint: $"Choose another {type}."),
        _ => null,
    };

    /// <summary>
    /// Observes enum assignment before native storage changes.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    internal static void AssignMode(GucMode value, PgGucExtra? extra)
        => s_events.Add($"mode:{(ulong)value}:old={(ulong)HookMode}:extra={ExtraText(extra)}");

    /// <summary>
    /// Formats a wide enum without changing its native ordinal.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    /// <returns>The display text.</returns>
    internal static string ShowMode(GucMode value, PgGucExtra? extra) => $"mode={(ulong)value};extra={ExtraText(extra)}";

    /// <summary>
    /// Probes SQL availability during native parameter reporting.
    /// </summary>
    /// <param name="value">The proposed or current typed value.</param>
    /// <param name="extra">The copied accepted hook data.</param>
    /// <returns>The report display text.</returns>
    internal static string ShowReported(int value, PgGucExtra? extra)
    {
        if (extra is not null)
        {
            throw new InvalidOperationException("Show-only setting received check-hook data.");
        }

        if (Control == "logging")
        {
            PgLog.Write(PgLogLevel.Notice, $"report={value};sql={ProbeSql()};café 100%");
        }

        return value == 666 ? throw new InvalidOperationException("Report show must not fail.") : $"report={value};sql={ProbeSql()}";
    }

    private static string ProbeSql()
    {
        try
        {
            return Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "unavailable";
        }
    }

    private static string ExtraText(PgGucExtra? extra) => extra is null ? "null" : Convert.ToHexString(extra.AsSpan());
}

/// <summary>
/// Supplies GUC labels independently of wide managed enum values and aliases.
/// </summary>
public enum GucMode : ulong
{
    /// <summary>
    /// Selects the default mode at the maximum unsigned value.
    /// </summary>
    [PgGucLabel("idle")]
    Idle = ulong.MaxValue,

    /// <summary>
    /// Selects a second name for the default mode.
    /// </summary>
    [PgGucLabel("rest")]
    IdleAlias = Idle,

    /// <summary>
    /// Selects a value outside the signed 64-bit range.
    /// </summary>
    [PgGucLabel("fast")]
    Fast = 1UL << 63,

    /// <summary>
    /// Selects an accepted label omitted from native hints.
    /// </summary>
    [PgGucLabel("secret", Hidden = true)]
    Hidden = 10,

    /// <summary>
    /// Selects a member without a label, which PostgreSQL accepts by its C# name as pgrx accepts a variant name.
    /// </summary>
    Steady = 3,
}
