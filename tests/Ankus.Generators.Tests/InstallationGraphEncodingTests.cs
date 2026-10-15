using System.Globalization;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies detached embedded graph encoding against the real consumer and allocation boundaries.
/// </summary>
[TestClass]
public sealed class InstallationGraphEncodingTests
{
    /// <summary>
    /// Empty and populated graphs retain the exact versioned format understood by artifact consumers.
    /// </summary>
    /// <param name="populated">Whether the graph contains an installable custom SQL declaration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InstallationGraphEncodingRoundTripsWithConsumer(bool populated)
    {
        InstallationGraphEncoding.Output output = InstallationGraphEncoding.Encode(new(populated ? [Node("SELECT 1;\n")] : []));
        Assert.IsNull(output.Limit);
        Assert.IsNotNull(output.Graph);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(output.Graph);
        Assert.HasCount(populated ? 1 : 0, graph.Items);
        if (populated)
        {
            Assert.AreEqual("SELECT 1;\n", Assert.ContainsSingle(graph.Items).Sql);
            Assert.AreSequenceEqual(["answer"], Assert.ContainsSingle(graph.Items).Names);
        }
    }

    /// <summary>
    /// The wire allocation limit accepts immediately adjacent valid sizes and fails closed one byte above it.
    /// </summary>
    /// <param name="offset">The byte offset from the 32 MiB embedded graph limit.</param>
    /// <param name="multibyte">Whether SQL contains multibyte UTF-8 text.</param>
    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(-1, true)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    public void InstallationGraphEncodingEnforcesExactByteLimit(int offset, bool multibyte)
    {
        InstallationGraphEncoding.Output baseline = InstallationGraphEncoding.Encode(new([Node(string.Empty)]));
        Assert.IsNotNull(baseline.Graph);
        int fixedBytes = Convert.FromBase64String(baseline.Graph).Length;
        const int Limit = 32 * 1024 * 1024;
        int payload = Limit - fixedBytes + offset;
        string sql = multibyte ? new string('é', payload / 2) + (payload % 2 == 0 ? string.Empty : "x") : new string('x', payload);
        InstallationGraphEncoding.Output output = InstallationGraphEncoding.Encode(new([Node(sql)]));
        if (offset > 0)
        {
            Assert.IsNull(output.Graph);
            Assert.AreEqual(InstallationGraphLimit.Size, output.Limit);
        }
        else
        {
            Assert.IsNull(output.Limit);
            Assert.IsNotNull(output.Graph);
            Assert.HasCount(Limit + offset, Convert.FromBase64String(output.Graph));
            Assert.AreEqual(sql, Assert.ContainsSingle(ExtensionSchemaGraph.Parse(output.Graph).Items).Sql);
        }
    }

    /// <summary>
    /// Declaration and per-field counts preserve the consumer's exact 100,000-entry boundary.
    /// </summary>
    /// <param name="field">Whether the count applies to one alias field instead of graph declarations.</param>
    /// <param name="count">The declaration or alias count.</param>
    [TestMethod]
    [DataRow(false, 100_000)]
    [DataRow(false, 100_001)]
    [DataRow(true, 100_000)]
    [DataRow(true, 100_001)]
    public void InstallationGraphEncodingEnforcesExactCountLimits(bool field, int count)
    {
        string[] names = [.. Enumerable.Range(0, count).Select(static index => "n" + index.ToString(CultureInfo.InvariantCulture))];
        EquatableArray<InstallationGraphModel.EncodedNode> nodes = field ? new([Node("SELECT 1;\n") with { Names = new(names) }]) :
            new(names.Select(static name => Node("SELECT 1;\n") with { Key = "sql:" + name, Names = new([name]) }));
        InstallationGraphEncoding.Output output = InstallationGraphEncoding.Encode(nodes);
        if (count > 100_000)
        {
            Assert.IsNull(output.Graph);
            Assert.AreEqual(field ? InstallationGraphLimit.Entries : InstallationGraphLimit.Declarations, output.Limit);
        }
        else
        {
            Assert.IsNull(output.Limit);
            Assert.IsNotNull(output.Graph);
            ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(output.Graph);
            Assert.HasCount(field ? 1 : count, graph.Items);
            if (field)
            {
                Assert.AreSequenceEqual(names.OrderBy(static name => name, StringComparer.Ordinal), Assert.ContainsSingle(graph.Items).Names);
            }
            else
            {
                Assert.AreSequenceEqual(names.Select(static name => "sql:" + name), graph.Items.Select(static item => item.Id));
            }
        }
    }

    /// <summary>
    /// Each exceeded bound has its own fixed, location-free extension diagnostic and guide section.
    /// </summary>
    /// <param name="limit">The exceeded encoding bound.</param>
    /// <param name="id">The fixed diagnostic ID.</param>
    /// <param name="message">The complete fixed message.</param>
    [TestMethod]
    [DataRow("Declarations", "ANKUS512", "The embedded installation graph cannot exceed 100,000 SQL declarations; split the extension")]
    [DataRow("Entries", "ANKUS513",
        "An embedded installation declaration cannot have more than 100,000 names, dependencies or attachments; split the declaration")]
    [DataRow("Size", "ANKUS514", "The embedded installation graph cannot exceed 32 MiB; reduce or split its installation SQL")]
    public void InstallationGraphLimitsHaveFixedDiagnostics(string limit, string id, string message)
    {
        DiagnosticDescriptor descriptor = SqlGraphDiagnostics.Descriptor(Enum.Parse<InstallationGraphLimit>(limit));
        Diagnostic diagnostic = Diagnostic.Create(descriptor, null);
        Assert.AreEqual(id, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(message, diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/custom-sql/#dependency-graph-diagnostics", descriptor.HelpLinkUri);
        Assert.AreEqual(Location.None, diagnostic.Location);
    }

    /// <summary>
    /// Supplies a valid detached custom SQL declaration to the encoder.
    /// </summary>
    /// <param name="sql">The exact installation text.</param>
    /// <returns>The encoding fields for one standalone node.</returns>
    private static InstallationGraphModel.EncodedNode Node(string sql)
        => new("sql:answer", "sql", sql, string.Empty, new EquatableArray<string>(["answer"]), new EquatableArray<string>([]), new EquatableArray<string>([]));
}
