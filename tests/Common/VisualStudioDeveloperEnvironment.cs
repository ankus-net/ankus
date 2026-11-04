using System.Diagnostics;

namespace Ankus.Tests;

/// <summary>
/// Gives a Windows test process the x64 Visual Studio developer environment that CI configures before its suites.
/// </summary>
internal static class VisualStudioDeveloperEnvironment
{
    /// <summary>
    /// Imports the environment of the newest Visual Studio 2022 17.9 or later with the C++ x64 tools, as the CI script's
    /// <c>windows-toolchain</c> command does, unless the process already has an x64 developer environment. Tests that
    /// compile native code then run the same from any shell and from Visual Studio's Test Explorer.
    /// </summary>
    /// <remarks>
    /// Without such an installation the environment stays as it is, and only the tests that run its tools fail.
    /// </remarks>
    /// <param name="context">Receives which environment the tests use.</param>
    internal static void Configure(TestContext context)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The developer environment sets Platform, which MSBuild reads as a property, so the solutions and projects the
        // tests build would select an x64 configuration. CI removes it from every dotnet it starts.
        Environment.SetEnvironmentVariable("Platform", null);
        if (Environment.GetEnvironmentVariable("VSCMD_ARG_TGT_ARCH") == "x64")
        {
            context.WriteLine($"Using the caller's x64 developer environment, MSVC {Environment.GetEnvironmentVariable("VCToolsVersion")}.");
            return;
        }

        string locator = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(locator))
        {
            context.WriteLine("Visual Studio is not installed, so native compilation uses the caller's environment.");
            return;
        }

        string installation = Capture(locator,
            "-latest -products * -version [17.9,) -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath")
            .Trim();
        string setup = Path.Join(installation, "VC", "Auxiliary", "Build", "vcvarsall.bat");
        if (installation.Length == 0 || !File.Exists(setup))
        {
            context.WriteLine("No Visual Studio 2022 17.9 or later has the C++ x64 tools, so native compilation uses the caller's environment.");
            return;
        }

        // cmd.exe needs its own quoting rules for a batch path containing spaces.
        string configured = Capture(Path.Join(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{setup}\" x64 >nul && set\"");
        foreach (string line in configured.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && !line[..separator].Equals("Platform", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(line[..separator], line[(separator + 1)..]);
            }
        }

        // CI's child Native AOT builds reuse this environment instead of running vcvarsall again for each project.
        Environment.SetEnvironmentVariable("IlcUseEnvironmentalTools", "true");
        context.WriteLine($"Using MSVC {Environment.GetEnvironmentVariable("VCToolsVersion")} for x64 from {installation}.");
    }

    /// <summary>
    /// Runs a program and returns its standard output.
    /// </summary>
    /// <param name="program">The program to run.</param>
    /// <param name="arguments">The command line arguments, quoted for the program.</param>
    /// <returns>The program's standard output.</returns>
    private static string Capture(string program, string arguments)
    {
        var start = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {program}.");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{program} {arguments} exited with {process.ExitCode}.");
        }

        return output;
    }
}
