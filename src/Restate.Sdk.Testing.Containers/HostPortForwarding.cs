using DotNet.Testcontainers.Configurations;

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

            await TestcontainersSettings.ExposeHostPortsAsync(port, ct).ConfigureAwait(false);
            ExposedPorts.Add(port);
        }
        finally
        {
            Gate.Release();
        }
    }
}
