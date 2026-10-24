using System.Diagnostics;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies exact development-server configuration and validation before native operations.
/// </summary>
/// <param name="context">The current test's cancellation scope.</param>
[TestClass]
public sealed class PostgresDevelopmentClusterTests(TestContext context)
{
    /// <summary>
    /// Each supported major receives its own pgrx-compatible port and local-only connection routing.
    /// </summary>
    /// <param name="major">The selected major.</param>
    /// <param name="port">Its independent default port.</param>
    [TestMethod]
    [DataRow(13, 28813)]
    [DataRow(14, 28814)]
    [DataRow(15, 28815)]
    [DataRow(16, 28816)]
    [DataRow(17, 28817)]
    [DataRow(18, 28818)]
    [DataRow(19, 28819)]
    public void DefaultConfigurationUsesIndependentLoopbackPorts(int major, int port)
    {
        string expected = $"port = {port}\nlisten_addresses = '127.0.0.1'\nunix_socket_directories = ''\n" +
            "log_destination = 'stderr'\nlogging_collector = off\n";
        Assert.AreEqual(expected, PostgresDevelopmentCluster.CreateConfiguration(major, new PostgresDevelopmentOptions())
            .ReplaceLineEndings("\n"));
    }

    /// <summary>
    /// Literal quotes, backslashes, equals signs, whitespace, empty values, and Unicode retain their exact meaning.
    /// </summary>
    [TestMethod]
    public void SettingsAreQuotedWithoutShellOrConfigurationExpansion()
    {
        var options = new PostgresDevelopmentOptions
        {
            Port = 15432,
            Settings = new Dictionary<string, string>
            {
                ["probe.text"] = " café='\\value # ; $(echo test) ",
                ["probe.empty"] = "",
                ["work_mem"] = "16MB",
            },
        };
        string actual = PostgresDevelopmentCluster.CreateConfiguration(18, options);
        Assert.StartsWith("probe.text = ' café=''\\\\value # ; $(echo test) '" + Environment.NewLine, actual);
        Assert.Contains("probe.empty = ''" + Environment.NewLine, actual);
        Assert.Contains("work_mem = '16MB'" + Environment.NewLine, actual);
        Assert.Contains("port = 15432" + Environment.NewLine, actual);
    }

