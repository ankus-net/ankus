using System.Globalization;
using System.Text.Json;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// The installed SDK measures native node layouts against the selected server headers without a reference checkout.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolMeasuresSelectedNativeHeaders()
    {
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "native binding layouts");
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-layouts", MajorText(), s_installation.PgConfigPath, output], context.CancellationToken);
        result.EnsureSuccess("dotnet", [helper, "binding-layouts"]);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Join(output, "native-layout.json"), context.CancellationToken));
        JsonElement layout = document.RootElement;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::integer", connection);
        Assert.AreEqual(await command.ExecuteScalarAsync(context.CancellationToken), layout.GetProperty("PostgresVersion").GetInt32());
        Assert.AreEqual(IntPtr.Size, layout.GetProperty("PointerSize").GetInt32());
        Assert.AreEqual(OperatingSystem.IsWindows() ? 4 : IntPtr.Size, layout.GetProperty("LongSize").GetInt32());
        Assert.AreEqual(BitConverter.IsLittleEndian, layout.GetProperty("IsLittleEndian").GetBoolean());
        JsonElement types = layout.GetProperty("Types");
        Assert.AreEqual(4, types.GetProperty("Node").GetProperty("Size").GetInt32());
        AssertNativeField(types, "Node", "type_", 0, 4);
        AssertNativeField(types, "Const", "consttype", 4, 4);
        Assert.AreEqual(IntPtr.Size, types.GetProperty("Const").GetProperty("Fields").GetProperty("constvalue").GetProperty("Size").GetInt32());
        AssertNativeField(types, "ListCell", "ptr_value", 0, IntPtr.Size);
        AssertNativeField(types, "ListCell", "int_value", 0, 4);
        AssertNativeField(types, "ListCell", "oid_value", 0, 4);
        Assert.AreEqual(0, types.GetProperty("List").GetProperty("Fields").GetProperty("initial_elements").GetProperty("Size").GetInt32());
    }

    /// <summary>
    /// A requested major that differs from the selected installation fails before emitting any layout artifacts.
    /// </summary>
    [TestMethod]
    public async Task PackagedBuildToolRejectsMismatchedBindingMajor()
    {
        string helper = await ReadPackagedBuildToolAsync();
        string output = Path.Join(s_root, "mismatched binding layouts");
        int otherMajor = s_installation.Version.Major == 18 ? 17 : 18;
        ProcessResult result = await RunDotnetAsync(
            [helper, "binding-layouts", otherMajor.ToString(CultureInfo.InvariantCulture), s_installation.PgConfigPath, output],
            context.CancellationToken);
        Assert.AreEqual(1, result.ExitCode);
        Assert.Contains($"Expected PostgreSQL {otherMajor}", result.StandardError);
        Assert.Contains(s_installation.Label, result.StandardError);
        Assert.IsFalse(Directory.Exists(output));
    }

    /// <summary>
    /// Reads the installed SDK's tool location without rebuilding the shared sample's native bindings.
    /// </summary>
    private async Task<string> ReadPackagedBuildToolAsync()
    {
        // The SDK import defines this property; resolving references would execute binding compilation.
        ProcessResult evaluated = await RunDotnetAsync(
            ["msbuild", s_project, "-verbosity:quiet", "-getProperty:_AnkusBuildTool,MSBuildProjectDirectory"],
            context.CancellationToken);
        evaluated.EnsureSuccess("dotnet", ["msbuild"]);
        using JsonDocument document = JsonDocument.Parse(evaluated.StandardOutput);
        string? path = document.RootElement.GetProperty("Properties").GetProperty("_AnkusBuildTool").GetString();
        Assert.IsNotNull(path);
        string helper = Path.GetFullPath(path);
        Assert.StartsWith(s_environment["NUGET_PACKAGES"]!, helper);
        string license = await File.ReadAllTextAsync(Path.Join(Path.GetDirectoryName(helper)!, "Bindings", "LICENSE.pgrx"), context.CancellationToken);
        Assert.Contains("Permission is hereby granted", license);
        return helper;
    }

    private static void AssertNativeField(JsonElement types, string type, string name, int offset, int size)
    {
        JsonElement field = types.GetProperty(type).GetProperty("Fields").GetProperty(name);
        Assert.AreEqual(offset, field.GetProperty("Offset").GetInt32());
        Assert.AreEqual(size, field.GetProperty("Size").GetInt32());
        Assert.AreEqual(size, field.GetProperty("ElementSize").GetInt32());
    }
}
