using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Defines configuration parameters with names computed at run time, as pgrx extensions do through GucRegistry.
/// </summary>
public static class RuntimeGucFunctions
{
    private const string Prefix = "ankus_runtime";

    private static PgGucSetting<bool>? s_enabled;
    private static PgGucSetting<int>? s_limit;
    private static PgGucSetting<double>? s_ratio;
    private static PgGucSetting<string?>? s_label;
    private static PgGucSetting<int>? s_mode;
    private static PgGucSetting<int>? s_hookedLimit;
    private static PgGucSetting<string?>? s_hookedLabel;
    private static PgGucSetting<int>? s_hookedMode;
    private static PgGucSetting<bool>? s_hookedEnabled;
    private static PgGucSetting<double>? s_hookedRatio;
    private static readonly List<string> s_hookEvents = [];

    /// <summary>
    /// Defines one parameter of each kind; module load calls this on every load attempt.
    /// </summary>
    internal static void DefineAtLoad()
    {
        string Name(string suffix) => Prefix + "." + suffix;
        s_enabled = PgGucRegistry.DefineBool(Name("enabled"), true, "Run-time Boolean", "Defined after the name is computed.");
        s_limit = PgGucRegistry.DefineInt(Name("limit"), 10, "Run-time integer", minimum: 1, maximum: 4096, unit: PgGucUnit.Kilobytes);
        s_ratio = PgGucRegistry.DefineReal(Name("ratio"), 0.25, "Run-time real", minimum: 0, maximum: 1);
        s_label = PgGucRegistry.DefineString(Name("label"), null, "Run-time string", context: PgGucContext.SuperuserSet);
        s_mode = PgGucRegistry.DefineEnum(Name("mode"), 1, "Run-time enumeration",
            [new PgGucEnumOption("off", 0), new PgGucEnumOption("on", 1), new PgGucEnumOption("true", 1, Hidden: true), new PgGucEnumOption("auto", 2)]);
        DefineHookedAtLoad();
    }

    /// <summary>
    /// Defines one parameter of each kind with hooks, as pgrx's define_*_guc_with_hooks functions do.
    /// </summary>
    private static void DefineHookedAtLoad()
    {
        static string Name(string suffix) => Prefix + ".hooked_" + suffix;
        s_hookedLimit = PgGucRegistry.DefineInt(Name("limit"), 4, "Run-time integer with hooks", minimum: 0, maximum: 100, hooks: new()
        {
            Check = static (proposed, _) => proposed % 2 != 0
                ? new(new PgGucCheckError("The limit must be even.", hint: "Use an even number."))
                : new(proposed, new PgGucExtra([(byte)(proposed / 2)])),
            Assign = static (accepted, extra) => s_hookEvents.Add($"limit:{accepted}:{extra?[0]}"),
            Show = static (current, extra) => $"{current} units ({extra?[0]} pairs)",
        });
        s_hookedLabel = PgGucRegistry.DefineString(Name("label"), "plain", "Run-time string with hooks", hooks: new()
        {
            Check = static (proposed, _) => new(proposed?.Trim()),
        });
        s_hookedMode = PgGucRegistry.DefineEnum(Name("mode"), 0, "Run-time enumeration with hooks",
            [new PgGucEnumOption("low", 0), new PgGucEnumOption("high", 1)], hooks: new()
        {
            Show = static (current, _) => current == 0 ? "LOW" : "HIGH",
        });
        s_hookedEnabled = PgGucRegistry.DefineBool(Name("enabled"), false, "Run-time Boolean with hooks", hooks: new()
        {
            Assign = static (accepted, _) => s_hookEvents.Add($"enabled:{accepted}"),
        });
        s_hookedRatio = PgGucRegistry.DefineReal(Name("ratio"), 0.5, "Run-time real with hooks", minimum: 0, maximum: 1, hooks: new()
        {
            Check = static (proposed, source) => source == PgGucSource.Session && proposed is 1
                ? new(new PgGucCheckError(detail: "A session cannot claim the whole ratio."))
                : new(Math.Round(proposed, 1)),
        });
    }

    /// <summary>
    /// Reads every hooked parameter and the assign events recorded since the last call.
    /// </summary>
    /// <returns>The values and events separated by vertical bars.</returns>
    [PgFunction]
    public static string RuntimeGucHookedValues()
    {
        string events = string.Join(',', s_hookEvents);
        s_hookEvents.Clear();
        return string.Join('|', s_hookedLimit!.Value, s_hookedLabel!.Value ?? "<null>", s_hookedMode!.Value, s_hookedEnabled!.Value,
            s_hookedRatio!.Value.ToString(CultureInfo.InvariantCulture), events);
    }

    /// <summary>
    /// Defines the hooked integer again with a different check, which must be rejected.
    /// </summary>
    /// <returns>Never returns normally.</returns>
    [PgFunction]
    public static int RuntimeGucRedefineHooks()
        => PgGucRegistry.DefineInt(Prefix + ".hooked_limit", 4, "Run-time integer with hooks", minimum: 0, maximum: 100,
            hooks: new() { Check = static (proposed, _) => new(proposed) }).Value;

    /// <summary>
    /// Reads every load-time parameter through its managed reader.
    /// </summary>
    /// <returns>The values separated by vertical bars.</returns>
    [PgFunction]
    public static string RuntimeGucValues()
        => string.Join('|', s_enabled!.Value, s_limit!.Value, s_ratio!.Value.ToString(CultureInfo.InvariantCulture),
            s_label!.Value ?? "<null>", s_mode!.Value);

    /// <summary>
    /// Defines an integer parameter from SQL to test definition after load, repeated definitions and rejected names.
    /// </summary>
    /// <param name="name">The qualified name.</param>
    /// <param name="defaultValue">The default value.</param>
    /// <param name="maximum">The inclusive maximum.</param>
    /// <param name="context">The <see cref="PgGucContext"/> value.</param>
    /// <returns>The parameter's current value.</returns>
    [PgFunction]
    public static int RuntimeGucDefineInt(string name, int defaultValue, int maximum, int context)
        => PgGucRegistry.DefineInt(name, defaultValue, "SQL-defined integer", 0, maximum, context: (PgGucContext)context).Value;

    /// <summary>
    /// Attempts to redefine an attribute-declared parameter at run time.
    /// </summary>
    /// <returns>Never returns normally.</returns>
    [PgFunction]
    public static bool RuntimeGucRedefineAttribute() => PgGucRegistry.DefineBool("ankus_test.module_load", true, "Conflicting definition").Value;
}
