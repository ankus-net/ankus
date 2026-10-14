using System.Runtime.InteropServices;
using System.Text.Json;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Installed tooling measures real PostgreSQL parameter, result and global storage from selected native headers.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolMeasuresCollectedHeaderStorage()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "collected header storage");
        string selection = Path.Join(s_root, "storage symbols.txt");
        string[] names = ["ConditionVariableSleep", "CreateStatistics", "ExecutorRun_hook",
            "FullTransactionIdFromU64", "pg_atomic_read_u32", "proc_exit"];
        await File.WriteAllLinesAsync(selection, names, token);
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-storage", selection, MajorText(), s_installation.PgConfigPath, output], token);
        result.EnsureSuccess("dotnet", [helper, "binding-storage"]);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(output, "native-storage.json"), token));
        JsonElement headers = document.RootElement.GetProperty("Headers");
        JsonElement target = headers.GetProperty("Target");
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::integer", connection);
        Assert.AreEqual(await command.ExecuteScalarAsync(token), target.GetProperty("PostgresVersion").GetInt32());
        Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, target.GetProperty("RuntimeIdentifier").GetString());
        Assert.AreEqual(IntPtr.Size, target.GetProperty("PointerSize").GetInt32());
        Assert.AreEqual(BitConverter.IsLittleEndian, target.GetProperty("IsLittleEndian").GetBoolean());
        JsonElement symbols = document.RootElement.GetProperty("Symbols");
        Assert.AreSequenceEqual(names, symbols.EnumerateObject().Select(static property => property.Name));
        JsonElement atomic = symbols.GetProperty("pg_atomic_read_u32");
        AssertStorageValue(atomic.GetProperty("Result"), 4, 4, null, false);
        AssertStorageValue(Assert.ContainsSingle(atomic.GetProperty("Parameters").EnumerateArray()),
            (ulong)IntPtr.Size, (ulong)IntPtr.Size, null, null);
        Assert.AreEqual(JsonValueKind.Null, atomic.GetProperty("Global").ValueKind);
        JsonElement condition = symbols.GetProperty("ConditionVariableSleep");
        Assert.AreEqual(JsonValueKind.Null, condition.GetProperty("Result").ValueKind);
        Assert.AreEqual(2, condition.GetProperty("Parameters").GetArrayLength());
        AssertStorageValue(condition.GetProperty("Parameters")[0], (ulong)IntPtr.Size, (ulong)IntPtr.Size, null, null);
        AssertStorageValue(condition.GetProperty("Parameters")[1], 4, 4, null, false);
        JsonElement transaction = symbols.GetProperty("FullTransactionIdFromU64");
        AssertStorageValue(transaction.GetProperty("Result"), 8, 8, null, null);
        AssertStorageValue(Assert.ContainsSingle(transaction.GetProperty("Parameters").EnumerateArray()), 8, 8, null, false);
        AssertStorageValue(symbols.GetProperty("ExecutorRun_hook").GetProperty("Global"),
            (ulong)IntPtr.Size, (ulong)IntPtr.Size, null, null);
        Assert.IsEmpty(symbols.GetProperty("ExecutorRun_hook").GetProperty("Parameters").EnumerateArray());
        Assert.AreEqual(JsonValueKind.Null, symbols.GetProperty("proc_exit").GetProperty("Result").ValueKind);
        AssertStorageValue(Assert.ContainsSingle(symbols.GetProperty("proc_exit").GetProperty("Parameters").EnumerateArray()),
            4, 4, null, true);
        // PG17 and earlier omit this annotation under MSVC; PG18 uses C11 _Noreturn.
        bool noReturn = s_installation.Version.Major >= 18 || !OperatingSystem.IsWindows();
        Assert.AreEqual(noReturn, headers.GetProperty("Symbols").GetProperty("proc_exit").GetProperty("DoesNotReturn").GetBoolean());
        AssertStorageValue(symbols.GetProperty("CreateStatistics").GetProperty("Result"), 12, 4, null, null);
    }

    /// <summary>
    /// A runtime mismatch cannot replace the installed command's previously measured storage artifact.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolPreservesStorageOnRuntimeMismatch()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "mismatched storage runtime");
        Directory.CreateDirectory(output);
        string contract = Path.Join(output, "native-storage.json");
        byte[] expected = "existing storage contract\n"u8.ToArray();
        await File.WriteAllBytesAsync(contract, expected, token);
        string selection = Path.Join(output, "symbols.txt");
        await File.WriteAllTextAsync(selection, "ExecutorRun\n", token);
        string otherRuntime = RuntimeInformation.RuntimeIdentifier == "linux-x64" ? "linux-arm64" : "linux-x64";
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-storage", selection, MajorText(), s_installation.PgConfigPath, output, "", "", otherRuntime], token);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("Native header types do not match the requested runtime ABI", result.StandardError);
        Assert.AreSequenceEqual(expected, await File.ReadAllBytesAsync(contract, token));
    }

    private static void AssertStorageValue(JsonElement value, ulong? size, ulong alignment, ulong? elementSize, bool? isSigned)
    {
        Assert.AreEqual(size, value.GetProperty("Size").ValueKind == JsonValueKind.Null ? null : value.GetProperty("Size").GetUInt64());
        Assert.AreEqual(alignment, value.GetProperty("Alignment").GetUInt64());
        Assert.AreEqual(elementSize, value.GetProperty("ElementSize").ValueKind == JsonValueKind.Null ? null : value.GetProperty("ElementSize").GetUInt64());
        Assert.AreEqual(isSigned, value.GetProperty("IsSigned").ValueKind == JsonValueKind.Null ? null : value.GetProperty("IsSigned").GetBoolean());
    }
}
