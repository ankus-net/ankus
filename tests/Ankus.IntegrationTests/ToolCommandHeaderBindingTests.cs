using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// The packaged frontend preserves selected PostgreSQL header types independently of reference-platform declarations.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolCollectsSelectedHeaderTypes()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "native header types");
        string selection = Path.Join(s_root, "native header symbols.txt");
        string[] names = ["ConditionVariableSleep", "ExecutorRun", "ExecutorRun_hook", "TYPEALIGN", "pg_atomic_read_u32", "pg_popcount32", "proc_exit"];
        await File.WriteAllLinesAsync(selection, names, token);
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-header-types", selection, MajorText(), s_installation.PgConfigPath, output], token);
        result.EnsureSuccess("dotnet", [helper, "binding-header-types"]);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Join(output, "native-header-types.json"), token));
        JsonElement target = document.RootElement.GetProperty("Target");
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::integer", connection);
        Assert.AreEqual(await command.ExecuteScalarAsync(token), target.GetProperty("PostgresVersion").GetInt32());
        Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, target.GetProperty("RuntimeIdentifier").GetString());
        Assert.AreEqual(IntPtr.Size, target.GetProperty("PointerSize").GetInt32());
        Assert.AreEqual(BitConverter.IsLittleEndian, target.GetProperty("IsLittleEndian").GetBoolean());
        Assert.IsGreaterThan(0, target.GetProperty("ClangMajor").GetInt32());
        JsonElement symbols = document.RootElement.GetProperty("Symbols");
        Assert.AreSequenceEqual(names, symbols.EnumerateObject().Select(static property => property.Name));

        JsonElement alignment = symbols.GetProperty("TYPEALIGN");
        Assert.IsTrue(alignment.GetProperty("IsFunction").GetBoolean());
        Assert.AreEqual("ankus_header_TYPEALIGN", alignment.GetProperty("NativeName").GetString());
        Assert.AreSequenceEqual<string?>(["alignment", "value"], alignment.GetProperty("ParameterNames").EnumerateArray().Select(static name => name.GetString()));
        JsonElement alignmentType = alignment.GetProperty("Type");
        Assert.AreEqual("function", alignmentType.GetProperty("Kind").GetString());
        Assert.AreEqual("uintptr_t", alignmentType.GetProperty("Result").GetProperty("Name").GetString());
        Assert.HasCount(2, alignmentType.GetProperty("Parameters").EnumerateArray());

        JsonElement executor = symbols.GetProperty("ExecutorRun");
        Assert.IsTrue(executor.GetProperty("IsFunction").GetBoolean());
        Assert.AreEqual("ExecutorRun", executor.GetProperty("NativeName").GetString());
        string[] parameters = s_installation.Version.Major >= 18
            ? ["queryDesc", "direction", "count"] : ["queryDesc", "direction", "count", "execute_once"];
        Assert.AreSequenceEqual(parameters, executor.GetProperty("ParameterNames").EnumerateArray().Select(static name => name.GetString()));
        JsonElement function = executor.GetProperty("Type");
        Assert.AreEqual("function", function.GetProperty("Kind").GetString());
        Assert.AreEqual(parameters.Length, function.GetProperty("Parameters").GetArrayLength());
        Assert.AreEqual("void", function.GetProperty("Result").GetProperty("Name").GetString());
        Assert.IsTrue(function.GetProperty("HasPrototype").GetBoolean());
        Assert.IsFalse(function.GetProperty("IsVariadic").GetBoolean());

        JsonElement hook = symbols.GetProperty("ExecutorRun_hook");
        Assert.IsFalse(hook.GetProperty("IsFunction").GetBoolean());
        JsonElement hookAlias = hook.GetProperty("Type");
        Assert.AreEqual("alias", hookAlias.GetProperty("Kind").GetString());
        Assert.AreEqual("ExecutorRun_hook_type", hookAlias.GetProperty("Name").GetString());
        JsonElement hookAddress = hookAlias.GetProperty("Underlying");
        Assert.AreEqual("pointer", hookAddress.GetProperty("Kind").GetString());
        Assert.IsTrue(JsonElement.DeepEquals(function, hookAddress.GetProperty("Element")));

        JsonElement atomic = symbols.GetProperty("pg_atomic_read_u32").GetProperty("Type");
        JsonElement argument = Assert.ContainsSingle(atomic.GetProperty("Parameters").EnumerateArray());
        Assert.AreEqual("pointer", argument.GetProperty("Kind").GetString());
        JsonElement qualified = argument.GetProperty("Element");
        Assert.AreEqual("qualified", qualified.GetProperty("Kind").GetString());
        Assert.AreEqual(2, qualified.GetProperty("Modifiers").GetInt32());
        Assert.AreEqual("pg_atomic_uint32", qualified.GetProperty("Underlying").GetProperty("Name").GetString());

        JsonElement popcount = symbols.GetProperty("pg_popcount32");
        bool isFunction = popcount.GetProperty("IsFunction").GetBoolean();
        // PostgreSQL 15 made it a plain function where no POPCNT dispatch exists; earlier majors always use a pointer.
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            Assert.AreEqual(s_installation.Version.Major >= 15, isFunction);
        }

        Assert.AreEqual("pg_popcount32", popcount.GetProperty("NativeName").GetString());
        JsonElement callable = popcount.GetProperty("Type");
        if (!isFunction)
        {
            Assert.AreEqual("pointer", callable.GetProperty("Kind").GetString());
            callable = callable.GetProperty("Element");
        }

        Assert.AreEqual("function", callable.GetProperty("Kind").GetString());
        Assert.AreEqual("int", callable.GetProperty("Result").GetProperty("Name").GetString());
        JsonElement word = Assert.ContainsSingle(callable.GetProperty("Parameters").EnumerateArray());
        Assert.AreEqual("alias", word.GetProperty("Kind").GetString());
        Assert.AreEqual("uint32", word.GetProperty("Name").GetString());
        JsonElement scalar = word.GetProperty("Underlying");
        while (scalar.GetProperty("Kind").GetString() == "alias")
        {
            scalar = scalar.GetProperty("Underlying");
        }

        Assert.AreEqual("scalar", scalar.GetProperty("Kind").GetString());
        Assert.AreEqual("unsigned int", scalar.GetProperty("Name").GetString());

        JsonElement condition = symbols.GetProperty("ConditionVariableSleep").GetProperty("Type")
            .GetProperty("Parameters")[0].GetProperty("Element");
        Assert.AreEqual("alias", condition.GetProperty("Kind").GetString());
        Assert.AreEqual("ConditionVariable", condition.GetProperty("Name").GetString());
        JsonElement record = condition.GetProperty("Underlying");
        Assert.AreEqual("record", record.GetProperty("Kind").GetString());
        Assert.AreEqual("", record.GetProperty("Name").GetString());
        Assert.IsFalse(record.GetProperty("IsUnion").GetBoolean());
        // PG17 and earlier omit this annotation under MSVC; PG18 uses C11 _Noreturn.
        bool noReturn = s_installation.Version.Major >= 18 || !OperatingSystem.IsWindows();
        Assert.AreEqual(noReturn, symbols.GetProperty("proc_exit").GetProperty("DoesNotReturn").GetBoolean());
    }

    /// <summary>
    /// A header target mismatch cannot replace an existing validated type contract.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolPreservesHeaderContractOnRuntimeMismatch()
    {
        CancellationToken token = context.CancellationToken;
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "mismatched header runtime");
        Directory.CreateDirectory(output);
        string contract = Path.Join(output, "native-header-types.json");
        byte[] expected = "existing header contract\n"u8.ToArray();
        await File.WriteAllBytesAsync(contract, expected, token);
        string selection = Path.Join(output, "symbols.txt");
        await File.WriteAllTextAsync(selection, "ExecutorRun\n", token);
        string otherRuntime = RuntimeInformation.RuntimeIdentifier == "linux-x64" ? "linux-arm64" : "linux-x64";
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-header-types", selection, MajorText(), s_installation.PgConfigPath, output, "", "", otherRuntime], token);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("Native header types do not match the requested runtime ABI", result.StandardError);
        Assert.AreSequenceEqual(expected, await File.ReadAllBytesAsync(contract, token));
    }

    /// <summary>
    /// A selected installation with the wrong major is rejected before header collection creates output.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolRejectsMismatchedHeaderMajor()
    {
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "mismatched header major");
        string selection = Path.Join(s_root, "mismatched header symbols.txt");
        await File.WriteAllTextAsync(selection, "ExecutorRun\n", context.CancellationToken);
        int otherMajor = s_installation.Version.Major == 18 ? 17 : 18;
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-header-types", selection, otherMajor.ToString(CultureInfo.InvariantCulture),
                s_installation.PgConfigPath, output], context.CancellationToken);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains("The selected installation has the wrong PostgreSQL major", result.StandardError);
        Assert.IsFalse(Directory.Exists(output));
    }
}
