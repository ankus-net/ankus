using System.Globalization;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Project properties select exact native code and default databases while client arguments remain client arguments.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task ProjectGlobalPropertiesReachRunConnectAndRegression()
    {
        CancellationToken token = context.CancellationToken;
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token);
        string home = CreateDirectory();
        string directory = CreateDirectory();
        string project = Path.Join(directory, "PropertyFlow.csproj");
        XDocument.Load(s_project).Save(project);
        await File.WriteAllTextAsync(Path.Join(directory, "Functions.cs"), """
            using Ankus;
            #if !NET10_0 || !PROPERTY_WITNESS || !EXTRA_WITNESS
            #error Framework and forwarded consumer constants must be retained.
            #endif
            public static class Functions
            {
                [PgFunction]
                public static int PropertyValue()
                {
            #if PROPERTY_WITNESS && EXTRA_WITNESS
                    return 42;
            #else
                    return -1;
            #endif
                }

                [PgFunction]
                public static int PropertyHeaderMajor() =>
            #if ANKUS_PG13
                    13;
            #elif ANKUS_PG14
                    14;
            #elif ANKUS_PG15
                    15;
            #elif ANKUS_PG16
                    16;
            #elif ANKUS_PG17
                    17;
            #elif ANKUS_PG18
                    18;
            #elif ANKUS_PG19
                    19;
            #else
            #error The selected PostgreSQL header constant must be retained.
            #endif
            }
            """, token);
        string suite = Path.Join(directory, "pg_regress");
        await WriteRegressionCaseAsync(suite, "setup", "CREATE EXTENSION ankus_property_flow;", "", token);
        await WriteRegressionCaseAsync(suite, "native", "SELECT property_value(); SELECT current_database(); SELECT property_header_major();",
            "42\nankus_property_flow_regress\n" + MajorText() + "\n", token);
        var cluster = new PostgresDevelopmentCluster(owner.Installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        string[] options = ["--home", home, "--project", project, "--port", port.ToString(CultureInfo.InvariantCulture),
            "--property", "AnkusPgConfigPath=" + owner.Installation.PgConfigPath,
            "--property", "AnkusPostgresMajor=" + MajorText(), "--property", "Configuration=Shipping+Checked",
            "--property", "AnkusExtensionName=ankus_property_flow",
            "--property", "DefineConstants=PROPERTY_WITNESS;EXTRA_WITNESS"];
        try
        {
            ProcessResult run = await InvokeAsync(["run", .. options, "--", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1",
                "-c", "CREATE EXTENSION ankus_property_flow; SELECT property_value(), property_header_major();"], token);
            Assert.AreEqual(0, run.ExitCode, run.StandardOutput + run.StandardError);
            Assert.Contains("Created database ankus_property_flow", run.StandardOutput);
            Assert.EndsWith("42|" + MajorText() + Environment.NewLine, run.StandardOutput);
            ProcessResult connect = await InvokeAsync(["connect", .. options, "--", "-X", "-A", "-t",
                "-c", "SELECT current_database(), property_value(), property_header_major();"], token);
            Assert.AreEqual(0, connect.ExitCode, connect.StandardOutput + connect.StandardError);
            Assert.Contains("Reusing database ankus_property_flow", connect.StandardOutput);
            Assert.EndsWith("ankus_property_flow|42|" + MajorText() + Environment.NewLine, connect.StandardOutput);
            // regress builds the extension itself here; the source fails to compile unless both forwarded constants arrive.
            ProcessResult regress = await InvokeAsync(["regress", .. options], token);
            Assert.AreEqual(0, regress.ExitCode, regress.StandardOutput + regress.StandardError);
            Assert.Contains("Created database ankus_property_flow_regress", regress.StandardOutput);
            Assert.AreEqual("\\set ECHO none\n42\nankus_property_flow_regress\n" + MajorText() + "\n",
                (await File.ReadAllTextAsync(Path.Join(suite, "results", "native.out"), token)).ReplaceLineEndings("\n"));
            Assert.IsTrue(await cluster.IsRunningAsync(token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Installed metadata queries preserve literal separators and empty values without publishing native code.
    /// </summary>
    /// <param name="value">The literal global property value.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("literal;comma,%3B,$(Other),@(Items),'quoted'=tail")]
    public async Task ProjectGlobalPropertyQueriesPreserveLiteralValues(string value)
    {
        string directory = CreateDirectory();
        string project = Path.Join(directory, "PropertyLiteral.csproj");
        await File.WriteAllTextAsync(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Other>must-not-expand</Other>
                <AnkusPostgresMajor>18</AnkusPostgresMajor>
                <AnkusExtensionName>default_name</AnkusExtensionName>
              </PropertyGroup>
            </Project>
            """, context.CancellationToken);
        ProcessResult query = await InvokeAsync(["get", "extname", "--project", project,
            "--property", "AnkusExtensionName=" + value], context.CancellationToken);
        Assert.AreEqual(0, query.ExitCode, query.StandardError);
        Assert.AreEqual((value.Length == 0 ? "propertyliteral" : value) + Environment.NewLine, query.StandardOutput);
        Assert.IsFalse(Directory.Exists(Path.Join(directory, "bin")));
        Assert.IsFalse(Directory.Exists(Path.Join(directory, "obj")));
    }
}
