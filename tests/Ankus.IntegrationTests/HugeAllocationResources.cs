using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Admits explicitly requested Linux huge-allocation witnesses and bounds their backend address-space growth.
/// </summary>
internal static class HugeAllocationResources
{
    private const long Gibibyte = 1024L * 1024 * 1024;
    private const long RequiredHeadroom = 5 * Gibibyte;
    private const long AddressSpaceAllowance = 3 * Gibibyte;

    /// <summary>
    /// Records host and actual cgroup resources, temporarily limits one fresh backend, and restores its soft limit.
    /// </summary>
    /// <param name="backend">The warmed, unpooled backend process identifier.</param>
    /// <param name="mode">The allocation mode and measured server configuration retained with the evidence.</param>
    /// <param name="context">The test output and artifact context.</param>
    /// <param name="action">The bounded allocation lifecycle operation.</param>
    /// <param name="cancellationToken">Cancels admission and the limit-setting subprocess.</param>
    internal static async Task RunAsync(int backend, string mode, TestContext context, Func<Task> action, CancellationToken cancellationToken)
    {
        string directory = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs", "huge-allocations");
        Directory.CreateDirectory(directory);
        string filename = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-pid{backend}-{(mode.Contains("try=True", StringComparison.Ordinal) ? (mode.Contains("alignment=64", StringComparison.Ordinal) ? "aligned-try" : "try") : mode.Contains("alignment=64", StringComparison.Ordinal) ? "aligned" : mode.Contains("zeroed=True", StringComparison.Ordinal) ? "zeroed" : "ordinary")}.txt";
        string evidence = Path.Join(directory, filename);
        void Record(string message)
        {
            context.WriteLine(message);
            File.AppendAllText(evidence, message + Environment.NewLine);
        }

        context.WriteLine($"Huge-allocation resource evidence: {evidence}");
        Record($"Requested huge allocation; UTC={DateTime.UtcNow:O}; backend={backend}; {mode}");
        RequireLinux64(backend);
        RequireUnmodifiedAllocatorEnvironment(backend, Record);
        ResourceSnapshot before = Capture(backend, "before", Record, admission: true);
        (string soft, string hard) = await ReadLimitsAsync(backend, cancellationToken);
        long ceiling = checked(before.VirtualBytes + AddressSpaceAllowance);
        Record($"RLIMIT_AS before: soft={soft}, hard={hard}; requested soft={ceiling} (warmed VmSize + {AddressSpaceAllowance}).");
        if ((soft != "unlimited" && long.Parse(soft, CultureInfo.InvariantCulture) < ceiling) ||
            (hard != "unlimited" && long.Parse(hard, CultureInfo.InvariantCulture) < ceiling))
        {
            throw new InvalidOperationException("Existing address-space limits cannot admit the requested bounded huge-allocation allowance.");
        }

        List<Exception> failures = [];
        try
        {
            await SetSoftLimitAsync(backend, ceiling.ToString(CultureInfo.InvariantCulture), cancellationToken);
            (string boundedSoft, string boundedHard) = await ReadLimitsAsync(backend, cancellationToken);
            Record($"RLIMIT_AS applied: soft={boundedSoft}, hard={boundedHard}.");
            if (boundedSoft != ceiling.ToString(CultureInfo.InvariantCulture) || boundedHard != hard)
            {
                throw new InvalidOperationException("The backend address-space soft limit was not applied while preserving its hard limit.");
            }

            await action();
        }
        // codeql[cs/catch-of-all-exceptions] Metrics and the limit are restored whatever the test threw, and every failure is reported.
        catch (Exception error)
        {
            failures.Add(error);
        }

        // Preserve a lifecycle failure while still recording metrics and restoring the limit.
        try
        {
            if (Directory.Exists($"/proc/{backend}"))
            {
                ResourceSnapshot after = Capture(backend, "after", Record, admission: false);
                foreach (CgroupObservation previous in before.Cgroups)
                {
                    CgroupObservation current = after.Cgroups.Single(group => group.Directory == previous.Directory);
                    foreach (string name in new[] { "max", "oom", "oom_kill" })
                    {
                        long first = previous.Events.GetValueOrDefault(name);
                        long last = current.Events.GetValueOrDefault(name);
                        if (last != first)
                        {
                            throw new InvalidOperationException($"Shared cgroup event {name} changed from {first} to {last} at {previous.Directory}; huge-allocation resource evidence is not clean.");
                        }
                    }
                }
            }
            else
            {
                Record("The dedicated backend exited before final /proc metrics could be captured.");
            }
        }
        // codeql[cs/catch-of-all-exceptions] Metrics and the limit are restored whatever the test threw, and every failure is reported.
        catch (Exception error)
        {
            failures.Add(error);
        }

        try
        {
            if (Directory.Exists($"/proc/{backend}"))
            {
                using var restoration = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await SetSoftLimitAsync(backend, soft, restoration.Token);
                (string restoredSoft, string restoredHard) = await ReadLimitsAsync(backend, restoration.Token);
                Record($"RLIMIT_AS restored: soft={restoredSoft}, hard={restoredHard}.");
                if (restoredSoft != soft || restoredHard != hard)
                {
                    throw new InvalidOperationException("The dedicated backend's original address-space limits were not restored.");
                }
            }
        }
        // codeql[cs/catch-of-all-exceptions] Metrics and the limit are restored whatever the test threw, and every failure is reported.
        catch (Exception error)
        {
            failures.Add(error);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("The huge-allocation witness and its resource cleanup failed.", failures);
        }
    }

