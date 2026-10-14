using System.Reflection;
using System.Text.Json;

namespace Ankus.Build.Tests;

/// <summary>
/// Selects the SDK that built the tests for temporary consumer projects.
/// </summary>
internal static class TestDotnetSdk
{
    /// <summary>
    /// Gets the exact SDK version used to build this test assembly.
    /// </summary>
    internal static string Version { get; } = ReadMetadata("AnkusTestSdkVersion");

    /// <summary>
    /// Writes an SDK selection that also locates an isolated SDK installation.
    /// </summary>
    /// <param name="directory">The temporary consumer project's directory.</param>
    /// <param name="cancellationToken">The test's cancellation token.</param>
    internal static Task ConfigureAsync(string directory, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(Path.Join(directory, "global.json"), JsonSerializer.Serialize(new
        {
            sdk = new
            {
                version = Version,
                rollForward = "disable",
                allowPrerelease = true,
                paths = new[] { Path.GetFullPath(Path.Join(ReadMetadata("AnkusTestSdkDirectory"), "..", "..")) },
            },
        }), cancellationToken);

    /// <summary>
    /// Reads one SDK identity recorded by MSBuild in the test assembly.
    /// </summary>
    /// <param name="key">The metadata key to read.</param>
    /// <returns>The required nonempty metadata value.</returns>
    private static string ReadMetadata(string key)
    {
        AssemblyMetadataAttribute item = Assert.ContainsSingle(typeof(TestDotnetSdk).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().Where(item => item.Key == key));
        Assert.IsFalse(string.IsNullOrEmpty(item.Value));
        return item.Value;
    }
}
