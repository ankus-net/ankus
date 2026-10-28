using System;

namespace Ankus.Testing;

/// <summary>
/// Reports a backend test failure together with its PostgreSQL session log and original exception.
/// </summary>
/// <remarks>
/// Initializes a failure for a named PostgreSQL test session.
/// </remarks>
/// <param name="testName">The name of the failed test.</param>
/// <param name="serverLog">The log emitted by the test's backend session.</param>
/// <param name="innerException">The original test or database exception.</param>
public sealed class PostgresTestException(string testName, string serverLog, Exception innerException)
    : Exception($"PostgreSQL test '{testName}' failed: {innerException.Message}{Environment.NewLine}{serverLog}", innerException)
{
    /// <summary>
    /// Gets the name of the failed test.
    /// </summary>
    public string TestName { get; } = testName;

    /// <summary>
    /// Gets the server log associated with this test session.
    /// </summary>
    public string ServerLog { get; } = serverLog;
}
