using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A package consumer uses the saved testing base to publish and query an extension on the exact requested port, and a
    /// cluster the host never disposes is stopped and removed when the host exits.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task PackagedExtensionFixtureUsesSavedTestPortAndCleansUp()
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        string extensionRoot = CreateDirectory();
        string extensionProject = Path.Join(extensionRoot, "PortProbe.csproj");
        XDocument.Load(s_project).Save(extensionProject);
        File.Copy(Path.Join(Path.GetDirectoryName(s_project)!, "Hello.cs"), Path.Join(extensionRoot, "Hello.cs"));
        using PortReservation reservation = ReserveConfigurablePort();
        ProcessResult init = await InvokeAsync(["init", "--home", home, s_postgresOption, s_installation.PgConfigPath,
            "--base-testing-port", (reservation.Port - s_installation.Version.Major).ToString(CultureInfo.InvariantCulture)], token);
        Assert.AreEqual(0, init.ExitCode, init.StandardError);
        string consumerRoot = CreateDirectory();
        string consumerProject = Path.Join(consumerRoot, "Consumer.csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("OutputType", "Exe"), new XElement("ImplicitUsings", "enable"), new XElement("Nullable", "enable")),
            new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", "Ankus.Testing"),
                new XAttribute("Version", s_version))))).Save(consumerProject);
        await File.WriteAllTextAsync(Path.Join(consumerRoot, "Program.cs"), """
            using Ankus.PgConfig;
            using Ankus.Testing;
            using Npgsql;
            var registry = new PostgresRegistry(args[1]);
            int major = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
            var installation = await registry.GetAsync(major);
            string observed;
            string data;
            string sockets;
            await using (var fixture = await PostgresExtensionTest.StartAsync(
                args[0], sharedPreload: false, registry.GetTestPort(major), installation))
            {
                data = fixture.Cluster.DataDirectory;
                sockets = fixture.Cluster.SocketDirectory;
                bool testsRejected = false;
                try
                {
                    await fixture.RunTestAsync(new Ankus.PgTestCase("Checks.Test()", null, "test"));
                }
                catch (InvalidOperationException error) when (error.Message.Contains("IncludeTests", StringComparison.Ordinal))
                {
                    testsRejected = true;
                }

                if (!testsRejected)
                {
                    throw new InvalidOperationException("A normal publication accepted a backend test.");
                }

                await using var connection = await fixture.Cluster.OpenConnectionAsync();
                await using var command = new NpgsqlCommand(
                    "SELECT current_setting('port') || '|' || public.add(19, 23)", connection);
                observed = (string)(await command.ExecuteScalarAsync())!;
            }

            Console.WriteLine($"PORT_PROBE:{observed}|{Directory.Exists(data)}|{Directory.Exists(sockets)}");

            // Exit without disposing this cluster; the fixture's process-exit handler must stop and remove it.
            PostgresTestCluster abandoned = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions { Installation = installation });
            Console.WriteLine($"ABANDONED:{abandoned.DataDirectory}|{abandoned.SocketDirectory}");
            """, token);
        reservation.Dispose();
        ProcessResult result = await PackageProcessRunner.RunAsync("dotnet",
            ["run", "--project", consumerProject, "--", extensionProject, home, MajorText()],
            s_environment, token, workingDirectory: consumerRoot);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains($"PORT_PROBE:{reservation.Port}|42|False|False", result.StandardOutput);
        string[] abandoned = result.StandardOutput.Split('\n').Single(static line => line.StartsWith("ABANDONED:", StringComparison.Ordinal))
            ["ABANDONED:".Length..].Trim().Split('|');
        Assert.IsFalse(Directory.Exists(abandoned[0]), "A cluster the host never disposed kept its data directory after exit.");
        Assert.IsFalse(Directory.Exists(abandoned[1]), "A cluster the host never disposed kept its socket directory after exit.");
        Assert.IsEmpty(Directory.GetDirectories(Path.Join(extensionRoot, "bin", "ankus-test-publish")));
        using PortReservation released = PortReservation.Create(reservation.Port);
        Assert.AreEqual(reservation.Port, released.Port);
    }

    /// <summary>
    /// Init updates selected bases atomically, preserves unrelated settings, and exposes resolved per-major ports.
    /// </summary>
    [TestMethod]
    public async Task InitPersistsPortBasesAndPreservesUnselectedSettings()
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        var registry = new PostgresRegistry(home);
        const string Initial = "{\"basePort\":29000,\"baseTestingPort\":31000,\"custom\":{\"keep\":true}}";
        await File.WriteAllTextAsync(registry.ConfigurationPath, Initial, token);
        ProcessResult first = await InvokeAsync(["init", "--home", home, s_postgresOption, s_installation.PgConfigPath,
            "--base-port", "30000"], token);
        Assert.AreEqual(0, first.ExitCode, first.StandardError);
        Assert.AreEqual(30000 + s_installation.Version.Major, registry.GetPort(s_installation.Version.Major));
        Assert.AreEqual(31000 + s_installation.Version.Major, registry.GetTestPort(s_installation.Version.Major));
        ProcessResult second = await InvokeAsync(["init", "--home", home, s_postgresOption, s_installation.PgConfigPath,
            "--base-testing-port", "32000"], token);
        Assert.AreEqual(0, second.ExitCode, second.StandardError);
        ProcessResult repeated = await InvokeAsync(["init", "--home", home, s_postgresOption, s_installation.PgConfigPath], token);
        Assert.AreEqual(0, repeated.ExitCode, repeated.StandardError);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(registry.ConfigurationPath, token));
        Assert.AreEqual(30000, document.RootElement.GetProperty("basePort").GetInt32());
        Assert.AreEqual(32000, document.RootElement.GetProperty("baseTestingPort").GetInt32());
        Assert.IsTrue(document.RootElement.GetProperty("custom").GetProperty("keep").GetBoolean());
        Assert.AreEqual(s_installation.PgConfigPath, registry.GetPath(s_installation.Version.Major));
        ProcessResult info = await InvokeAsync(["info", "--home", home, "--pg", MajorText()], token);
        Assert.AreEqual(0, info.ExitCode, info.StandardError);
        Assert.Contains($"Development port: {30000 + s_installation.Version.Major}", info.StandardOutput);
        Assert.Contains($"Test port: {32000 + s_installation.Version.Major}", info.StandardOutput);
    }

    /// <summary>
    /// Invalid bases are rejected before a default init can download or register any version.
    /// </summary>
    /// <param name="option">The selected base option.</param>
    /// <param name="value">The value outside the supported range.</param>
    [TestMethod]
    [DataRow("--base-port", "-1")]
    [DataRow("--base-port", "65517")]
    [DataRow("--base-testing-port", "-1")]
    [DataRow("--base-testing-port", "65517")]
    public async Task InitRejectsInvalidPortBasesBeforeProvisioning(string option, string value)
    {
        string home = CreateDirectory();
        ProcessResult result = await InvokeAsync(["init", "--home", home, option, value], context.CancellationToken);
        Assert.AreEqual(1, result.ExitCode, result.StandardError);
        Assert.Contains("Port bases", result.StandardError);
        Assert.IsEmpty(Directory.GetFileSystemEntries(home));
    }

    /// <summary>
    /// Failed registration and cancellation preserve both settings, and an explicit replacement can repair a bad saved value.
    /// </summary>
    [TestMethod]
    public async Task PortRegistrationFailuresPreserveConfiguration()
    {
        CancellationToken token = context.CancellationToken;
        var registry = new PostgresRegistry(CreateDirectory());
        Dictionary<int, string> paths = new() { [s_installation.Version.Major] = s_installation.PgConfigPath };
        await registry.RegisterAsync(paths, new PostgresPortOptions(30000, 31000), token);
        string original = await File.ReadAllTextAsync(registry.ConfigurationPath, token);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => registry.RegisterAsync(paths,
            new PostgresPortOptions(32000, 33000), canceled.Token));
        Assert.AreEqual(original, await File.ReadAllTextAsync(registry.ConfigurationPath, token));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => registry.RegisterAsync(
            new Dictionary<int, string> { [DifferentMajor()] = s_installation.PgConfigPath }, new PostgresPortOptions(32000, 33000), token));
        Assert.AreEqual(original, await File.ReadAllTextAsync(registry.ConfigurationPath, token));
        const string Invalid = "{\"basePort\":\"bad\",\"baseTestingPort\":31000,\"custom\":true}";
        await File.WriteAllTextAsync(registry.ConfigurationPath, Invalid, token);
        await Assert.ThrowsExactlyAsync<FormatException>(() => registry.RegisterAsync(paths, new PostgresPortOptions(baseTestingPort: 33000), token));
        Assert.AreEqual(Invalid, await File.ReadAllTextAsync(registry.ConfigurationPath, token));
        await registry.RegisterAsync(paths, new PostgresPortOptions(basePort: 30000), token);
        Assert.AreEqual(30000 + s_installation.Version.Major, registry.GetPort(s_installation.Version.Major));
        Assert.AreEqual(31000 + s_installation.Version.Major, registry.GetTestPort(s_installation.Version.Major));
        Assert.IsEmpty(Directory.GetFiles(registry.HomeDirectory, "*.tmp"));
    }

    /// <summary>
    /// Saved bases affect future starts, while an active server and explicit overrides retain their exact selected ports and data.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task DevelopmentPortsHonorSavedBasesRunningStateAndOverrides()
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        var registry = new PostgresRegistry(home);
        var cluster = new PostgresDevelopmentCluster(s_installation, home);
        using PortReservation first = ReserveConfigurablePort();
        using PortReservation second = ReserveConfigurablePort();
        using PortReservation explicitPort = ReserveConfigurablePort();
        string[] connect = ["connect", "--home", home, "--pg", MajorText(), "--database", "port_probe"];
        string[] query = ["--", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c", "SELECT value, current_setting('port') FROM retained"];
        try
        {
            ProcessResult init = await InvokeAsync(["init", "--home", home, s_postgresOption, s_installation.PgConfigPath,
                "--base-port", (first.Port - s_installation.Version.Major).ToString(CultureInfo.InvariantCulture)], token);
            Assert.AreEqual(0, init.ExitCode, init.StandardError);
            first.Dispose();
            ProcessResult started = await InvokeAsync([.. connect, "--", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c",
                "CREATE TABLE retained(value integer); INSERT INTO retained VALUES(42); SELECT current_setting('port')"], token);
            Assert.AreEqual(0, started.ExitCode, started.StandardError);
            Assert.EndsWith(first.Port.ToString(CultureInfo.InvariantCulture) + Environment.NewLine, started.StandardOutput);
            ProcessResult changed = await InvokeAsync(["init", "--home", home, s_postgresOption, s_installation.PgConfigPath,
                "--base-port", (second.Port - s_installation.Version.Major).ToString(CultureInfo.InvariantCulture)], token);
            Assert.AreEqual(0, changed.ExitCode, changed.StandardError);
            ProcessResult live = await InvokeAsync([.. connect, .. query], token);
            Assert.AreEqual(0, live.ExitCode, live.StandardError);
            Assert.EndsWith($"42|{first.Port}" + Environment.NewLine, live.StandardOutput);
            await cluster.StopAsync(token);
            second.Dispose();
            ProcessResult restarted = await InvokeAsync([.. connect, .. query], token);
            Assert.AreEqual(0, restarted.ExitCode, restarted.StandardError);
            Assert.EndsWith($"42|{second.Port}" + Environment.NewLine, restarted.StandardOutput);
            await cluster.StopAsync(token);
            explicitPort.Dispose();
            ProcessResult overridden = await InvokeAsync([.. connect, "--port", explicitPort.Port.ToString(CultureInfo.InvariantCulture), .. query], token);
            Assert.AreEqual(0, overridden.ExitCode, overridden.StandardError);
            Assert.EndsWith($"42|{explicitPort.Port}" + Environment.NewLine, overridden.StandardOutput);
            Assert.AreEqual(second.Port, registry.GetPort(s_installation.Version.Major));
            await cluster.StopAsync(token);
            ProcessResult saved = await InvokeAsync([.. connect, .. query], token);
            Assert.AreEqual(0, saved.ExitCode, saved.StandardError);
            Assert.EndsWith($"42|{second.Port}" + Environment.NewLine, saved.StandardOutput);
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    private static PortReservation ReserveConfigurablePort()
        => TestPortReservations.Create();
}
