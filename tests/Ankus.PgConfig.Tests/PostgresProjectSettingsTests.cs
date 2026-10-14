namespace Ankus.PgConfig.Tests;

/// <summary>
/// Checks how a solution's projects combine into one PostgreSQL selection when they are evaluated side by side.
/// </summary>
/// <param name="context">The test context.</param>
[TestClass]
public sealed class PostgresProjectSettingsTests(TestContext context)
{
    /// <summary>
    /// Projects that select nothing inherit the selection of the extension they all reference.
    /// </summary>
    [TestMethod]
    public async Task ProjectsInheritTheirSharedReference()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-project-selection-").FullName;
        try
        {
            Write(directory, "Extension", "<PropertyGroup><AnkusPostgresMajor>16</AnkusPostgresMajor></PropertyGroup>");
            List<string> projects = [Path.Join(directory, "Extension.csproj")];
            for (int index = 0; index < 12; index++)
            {
                projects.Add(Write(directory, "Tests" + index, "<ItemGroup><ProjectReference Include=\"Extension.csproj\" /></ItemGroup>"));
            }

            PostgresProjectSettings? settings = await PostgresProjectSettings.TryReadAsync(projects, "Debug",
                cancellationToken: context.CancellationToken);
            Assert.IsNotNull(settings);
            Assert.AreEqual(16, settings.PostgresMajor);
            Assert.IsTrue(settings.HasExplicitPostgresMajor);
            Assert.IsNull(settings.PgConfigPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The first problem in solution order is reported, whichever evaluation finishes first.
    /// </summary>
    /// <param name="conflictFirst">Whether the conflicting project precedes the invalid one.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task TheFirstProblemInSolutionOrderIsReported(bool conflictFirst)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-project-selection-").FullName;
        try
        {
            Write(directory, "Thirteen", "<PropertyGroup><AnkusPostgresMajor>13</AnkusPostgresMajor></PropertyGroup>");
            Write(directory, "Nineteen", "<PropertyGroup><AnkusPostgresMajor>19</AnkusPostgresMajor></PropertyGroup>");
            string conflict = Write(directory, "Conflict",
                "<ItemGroup><ProjectReference Include=\"Thirteen.csproj\" /><ProjectReference Include=\"Nineteen.csproj\" /></ItemGroup>");
            string invalid = Write(directory, "Invalid", "<PropertyGroup><AnkusPostgresMajor>banana</AnkusPostgresMajor></PropertyGroup>");
            string[] projects = conflictFirst ? [conflict, invalid] : [invalid, conflict];

            Task read = PostgresProjectSettings.TryReadAsync(projects, "Debug", cancellationToken: context.CancellationToken);
            if (conflictFirst)
            {
                InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => read);
                Assert.Contains("different PostgreSQL installations", error.Message);
            }
            else
            {
                FormatException error = await Assert.ThrowsExactlyAsync<FormatException>(() => read);
                Assert.Contains("AnkusPostgresMajor must select PostgreSQL 13–19", error.Message);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Writes a project file with no SDK, which MSBuild evaluates without restoring anything. It first clears the
    /// selection that CI exports as environment variables, which MSBuild would otherwise read as properties.
    /// </summary>
    private static string Write(string directory, string name, string body)
    {
        string path = Path.Join(directory, name + ".csproj");
        File.WriteAllText(path, "<Project><PropertyGroup><AnkusPostgresMajor /><AnkusPgConfigPath /></PropertyGroup>" + body + "</Project>");
        return path;
    }
}
