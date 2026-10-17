namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies what a failed PostgreSQL source build reports and where the build looks for pkg-config.
/// </summary>
[TestClass]
public sealed class PostgresBuildFailureTests
{
    /// <summary>
    /// A configure failure leads with configure's own reason, which used to follow a hundred or so "checking for" lines,
    /// keeps the rest of its error, and names the file that keeps the output and config.log.
    /// </summary>
    [TestMethod]
    public void ConfigureFailureLeadsWithItsCause()
    {
        const string Output = "checking for gcc... gcc\nchecking for library containing readline... no\n";
        const string Error = "configure: error: readline library not found\nIf you have readline already installed, see config.log for details on the\n" +
            "failure.  It is possible the compiler isn't looking in the proper directory.\nUse --without-readline to disable readline support.\n";
        string message = PostgresProvisioner.DescribeFailure("configure", 1, Output, Error, "/home/user/.ankus/postgres/18.6-0.build.log");
        string[] lines = message.Split('\n');
        Assert.AreEqual("PostgreSQL's configure failed (1): readline library not found", lines[0]);
        Assert.Contains("Use --without-readline to disable readline support.", message);
        Assert.AreEqual("The output and config.log are in /home/user/.ankus/postgres/18.6-0.build.log.", lines[^1]);
        Assert.DoesNotContain("checking for", message);
    }

    /// <summary>
    /// A make failure leads with the first compiler error rather than make's own summary.
    /// </summary>
    [TestMethod]
    public void MakeFailureLeadsWithTheCompilerError()
    {
        const string Error = "plancat.c:12:1: warning: unused variable 'x'\nheap.c:40:5: error: unknown type name 'Relationx'\n" +
            "make[2]: *** [heap.o] Error 1\nmake: *** [all] Error 2\n";
        string message = PostgresProvisioner.DescribeFailure("make", 2, "", Error, null);
        Assert.StartsWith("PostgreSQL's make failed (2): heap.c:40:5: error: unknown type name 'Relationx'\n", message);
        Assert.DoesNotContain("config.log", message);
    }

    /// <summary>
    /// Output with no error line still reports the command, exit code and output.
    /// </summary>
    [TestMethod]
    public void FailureWithoutAnErrorLineReportsTheOutput()
    {
        string message = PostgresProvisioner.DescribeFailure("make", 2, "Killed\n", "", null);
        Assert.AreEqual("PostgreSQL's make failed (2).\nKilled", message);
    }

    /// <summary>
    /// The first pkg-config found in the given directories is used, and none is found when no directory has one.
    /// </summary>
    [TestMethod]
    public void FindsPkgConfigInTheGivenDirectories()
    {
        string root = Directory.CreateTempSubdirectory("ankus pkg-config ").FullName;
        try
        {
            string empty = Directory.CreateDirectory(Path.Join(root, "empty")).FullName;
            string homebrew = Directory.CreateDirectory(Path.Join(root, "homebrew")).FullName;
            string macports = Directory.CreateDirectory(Path.Join(root, "macports")).FullName;
            Assert.IsNull(PostgresProvisioner.FindPkgConfig([empty, homebrew, macports]));
            File.WriteAllText(Path.Join(macports, "pkg-config"), "");
            Assert.AreEqual(Path.Join(macports, "pkg-config"), PostgresProvisioner.FindPkgConfig([empty, homebrew, macports]));
            File.WriteAllText(Path.Join(homebrew, "pkg-config"), "");
            Assert.AreEqual(Path.Join(homebrew, "pkg-config"), PostgresProvisioner.FindPkgConfig([empty, homebrew, macports]));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
