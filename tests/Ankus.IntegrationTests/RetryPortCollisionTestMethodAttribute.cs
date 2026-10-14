using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ankus.IntegrationTests;

/// <summary>
/// Marks a test whose PostgreSQL server can lose its released port to another process before binding it, and reruns
/// the test when that happens.
/// </summary>
/// <remarks>
/// Tests release a port reservation before an Ankus command or development cluster starts PostgreSQL on it, as
/// PostgreSQL's own test clusters do. Another process can bind the port inside that window. On Windows, for example,
/// WSL's localhost relay binds each Linux listener's randomly chosen port on the host. Every attempt selects new ports
/// and directories. Only an attempt whose failures all report PostgreSQL's bind collision is rerun, so deterministic
/// failures fail on every attempt, and the final result records each superseded collision.
/// </remarks>
/// <param name="callerFilePath">The declaring source file, propagated for test source information.</param>
/// <param name="callerLineNumber">The declaring source line, propagated for test source information.</param>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RetryPortCollisionTestMethodAttribute(
    [CallerFilePath] string callerFilePath = "",
    [CallerLineNumber] int callerLineNumber = -1) : TestMethodAttribute(callerFilePath, callerLineNumber)
{
    /// <summary>
    /// Gets the total number of attempts, matching automatic-port startup in <see cref="Ankus.Testing.PostgresTestCluster"/>.
    /// </summary>
    internal const int MaximumAttempts = 3;

    /// <summary>
    /// Runs the test, rerunning attempts that failed only because PostgreSQL could not bind its selected port.
    /// </summary>
    /// <param name="testMethod">The test method to execute.</param>
    /// <returns>The results of the final attempt.</returns>
    public override async Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        TestResult[] results = await base.ExecuteAsync(testMethod).ConfigureAwait(false);
        var superseded = new StringBuilder();
        for (int attempt = 2; attempt <= MaximumAttempts && IsPortCollision(results); attempt++)
        {
            superseded.AppendLine(CultureInfo.InvariantCulture, $"Attempt {attempt - 1} lost its PostgreSQL port to another process and was rerun:")
                .AppendJoin(Environment.NewLine, results.Select(static result => result.TestFailureException?.Message)).AppendLine();
            results = await base.ExecuteAsync(testMethod).ConfigureAwait(false);
        }

        if (superseded.Length != 0)
        {
            foreach (TestResult result in results)
            {
                result.LogOutput = superseded + result.LogOutput;
            }
        }

        return results;
    }

    /// <summary>
    /// Determines whether an attempt failed, and every failure reports that PostgreSQL could not bind its selected port.
    /// </summary>
    /// <param name="results">The results of one attempt.</param>
    /// <returns><see langword="true"/> when the attempt failed only because of port collisions.</returns>
    internal static bool IsPortCollision(IReadOnlyCollection<TestResult> results)
    {
        bool collided = false;
        foreach (TestResult result in results)
        {
            if (result.Outcome is not (UnitTestOutcome.Failed or UnitTestOutcome.Timeout))
            {
                continue;
            }

            string failure = result.TestFailureException?.ToString() ?? string.Empty;
            if (!failure.Contains("could not bind IPv", StringComparison.Ordinal) ||
                !failure.Contains("Is another postmaster already running on port ", StringComparison.Ordinal))
            {
                return false;
            }

            collided = true;
        }

        return collided;
    }
}
