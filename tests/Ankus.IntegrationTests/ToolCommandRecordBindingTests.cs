using System.Runtime.InteropServices;
using System.Text.Json;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Installed tooling retains authoritative PostgreSQL record fields beside its existing semantic symbol contracts.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolCollectsTransitiveHeaderRecords()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "collected header records");
        string selection = Path.Join(s_root, "record symbols.txt");
        string[] names = ["CreateStatistics", "FullTransactionIdFromU64", "pg_atomic_read_u32"];
        await File.WriteAllLinesAsync(selection, names, token);
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-records", selection, MajorText(), s_installation.PgConfigPath, output], token);
        result.EnsureSuccess("dotnet", [helper, "binding-records"]);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(output, "native-records.json"), token));
        JsonElement graph = document.RootElement.GetProperty("Graph");
        JsonElement target = graph.GetProperty("Target");
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::integer", connection);
        Assert.AreEqual(await command.ExecuteScalarAsync(token), target.GetProperty("PostgresVersion").GetInt32());
        Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, target.GetProperty("RuntimeIdentifier").GetString());
        Assert.AreEqual(IntPtr.Size, target.GetProperty("PointerSize").GetInt32());
        Assert.AreEqual(BitConverter.IsLittleEndian, target.GetProperty("IsLittleEndian").GetBoolean());
        Assert.AreSequenceEqual(names, graph.GetProperty("Roots").EnumerateObject().Select(static value => value.Name));
        JsonElement types = graph.GetProperty("Types");
        JsonElement declarations = graph.GetProperty("Declarations");
        JsonElement addressType = Canonical(Function("CreateStatistics").GetProperty("Result").GetInt32());
        JsonElement address = declarations[addressType.GetProperty("Declaration").GetInt32()];
        Assert.AreEqual("ObjectAddress", address.GetProperty("Name").GetString());
        Assert.AreEqual(12L, address.GetProperty("Size").GetInt64());
        Assert.AreEqual(4L, address.GetProperty("Alignment").GetInt64());
        Assert.AreSequenceEqual<string?>(["classId", "objectId", "objectSubId"], address.GetProperty("Fields").EnumerateArray().Select(static field => field.GetProperty("Name").GetString()));
        Assert.AreSequenceEqual<long>([0, 32, 64], address.GetProperty("Fields").EnumerateArray().Select(static field => field.GetProperty("OffsetBits").GetInt64()));
        JsonElement transactionType = Canonical(Function("FullTransactionIdFromU64").GetProperty("Result").GetInt32());
        JsonElement transaction = declarations[transactionType.GetProperty("Declaration").GetInt32()];
        Assert.AreEqual("FullTransactionId", transaction.GetProperty("Name").GetString());
        Assert.AreEqual(8L, transaction.GetProperty("Size").GetInt64());
        JsonElement value = Assert.ContainsSingle(transaction.GetProperty("Fields").EnumerateArray());
        Assert.AreEqual("value", value.GetProperty("Name").GetString());
        Assert.AreEqual(0L, value.GetProperty("OffsetBits").GetInt64());
        Assert.AreEqual(8L, Canonical(value.GetProperty("Type").GetInt32()).GetProperty("Size").GetInt64());
        JsonElement atomicParameter = Canonical(Assert.ContainsSingle(Function("pg_atomic_read_u32").GetProperty("Parameters").EnumerateArray()).GetInt32());
        Assert.AreEqual("pointer", atomicParameter.GetProperty("Kind").GetString());
        JsonElement atomicType = Canonical(atomicParameter.GetProperty("Element").GetInt32());
        JsonElement atomic = declarations[atomicType.GetProperty("Declaration").GetInt32()];
        Assert.AreEqual(4L, atomic.GetProperty("Size").GetInt64());
        Assert.AreEqual("value", Assert.ContainsSingle(atomic.GetProperty("Fields").EnumerateArray()).GetProperty("Name").GetString());
        JsonElement signatures = document.RootElement.GetProperty("Headers").GetProperty("Symbols");
        Assert.IsTrue(signatures.GetProperty("pg_atomic_read_u32").GetProperty("IsFunction").GetBoolean());
        Assert.AreEqual("static", signatures.GetProperty("pg_atomic_read_u32").GetProperty("StorageClass").GetString());

        JsonElement Canonical(int index) => types[types[index].GetProperty("Canonical").GetInt32()];
        JsonElement Function(string name) => Canonical(graph.GetProperty("Roots").GetProperty(name).GetInt32()).GetProperty("Function");
    }

    /// <summary>
    /// A native worker load failure cannot replace an existing final record contract.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolPreservesRecordsOnLibraryFailure()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "invalid record library");
        Directory.CreateDirectory(output);
        string contract = Path.Join(output, "native-records.json");
        byte[] expected = "existing record contract\n"u8.ToArray();
        await File.WriteAllBytesAsync(contract, expected, token);
        string selection = Path.Join(output, "symbols.txt");
        await File.WriteAllTextAsync(selection, "FullTransactionIdFromU64\n", token);
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-records", selection, MajorText(), s_installation.PgConfigPath, output,
                "", "", "", "", Path.Join(output, "missing-libclang")], token);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("Native record worker exited", result.StandardError);
        Assert.AreSequenceEqual(expected, await File.ReadAllBytesAsync(contract, token));
        Assert.IsEmpty(Directory.GetFiles(output, "native-records-*.tmp"));
    }
}
