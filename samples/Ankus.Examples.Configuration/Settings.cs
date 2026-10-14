using System.Globalization;

namespace Ankus.Examples.Configuration;

/// <summary>
/// Declares native settings that can be preloaded before PostgreSQL forks its backends.
/// </summary>
public static partial class Settings
{
    private static readonly int s_firstProcess = Environment.ProcessId;

    /// <summary>
    /// Gets the enabled setting using PostgreSQL's UserSet context.
    /// </summary>
    [PgGucBool("ankus_configuration.enabled", true, "Enabled setting", Context = PgGucContext.UserSet)]
    public static partial bool Enabled { get; }

    /// <summary>
    /// Gets the startup setting using PostgreSQL's Postmaster context.
    /// </summary>
    [PgGucInt("ankus_configuration.startup", 10, "Startup setting", Context = PgGucContext.Postmaster)]
    public static partial int Startup { get; }

    /// <summary>
    /// Gets the reload setting using PostgreSQL's Sighup context.
    /// </summary>
    [PgGucInt("ankus_configuration.reload", 20, "Reload setting", Context = PgGucContext.Sighup)]
    public static partial int Reload { get; }

    /// <summary>
    /// Gets the connection setting using PostgreSQL's Backend context.
    /// </summary>
    [PgGucInt("ankus_configuration.connection", 30, "Connection setting", Context = PgGucContext.Backend)]
    public static partial int Connection { get; }

    /// <summary>
    /// Gets the privileged connection setting using PostgreSQL's SuperuserBackend context.
    /// </summary>
    [PgGucInt("ankus_configuration.privileged_connection", 40, "PrivilegedConnection setting", Context = PgGucContext.SuperuserBackend)]
    public static partial int PrivilegedConnection { get; }

    /// <summary>
    /// Gets the internal setting using PostgreSQL's Internal context.
    /// </summary>
    [PgGucInt("ankus_configuration.internal", 50, "Internal setting", Context = PgGucContext.Internal)]
    public static partial int Internal { get; }

    /// <summary>
    /// Gets the user setting using PostgreSQL's UserSet context.
    /// </summary>
    [PgGucInt("ankus_configuration.user", 60, "User setting", Context = PgGucContext.UserSet)]
    public static partial int User { get; }

    /// <summary>
    /// Gets the privileged setting using PostgreSQL's SuperuserSet context.
    /// </summary>
    [PgGucInt("ankus_configuration.privileged", 70, "Privileged setting", Context = PgGucContext.SuperuserSet)]
    public static partial int Privileged { get; }

    /// <summary>
    /// Gets the optional native string, preserving a null boot value.
    /// </summary>
    [PgGucString("ankus_configuration.text", null, "Optional text")]
    public static partial string? Text { get; }

    /// <summary>
    /// Gets a setting with PostgreSQL's native file-restriction flag.
    /// </summary>
    [PgGucInt("ankus_configuration.no_file", 1, "Native file restriction", Flags = PgGucOptions.DisallowInFile)]
    public static partial int NoFile { get; }

    /// <summary>
    /// Gets a quantity expressed in bytes.
    /// </summary>
    [PgGucInt("ankus_configuration.bytes", 0, "Quantity in bytes", Unit = PgGucUnit.Bytes)]
    public static partial int Bytes { get; }

    /// <summary>
    /// Gets a quantity expressed in kilobytes.
    /// </summary>
    [PgGucInt("ankus_configuration.kilobytes", 0, "Quantity in kilobytes", Unit = PgGucUnit.Kilobytes)]
    public static partial int Kilobytes { get; }

    /// <summary>
    /// Gets a quantity expressed in blocks.
    /// </summary>
    [PgGucInt("ankus_configuration.blocks", 0, "Quantity in blocks", Unit = PgGucUnit.Blocks)]
    public static partial int Blocks { get; }

    /// <summary>
    /// Gets a quantity expressed in walblocks.
    /// </summary>
    [PgGucInt("ankus_configuration.walblocks", 0, "Quantity in walblocks", Unit = PgGucUnit.WalBlocks)]
    public static partial int WalBlocks { get; }

    /// <summary>
    /// Gets a quantity expressed in megabytes.
    /// </summary>
    [PgGucInt("ankus_configuration.megabytes", 0, "Quantity in megabytes", Unit = PgGucUnit.Megabytes)]
    public static partial int Megabytes { get; }

    /// <summary>
    /// Gets a quantity expressed in milliseconds.
    /// </summary>
    [PgGucInt("ankus_configuration.milliseconds", 0, "Quantity in milliseconds", Unit = PgGucUnit.Milliseconds)]
    public static partial int Milliseconds { get; }

    /// <summary>
    /// Gets a quantity expressed in seconds.
    /// </summary>
    [PgGucInt("ankus_configuration.seconds", 0, "Quantity in seconds", Unit = PgGucUnit.Seconds)]
    public static partial int Seconds { get; }

    /// <summary>
    /// Gets a quantity expressed in minutes.
    /// </summary>
    [PgGucInt("ankus_configuration.minutes", 0, "Quantity in minutes", Unit = PgGucUnit.Minutes)]
    public static partial int Minutes { get; }

    /// <summary>
    /// Gets a fractional quantity expressed in seconds.
    /// </summary>
    [PgGucReal("ankus_configuration.real_seconds", 0, "Fractional seconds", Unit = PgGucUnit.Seconds)]
    public static partial double RealSeconds { get; }

    /// <summary>
    /// Reads the inherited native values from a freshly initialized managed backend.
    /// </summary>
    /// <returns>The setting values and the process that first entered this managed type.</returns>
    [PgFunction]
    public static string ConfigurationSnapshot()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"{Startup}|{Reload}|{Connection}|{PrivilegedConnection}|{Internal}|{User}|{Privileged}|{Text ?? "<null>"}|{s_firstProcess}");
    }

    /// <summary>
    /// Reads all numeric units in their declared base units without formatting them through SHOW.
    /// </summary>
    /// <returns>The integer and fractional quantities.</returns>
    [PgFunction]
    public static string ConfigurationUnits() => string.Create(CultureInfo.InvariantCulture,
        $"{Bytes}|{Kilobytes}|{Blocks}|{WalBlocks}|{Megabytes}|{Milliseconds}|{Seconds}|{Minutes}|{RealSeconds:R}");
}
