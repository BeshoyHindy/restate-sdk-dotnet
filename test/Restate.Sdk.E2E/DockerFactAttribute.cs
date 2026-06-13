using Xunit;

namespace Restate.Sdk.E2E;

/// <summary>
///     A <see cref="FactAttribute" /> that skips cleanly when no Docker daemon is reachable, so a
///     developer without Docker can still run <c>dotnet test</c> on the rest of the repo without
///     red. In CI Docker is always present, and <c>e2e.yml</c> asserts the suite did NOT skip (it
///     greps the trx for <c>NotExecuted</c> outcomes) — a broken Docker detection there becomes a
///     build failure rather than a silent green, so this skip never hides a real regression.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    private const string UnixDockerSocket = "/var/run/docker.sock";

    public DockerFactAttribute()
    {
        if (!DockerAvailable)
            Skip = "Docker is not available";
    }

    /// <summary>
    ///     True when a Docker daemon is reachable: either the default Unix socket exists or an
    ///     explicit endpoint is configured via <c>DOCKER_HOST</c> (Podman, remote daemon, rootless).
    /// </summary>
    internal static bool DockerAvailable =>
        File.Exists(UnixDockerSocket) ||
        Environment.GetEnvironmentVariable("DOCKER_HOST") is { Length: > 0 };
}