    /// <summary>
    /// Explicit port and startup-timeout endpoints remain accepted.
    /// </summary>
    /// <param name="port">An endpoint port.</param>
    /// <param name="timeout">An endpoint timeout.</param>
    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(65535, 600)]
    public void AcceptsOptionBoundaries(int port, int timeout)
    {
        string configuration = PostgresDevelopmentCluster.CreateConfiguration(18,
            new PostgresDevelopmentOptions { Port = port, TimeoutSeconds = timeout });
        Assert.StartsWith($"port = {port}" + Environment.NewLine, configuration);
    }

    /// <summary>
    /// Values immediately outside supported bounds are rejected.
    /// </summary>
    /// <param name="port">The port to validate.</param>
    /// <param name="timeout">The startup timeout to validate.</param>
    [TestMethod]
    [DataRow(0, 60)]
    [DataRow(-1, 60)]
    [DataRow(65536, 60)]
    [DataRow(28818, 0)]
    [DataRow(28818, -1)]
    [DataRow(28818, 601)]
    public void RejectsOptionValuesOutsideBounds(int port, int timeout)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PostgresDevelopmentCluster.CreateConfiguration(18,
            new PostgresDevelopmentOptions { Port = port, TimeoutSeconds = timeout }));
    }

    /// <summary>
    /// Routing or ownership overrides cannot redirect Ankus to another cluster or authentication file.
    /// </summary>
    /// <param name="name">A setting managed by Ankus.</param>
    [TestMethod]
    [DataRow("data_directory")]
    [DataRow("config_file")]
    [DataRow("ident_file")]
    [DataRow("external_pid_file")]
    [DataRow("port")]
    [DataRow("LISTEN_ADDRESSES")]
    [DataRow("unix_socket_directories")]
    [DataRow("log_destination")]
    [DataRow("logging_collector")]
    [DataRow("event_source")]
    [DataRow("include")]
    [DataRow("include_dir")]
    [DataRow("include_if_exists")]
    public void RejectsManagedSettingOverrides(string name)
    {
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => PostgresDevelopmentCluster.CreateConfiguration(18,
            new PostgresDevelopmentOptions { Settings = new Dictionary<string, string> { [name] = "anything" } }));
        Assert.Contains(name, error.Message);
    }

    /// <summary>
    /// A caller's own pg_hba.conf is accepted by absolute path; a relative path would be read from the data directory.
    /// </summary>
    [TestMethod]
    public void AcceptsAnAbsoluteHbaFile()
    {
        string path = Path.Join(Path.GetTempPath(), "regress café", "pg_hba.conf");
        string configuration = PostgresDevelopmentCluster.CreateConfiguration(18,
            new PostgresDevelopmentOptions { Settings = new Dictionary<string, string> { ["hba_file"] = path } });
        Assert.StartsWith("hba_file = '" + path.Replace("\\", "\\\\", StringComparison.Ordinal) + "'" + Environment.NewLine, configuration);
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => PostgresDevelopmentCluster.CreateConfiguration(18,
            new PostgresDevelopmentOptions { Settings = new Dictionary<string, string> { ["HBA_FILE"] = "regress-pg_hba.conf" } }));
        Assert.Contains("absolute path", error.Message);
    }

    /// <summary>
    /// The server gets pg_regress's temporary-instance client environment, naming itself and the cluster's superuser.
    /// </summary>
    [TestMethod]
    public void ServerEnvironmentNamesTheServerItself()
    {
        Dictionary<string, string?> environment = PostgresDevelopmentCluster.CreateServerEnvironment(28818);
        Assert.AreEqual("127.0.0.1", environment["PGHOST"]);
        Assert.AreEqual("28818", environment["PGPORT"]);
        Assert.AreEqual("postgres", environment["PGUSER"]);
        foreach (string name in (string[])["PGDATABASE", "PGHOSTADDR", "PGPASSWORD", "PGSERVICE", "PGSSLMODE", "PGTARGETSESSIONATTRS"])
        {
            Assert.IsTrue(environment.TryGetValue(name, out string? value), name);
            Assert.IsNull(value, name);
        }
    }

    /// <summary>
    /// Configuration names and values cannot inject extra statements or terminate native text.
    /// </summary>
    /// <param name="name">A candidate setting name.</param>
    /// <param name="value">A candidate literal value.</param>
    [TestMethod]
    [DataRow("bad name", "value")]
    [DataRow("", "value")]
    [DataRow(" ", "value")]
    [DataRow("1name", "value")]
    [DataRow("work_mem='1MB' #", "value")]
    [DataRow("work_mem", "16MB\nport=5432")]
    [DataRow("work_mem", "16MB\rport=5432")]
    [DataRow("work_mem", "16MB\0")]
    public void RejectsConfigurationInjection(string name, string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => PostgresDevelopmentCluster.CreateConfiguration(18,
            new PostgresDevelopmentOptions { Settings = new Dictionary<string, string> { [name] = value } }));
    }

    /// <summary>
    /// Null option collections and values fail explicitly instead of becoming omitted settings.
    /// </summary>
    [TestMethod]
    public void RejectsNullSettingsAndValues()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => PostgresDevelopmentCluster.CreateConfiguration(18,
            new PostgresDevelopmentOptions { Settings = null! }));
        Assert.ThrowsExactly<ArgumentNullException>(() => PostgresDevelopmentCluster.CreateConfiguration(18,
            new PostgresDevelopmentOptions { Settings = new Dictionary<string, string> { ["probe.text"] = null! } }));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PostgresDevelopmentCluster(null!));
    }

    /// <summary>
    /// A stop waits while another process still writes the log, as the shell pg_ctl starts on Windows can just after the
    /// server exits, but not for a missing log, a reader, or past its timeout.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task StopWaitsOnlyForTheLogWriter()
    {
        CancellationToken token = context.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("ankus-log-writer-").FullName;
        try
        {
            string path = Path.Join(directory, "pg18.log.stderr.log");
            TimeSpan wait = TimeSpan.FromSeconds(30);
            var elapsed = Stopwatch.StartNew();
            await PostgresDevelopmentCluster.WaitForLogWriterAsync(path, wait, token);
            await File.WriteAllTextAsync(path, "ready\n", token);
            await using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                await PostgresDevelopmentCluster.WaitForLogWriterAsync(path, wait, token);
            }

            Assert.IsLessThan(TimeSpan.FromSeconds(5), elapsed.Elapsed);
            Task waiting;
            await using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                waiting = PostgresDevelopmentCluster.WaitForLogWriterAsync(path, wait, token);
                await Task.Delay(300, token);
                Assert.IsFalse(waiting.IsCompleted);
            }

            await waiting.WaitAsync(wait, token);
            await using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                await PostgresDevelopmentCluster.WaitForLogWriterAsync(path, TimeSpan.FromMilliseconds(200), token).WaitAsync(wait, token);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
