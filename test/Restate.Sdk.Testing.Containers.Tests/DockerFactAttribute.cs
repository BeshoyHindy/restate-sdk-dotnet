using System.ComponentModel;
using System.Diagnostics;

namespace Restate.Sdk.Testing.Containers.Tests;

/// <summary>
///     An xUnit fact that is skipped when Docker is not available on the machine,
///     so container-backed tests never fail on hosts without a Docker daemon.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerDetection.IsDockerAvailable)
            Skip = "Docker is not available on this machine.";
    }
}

/// <summary>Detects (once per test run) whether a Docker daemon is reachable.</summary>
internal static class DockerDetection
{
    public static bool IsDockerAvailable { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            var startInfo = new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
