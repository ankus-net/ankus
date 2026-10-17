using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Limits build-intensive child processes without blocking unrelated tool tests.
/// </summary>
internal static class PackageProcessRunner
{
    private static readonly SemaphoreSlim s_slots = new(
        IntegrationEnvironment.PackageTestConcurrency,
        IntegrationEnvironment.PackageTestConcurrency);
    private static readonly ConcurrentQueue<int> s_processorCounts = new(
        CreateProcessorBudgets(Environment.ProcessorCount, IntegrationEnvironment.PackageTestConcurrency));

    /// <summary>
    /// Runs a child process while reserving capacity only for build-intensive commands.
    /// </summary>
    internal static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken,
        bool captureOutput = true,
        string? workingDirectory = null)
    {
        bool reserve = RequiresSlot(fileName, arguments);
        int processorCount = 0;
        long requested = Stopwatch.GetTimestamp();
        if (reserve)
        {
            await s_slots.WaitAsync(cancellationToken);
            if (!s_processorCounts.TryDequeue(out processorCount))
            {
                s_slots.Release();
                throw new InvalidOperationException("An acquired compiler slot has no processor budget.");
            }
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            IReadOnlyDictionary<string, string?> processEnvironment = reserve
                ? CreateBuildEnvironment(environment, processorCount)
                : environment;
            return await ProcessRunner.RunAsync(
                fileName,
                arguments,
                processEnvironment,
                cancellationToken,
                captureOutput,
                workingDirectory);
        }
        finally
        {
            ProcessTimings.Record(fileName, arguments, Stopwatch.GetElapsedTime(requested, started), Stopwatch.GetElapsedTime(started));
            if (reserve)
            {
                s_processorCounts.Enqueue(processorCount);
                s_slots.Release();
            }
        }
    }

    private static Dictionary<string, string?> CreateBuildEnvironment(
        IReadOnlyDictionary<string, string?> environment, int processorCount)
    {
        Dictionary<string, string?> result = new(environment.Count + 2, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string? value) in environment)
        {
            result[name] = value;
        }

        result["MSBUILDDISABLENODEREUSE"] = "1";
        result["DOTNET_PROCESSOR_COUNT"] = processorCount.ToString(CultureInfo.InvariantCulture);
        return result;
    }

    /// <summary>
    /// Divides the full logical-processor budget across build slots without discarding the remainder.
    /// </summary>
    /// <param name="logicalProcessors">The host's effective processor count.</param>
    /// <param name="concurrency">The configured number of simultaneous compiler processes.</param>
    /// <returns>One positive processor budget for each build slot, differing by at most one.</returns>
    internal static int[] CreateProcessorBudgets(int logicalProcessors, int concurrency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(logicalProcessors, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        int processors = Math.Max(1, logicalProcessors / concurrency);
        int remainder = logicalProcessors >= concurrency ? logicalProcessors % concurrency : 0;
        int[] result = new int[concurrency];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = processors + (index < remainder ? 1 : 0);
        }

        return result;
    }

    /// <summary>
    /// Runs a child process and requires a successful exit code.
    /// </summary>
    internal static async Task<ProcessResult> RunCheckedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken,
        bool captureOutput = true,
        string? workingDirectory = null)
    {
        ProcessResult result = await RunAsync(
            fileName,
            arguments,
            environment,
            cancellationToken,
            captureOutput,
            workingDirectory);
        result.EnsureSuccess(fileName, arguments);
        return result;
    }

    /// <summary>
    /// Reserves compiler capacity for commands that can build, preserving capacity for actual compilation.
    /// </summary>
    internal static bool RequiresSlot(string fileName, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return false;
        }

        string executable = Path.GetFileNameWithoutExtension(fileName);
        string command = arguments[0];
        if (string.Equals(executable, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // A test host can publish its extension from fixture initialization, even with --no-build.
            return command is "build" or "msbuild" or "pack" or "publish" or "restore" or "run" or "test" or "tool";
        }

        if (!string.Equals(executable, "ankus", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        bool skipsBuild = command is "bench" or "regress" or "run" or "schema" && EnabledOption(arguments, "--no-build") ||
            command == "schema" && EnabledOption(arguments, "--skip-build");
        bool onlyReports = command == "bench" && EnabledOption(arguments, "--report") ||
            command == "regress" && EnabledOption(arguments, "--dry-run");
        bool usesPackage = command is "get" or "install" or "package" or "schema" && HasOption(arguments, "--from");
        if (skipsBuild || onlyReports || usesPackage)
        {
            return false;
        }

        // A project property read runs the control-file build target, so it competes for the same processors.
        return command is "bench" or "build" or "get" or "install" or "package" or "publish" or "regress" or "run" or "schema" or "test";
    }

    /// <summary>
    /// Recognizes a supplied publication option before any forwarded command arguments.
    /// </summary>
    private static bool HasOption(IReadOnlyList<string> arguments, string name)
    {
        for (int index = 1; index < arguments.Count && arguments[index] != "--"; index++)
        {
            string argument = arguments[index];
            if (argument == name || argument.StartsWith(name + "=", StringComparison.Ordinal) ||
                argument.StartsWith(name + ":", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads a boolean tool option without treating forwarded client arguments as tool options.
    /// </summary>
    private static bool EnabledOption(IReadOnlyList<string> arguments, string name)
    {
        bool enabled = false;
        for (int index = 1; index < arguments.Count && arguments[index] != "--"; index++)
        {
            string argument = arguments[index];
            if (argument == name)
            {
                enabled = index + 1 >= arguments.Count || !bool.TryParse(arguments[index + 1], out bool value) || value;
            }
            else if ((argument.StartsWith(name + "=", StringComparison.Ordinal) || argument.StartsWith(name + ":", StringComparison.Ordinal)) &&
                !bool.TryParse(argument[(name.Length + 1)..], out enabled))
            {
                return false;
            }
        }

        return enabled;
    }
}
