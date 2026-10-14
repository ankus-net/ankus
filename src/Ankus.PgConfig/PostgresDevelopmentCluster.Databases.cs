using System.Globalization;
using System.Text;

namespace Ankus.PgConfig;

public sealed partial class PostgresDevelopmentCluster
{
    /// <summary>
    /// Gets a libpq connection string for the running cluster's actual port and an exact database name.
    /// This does not start the server or create the database.
    /// </summary>
    /// <param name="database">The literal database name, not a connection string.</param>
    /// <param name="cancellationToken">Cancels status and port discovery.</param>
    /// <returns>Connection settings suitable for psql or another libpq client.</returns>
    public Task<string> GetConnectionStringAsync(string database, CancellationToken cancellationToken = default)
        => GetConnectionStringAsync(database, "postgres", cancellationToken);

    private async Task<string> GetConnectionStringAsync(string database, string user, CancellationToken cancellationToken)
    {
        ValidateDatabaseName(database);
        if (!await IsRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The development server is stopped. Run 'ankus start' first.");
        }

        // PostgreSQL writes the active TCP port on line four, independently of later configuration edits.
        // Its status updates may keep a writable handle open while clients read that stable field.
        using var status = new FileStream(Path.Join(DataDirectory, "postmaster.pid"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(status);
        string? portText = null;
        for (int line = 0; line < 4; line++)
        {
            portText = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
        {
            throw new FormatException("The running PostgreSQL server has no valid TCP port in postmaster.pid.");
        }

        return CreateConnectionString(port, database, user);
    }

    /// <summary>
    /// Creates a database in the running cluster, or reuses an existing database without modifying it.
    /// Names retain their exact UTF-8 spelling; names exceeding the server's identifier limit are rejected.
    /// </summary>
    /// <param name="database">The literal database name.</param>
    /// <param name="cancellationToken">Cancels database queries and creation.</param>
    /// <returns>True when a database was created, or false when it already existed.</returns>
    public Task<bool> CreateDatabaseAsync(string database, CancellationToken cancellationToken = default)
        => CreateDatabaseAsync(database, null, cancellationToken);

    /// <summary>
    /// Creates a database in the running cluster as another Unix account, or reuses an existing database without modifying it.
    /// </summary>
    /// <remarks>
    /// As <c>cargo pgrx regress --runas</c> runs <c>createdb</c>, psql runs as <paramref name="runAs"/> through
    /// <c>sudo -u</c> and connects as the role of the same name, which owns the new database. That role must exist and
    /// be allowed to create databases. A null account creates the database as the cluster's <c>postgres</c> role.
    /// </remarks>
    /// <param name="database">The literal database name.</param>
    /// <param name="runAs">The Unix account and role that creates the database, or null for the cluster's own role.</param>
    /// <param name="cancellationToken">Cancels database queries and creation.</param>
    /// <returns>True when a database was created, or false when it already existed.</returns>
    public async Task<bool> CreateDatabaseAsync(string database, string? runAs, CancellationToken cancellationToken)
    {
        int byteCount = ValidateDatabaseName(database);
        ValidateAccount(runAs);
        string connection = await GetConnectionStringAsync("postgres", cancellationToken).ConfigureAwait(false);
        using FileStream operationLock = AcquireLock();
        // Encode names in SQL so native argv code pages and psql's text-mode input
        // cannot change Unicode, CRLF or control characters in identifiers.
        string[] arguments = ["--no-psqlrc", "--no-password", "--quiet", "--tuples-only", "--no-align",
            "--set=ON_ERROR_STOP=1", "--dbname=" + connection, "--file=-"];
        if (await DatabaseExistsAsync(database, byteCount, arguments, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        (int code, string output) = await RunClientAsync(runAs, arguments, "CREATE DATABASE " + QuoteDatabaseName(database) + ";\n",
            cancellationToken).ConfigureAwait(false);
        RequireDatabaseCommand(code, output);
        return true;
    }

    /// <summary>
    /// Drops an exact database from the running development cluster, or leaves an absent database unchanged.
    /// Other databases and the server remain running. PostgreSQL errors preserve their diagnostics.
    /// </summary>
    /// <param name="database">The literal database name. Names exceeding the server's identifier limit are rejected.</param>
    /// <param name="force">Whether PostgreSQL should terminate connections to the selected database before dropping it.</param>
    /// <param name="cancellationToken">Cancels database queries and removal.</param>
    /// <returns>True when a database was dropped, or false when it was already absent.</returns>
    public Task<bool> DropDatabaseAsync(string database, bool force = false, CancellationToken cancellationToken = default)
        => DropDatabaseAsync(database, force, null, cancellationToken);

    /// <summary>
    /// Drops an exact database from the running development cluster as another Unix account, or leaves an absent
    /// database unchanged.
    /// </summary>
    /// <remarks>
    /// As <c>cargo pgrx regress --runas</c> runs <c>dropdb</c>, psql runs as <paramref name="runAs"/> through
    /// <c>sudo -u</c> and connects as the role of the same name, which must own the database or be a superuser.
    /// </remarks>
    /// <param name="database">The literal database name. Names exceeding the server's identifier limit are rejected.</param>
    /// <param name="force">Whether PostgreSQL should terminate connections to the selected database before dropping it.</param>
    /// <param name="runAs">The Unix account and role that drops the database, or null for the cluster's own role.</param>
    /// <param name="cancellationToken">Cancels database queries and removal.</param>
    /// <returns>True when a database was dropped, or false when it was already absent.</returns>
    public async Task<bool> DropDatabaseAsync(string database, bool force, string? runAs, CancellationToken cancellationToken)
    {
        int byteCount = ValidateDatabaseName(database);
        ValidateAccount(runAs);
        string connection = await GetConnectionStringAsync("postgres", cancellationToken).ConfigureAwait(false);
        using FileStream operationLock = AcquireLock();
        string[] arguments = ["--no-psqlrc", "--no-password", "--quiet", "--tuples-only", "--no-align",
            "--set=ON_ERROR_STOP=1", "--dbname=" + connection, "--file=-"];
        if (!await DatabaseExistsAsync(database, byteCount, arguments, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        string command = "DROP DATABASE " + QuoteDatabaseName(database) +
            (force ? " WITH (FORCE)" : "") + ";\n";
        (int code, string output) = await RunClientAsync(runAs, arguments, command, cancellationToken).ConfigureAwait(false);
        RequireDatabaseCommand(code, output);
        return true;
    }

    /// <summary>
    /// Formats exact database names as ASCII libpq URIs, preserving Unicode across native Windows command lines.
    /// </summary>
    internal static string CreateConnectionString(int port, string database, string user = "postgres")
    {
        ValidateDatabaseName(database);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return string.Create(CultureInfo.InvariantCulture,
            $"postgresql://{Uri.EscapeDataString(user)}@127.0.0.1:{port}/{Uri.EscapeDataString(database)}?hostaddr=127.0.0.1&connect_timeout=10");
    }

    /// <summary>
    /// Runs one psql database command as the cluster's own role, or as another account and its role through sudo.
    /// </summary>
    private async Task<(int Code, string Output)> RunClientAsync(string? runAs, string[] arguments, string input,
        CancellationToken cancellationToken)
    {
        if (runAs is null)
        {
            return await RunAsync(_installation.PsqlPath, arguments, cancellationToken, input: input, postgresClient: true)
                .ConfigureAwait(false);
        }

        string connection = await GetConnectionStringAsync("postgres", runAs, cancellationToken).ConfigureAwait(false);
        string[] accountArguments = [.. arguments.Select(argument => argument.StartsWith("--dbname=", StringComparison.Ordinal)
            ? "--dbname=" + connection : argument)];
        return await RunAsync("sudo", ["-u", runAs, "--", "env", "PGCLIENTENCODING=UTF8", _installation.PsqlPath, .. accountArguments],
            cancellationToken, input: input, postgresClient: true, sudo: true).ConfigureAwait(false);
    }

    private static void ValidateAccount(string? runAs)
    {
        if (runAs is null)
        {
            return;
        }

        if (runAs.Length == 0 || runAs.StartsWith('-') || runAs.Any(static c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("The account must be a Unix account name.", nameof(runAs));
        }

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Database commands cannot run as another account on Windows.");
        }
    }

    private static int ValidateDatabaseName(string database)
    {
        ArgumentException.ThrowIfNullOrEmpty(database);
        if (database.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Database names cannot contain NUL.", nameof(database));
        }

        return new UTF8Encoding(false, true).GetByteCount(database);
    }

    private async Task<bool> DatabaseExistsAsync(string database, int byteCount, string[] arguments, CancellationToken cancellationToken)
    {
        string literal = "convert_from(decode('" + Convert.ToHexString(Encoding.UTF8.GetBytes(database)) + "', 'hex'), 'UTF8')";
        (int code, string output) = await RunAsync(_installation.PsqlPath, arguments, cancellationToken,
            input: $"SELECT current_setting('max_identifier_length'), EXISTS (SELECT FROM pg_catalog.pg_database WHERE datname = {literal});\n",
            postgresClient: true).ConfigureAwait(false);
        RequireDatabaseCommand(code, output);
        string[] fields = output.Trim().Split('|');
        if (fields.Length != 2 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int limit) ||
            fields[1] is not ("t" or "f"))
        {
            throw new FormatException($"Unexpected PostgreSQL database metadata: {output}");
        }

        if (byteCount > limit)
        {
            throw new ArgumentException($"The database name exceeds PostgreSQL's {limit}-byte identifier limit.", nameof(database));
        }

        return fields[1] == "t";
    }

    /// <summary>
    /// Represents every Unicode scalar with an ASCII SQL escape, including quotes and line endings.
    /// </summary>
    private static string QuoteDatabaseName(string database)
    {
        var result = new StringBuilder("U&\"");
        foreach (Rune value in database.EnumerateRunes())
        {
            result.Append("\\+").Append(value.Value.ToString("X6", CultureInfo.InvariantCulture));
        }

        return result.Append('"').ToString();
    }

    private static void RequireDatabaseCommand(int code, string output)
    {
        if (code != 0)
        {
            throw new InvalidOperationException($"PostgreSQL database command failed ({code}): {output}");
        }
    }
}