    private static void RequireLinux64(int backend)
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
        {
            throw new InvalidOperationException("Explicit huge-allocation tests require a 64-bit Linux test process and backend with a readable cgroup v2 hierarchy.");
        }

        using FileStream executable = File.OpenRead($"/proc/{backend}/exe");
        Span<byte> header = stackalloc byte[5];
        executable.ReadExactly(header);
        ReadOnlySpan<byte> expectedHeader = [0x7f, 0x45, 0x4c, 0x46, 2];
        if (!header.SequenceEqual(expectedHeader))
        {
            throw new InvalidOperationException("Explicit huge-allocation tests require an ELF64 PostgreSQL backend.");
        }
    }

    private static void RequireUnmodifiedAllocatorEnvironment(int backend, Action<string> record)
    {
        string[] entries = Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{backend}/environ")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        string[] variables = ["MALLOC_PERTURB_", "MALLOC_CHECK_", "MALLOC_MMAP_THRESHOLD_", "MALLOC_TRIM_THRESHOLD_", "MALLOC_TOP_PAD_",
            "MALLOC_ARENA_MAX", "MALLOC_ARENA_TEST", "GLIBC_TUNABLES", "LD_PRELOAD", "LD_AUDIT"];
        foreach (string name in variables)
        {
            bool present = entries.Any(entry => entry.StartsWith(name + "=", StringComparison.Ordinal));
            record($"Backend allocator environment {name}: {(present ? "present" : "unset")}.");
            if (present)
            {
                throw new InvalidOperationException($"Huge-allocation admission rejects inherited {name}; the fixture does not alter allocator or loader settings.");
            }
        }
    }

    private static ResourceSnapshot Capture(int backend, string phase, Action<string> record, bool admission)
    {
        Dictionary<string, long> memory = ReadKilobytes("/proc/meminfo");
        Dictionary<string, long> process = ReadKilobytes($"/proc/{backend}/status");
        string overcommit = File.ReadAllText("/proc/sys/vm/overcommit_memory").Trim();
        record($"{phase} UTC={DateTime.UtcNow:O}; backend VmSize={process["VmSize"]}, VmRSS={process["VmRSS"]}, VmHWM={process["VmHWM"]} bytes.");
        record($"{phase} host MemAvailable={memory["MemAvailable"]}, SwapFree={memory["SwapFree"]}, CommitLimit={memory["CommitLimit"]}, Committed_AS={memory["Committed_AS"]} bytes; vm.overcommit_memory={overcommit}; vm.overcommit_ratio={File.ReadAllText("/proc/sys/vm/overcommit_ratio").Trim()}; vm.overcommit_kbytes={File.ReadAllText("/proc/sys/vm/overcommit_kbytes").Trim()}.");
        record($"{phase} host memory PSI:\n{File.ReadAllText("/proc/pressure/memory").Trim()}");
        List<CgroupObservation> groups = ReadCgroups(backend, phase, record, admission);
        if (admission && memory["MemAvailable"] <= RequiredHeadroom)
        {
            throw new InvalidOperationException($"Huge-allocation admission requires more than {RequiredHeadroom} bytes of host MemAvailable; observed {memory["MemAvailable"]}.");
        }

        if (admission && overcommit == "2" && memory["CommitLimit"] - memory["Committed_AS"] <= AddressSpaceAllowance)
        {
            throw new InvalidOperationException("Strict Linux overcommit accounting cannot admit the 3 GiB incremental address-space allowance.");
        }

        return new(process["VmSize"], groups);
    }

    private static List<CgroupObservation> ReadCgroups(int backend, string phase, Action<string> record, bool admission)
    {
        string membership = File.ReadLines($"/proc/{backend}/cgroup").SingleOrDefault(static line => line.StartsWith("0::", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Huge-allocation admission requires an identifiable actual cgroup v2 membership.");
        string[] mount = File.ReadLines($"/proc/{backend}/mountinfo").Select(static line => line.Split(' '))
            .SingleOrDefault(static fields =>
            {
                int separator = Array.IndexOf(fields, "-");
                return separator >= 0 && separator + 1 < fields.Length && fields[separator + 1] == "cgroup2";
            })
            ?? throw new InvalidOperationException("Huge-allocation admission requires one visible cgroup v2 mount.");
        string root = DecodeMountPath(mount[3]);
        string mountPoint = DecodeMountPath(mount[4]);
        if (root != "/")
        {
            throw new InvalidOperationException("A cgroup subtree mount hides ancestor limits; this environment cannot admit the huge-allocation witness.");
        }

        string actual = membership[3..];
        if (!actual.StartsWith('/'))
        {
            throw new InvalidOperationException("The backend's cgroup membership is not an absolute hierarchy path.");
        }

        string leaf = Path.GetFullPath(Path.Join(mountPoint, actual.TrimStart('/')));
        if (leaf != mountPoint && !leaf.StartsWith(mountPoint + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The backend's cgroup membership escaped the visible hierarchy.");
        }

        record($"{phase} actual cgroup membership={membership}; mount={mountPoint}; leaf={leaf}.");
        List<CgroupObservation> groups = [];
        for (string directory = leaf; ; directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("The cgroup ancestor path ended unexpectedly."))
        {
            bool hierarchyRoot = directory == mountPoint;
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"The actual cgroup ancestor is unreadable: {directory}");
            }

            string? currentText = ReadController(directory, "memory.current", hierarchyRoot);
            string? maximumText = ReadController(directory, "memory.max", hierarchyRoot);
            string? highText = ReadController(directory, "memory.high", hierarchyRoot);
            string? eventsText = ReadController(directory, "memory.events", hierarchyRoot);
            string? pressure = ReadController(directory, "memory.pressure", hierarchyRoot);
            record($"{phase} cgroup {directory}: memory.current={currentText ?? "not exposed"}, memory.max={maximumText ?? "not exposed"}, memory.high={highText ?? "not exposed"}; memory.peak={ReadController(directory, "memory.peak", optional: true) ?? "not exposed"}; memory.swap.current={ReadController(directory, "memory.swap.current", optional: true) ?? "not exposed"}; memory.swap.max={ReadController(directory, "memory.swap.max", optional: true) ?? "not exposed"}.");
            record($"{phase} cgroup {directory} memory.events:\n{eventsText ?? "not exposed"}\n{phase} memory PSI:\n{pressure ?? "not exposed"}");
            if (admission)
            {
                RequireControllerHeadroom(directory, "memory.max", maximumText, currentText);
                RequireControllerHeadroom(directory, "memory.high", highText, currentText);
            }

            Dictionary<string, long> events = [];
            foreach (string line in (eventsText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                events.Add(fields[0], long.Parse(fields[1], CultureInfo.InvariantCulture));
            }

            groups.Add(new(directory, events));
            if (hierarchyRoot)
            {
                break;
            }
        }

        return groups;
    }

    private static void RequireControllerHeadroom(string directory, string controller, string? limitText, string? currentText)
    {
        if (limitText is null or "max")
        {
            return;
        }

        long limit = long.Parse(limitText, CultureInfo.InvariantCulture);
        long current = long.Parse(currentText ?? throw new InvalidOperationException($"Missing usage beneath a finite {controller} at {directory}."), CultureInfo.InvariantCulture);
        if (limit - current <= RequiredHeadroom)
        {
            throw new InvalidOperationException($"Huge-allocation admission requires more than {RequiredHeadroom} bytes under {directory}/{controller}; limit={limit}, current={current}.");
        }
    }

    private static string? ReadController(string directory, string name, bool optional)
    {
        string path = Path.Join(directory, name);
        return optional && !File.Exists(path) ? null : File.ReadAllText(path).Trim();
    }

    private static Dictionary<string, long> ReadKilobytes(string path)
    {
        Dictionary<string, long> values = [];
        foreach (string line in File.ReadLines(path))
        {
            string[] fields = line.Split([' ', '\t', ':'], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 3 && fields[2] == "kB")
            {
                values.Add(fields[0], checked(long.Parse(fields[1], CultureInfo.InvariantCulture) * 1024));
            }
        }

        return values;
    }

    private static string DecodeMountPath(string value)
        => value.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal)
            .Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);

    private static async Task<(string Soft, string Hard)> ReadLimitsAsync(int backend, CancellationToken cancellationToken)
    {
        ProcessResult result = await ProcessRunner.RunCheckedAsync("prlimit",
            ["--pid", backend.ToString(CultureInfo.InvariantCulture), "--as", "--output", "SOFT,HARD", "--noheadings", "--raw"],
            new Dictionary<string, string?>(), cancellationToken);
        string[] limits = result.StandardOutput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (limits.Length != 2)
        {
            throw new InvalidOperationException("prlimit returned an unrecognized address-space limit record.");
        }

        return (limits[0], limits[1]);
    }

    private static Task<ProcessResult> SetSoftLimitAsync(int backend, string soft, CancellationToken cancellationToken)
        => ProcessRunner.RunCheckedAsync("prlimit", ["--pid", backend.ToString(CultureInfo.InvariantCulture), "--as=" + soft + ":"],
            new Dictionary<string, string?>(), cancellationToken);

    private sealed record ResourceSnapshot(long VirtualBytes, List<CgroupObservation> Cgroups);

    private sealed record CgroupObservation(string Directory, Dictionary<string, long> Events);
}
