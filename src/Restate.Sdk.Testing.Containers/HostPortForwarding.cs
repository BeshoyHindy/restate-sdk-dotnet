using DotNet.Testcontainers.Configurations;
using Renci.SshNet.Common;

namespace Restate.Sdk.Testing.Containers;

/// <summary>
///     Guards <see cref="TestcontainersSettings.ExposeHostPortsAsync(ushort, CancellationToken)" />
///     so each host port is only forwarded once per process. Forwarding the same port twice
///     would open a second SSH tunnel for an already-bound remote port and fail.
/// </summary>
internal static class HostPortForwarding
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<ushort> ExposedPorts = [];

    /// <summary>
    ///     Forwards the given host port into the Testcontainers network (reachable from
    ///     containers as <c>host.testcontainers.internal</c>), unless it was already forwarded.
    /// </summary>
    /// <remarks>
    ///     The first call also starts the shared port-forwarding container. Containers only
    ///     receive the <c>host.testcontainers.internal</c> extra-host entry when the
    ///     port-forwarding container is running at the time their builder is constructed,
    ///     so this must be called before <see cref="RestateBuilder" /> is instantiated.
    ///     Ports the caller already forwarded directly via
    ///     <see cref="TestcontainersSettings.ExposeHostPortsAsync(ushort, CancellationToken)" />
    ///     are tolerated: the resulting remote-bind conflict is detected and treated as success.
    /// </remarks>
    /// <param name="port">The host port to forward.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task EnsureExposedAsync(ushort port, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ExposedPorts.Contains(port))
                return;

            try
            {
                await TestcontainersSettings.ExposeHostPortsAsync(port, ct).ConfigureAwait(false);
            }
            catch (SshException exception) when (exception.GetType() == typeof(SshException))
            {
                // The sshd port-forwarding container is per-process, so a remote-forward bind
                // conflict ("Port forwarding for '127.0.0.1' port 'X' failed to start.") can only
                // mean this process already forwarded the port — e.g. the caller invoked
                // TestcontainersSettings.ExposeHostPortsAsync directly, as documented on
                // RestateContainer.RegisterDeploymentAsync. Treat it as already exposed.
                // Connection and authentication failures are SshException subclasses and
                // deliberately excluded by the exact-type filter.
            }

            ExposedPorts.Add(port);
        }
        finally
        {
            Gate.Release();
        }
    }
}
