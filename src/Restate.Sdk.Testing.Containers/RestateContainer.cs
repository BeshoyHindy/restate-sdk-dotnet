using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using DotNet.Testcontainers.Containers;
using Restate.Sdk.Client;

namespace Restate.Sdk.Testing.Containers;

/// <summary>
///     A Docker container running the Restate server, started via <see cref="RestateBuilder" />.
///     Exposes the mapped ingress and admin endpoints and can register SDK endpoints running
///     on the test host with the server's admin API.
/// </summary>
public sealed class RestateContainer : DockerContainer
{
    private static readonly TimeSpan RegistrationPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Initializes a new instance of the <see cref="RestateContainer" /> class.
    /// </summary>
    /// <param name="configuration">The container configuration.</param>
    public RestateContainer(RestateConfiguration configuration)
        : base(configuration)
    {
    }

    /// <summary>
    ///     Gets the Restate ingress URI on the test host
    ///     (the mapped port for container port <see cref="RestateBuilder.IngressPort" />).
    /// </summary>
    /// <returns>The ingress base URI, e.g. <c>http://localhost:55123</c>.</returns>
    public Uri GetIngressUri()
    {
        return new UriBuilder(Uri.UriSchemeHttp, Hostname, GetMappedPublicPort(RestateBuilder.IngressPort)).Uri;
    }

    /// <summary>
    ///     Gets the Restate admin API URI on the test host
    ///     (the mapped port for container port <see cref="RestateBuilder.AdminPort" />).
    /// </summary>
    /// <returns>The admin API base URI, e.g. <c>http://localhost:55124</c>.</returns>
    public Uri GetAdminUri()
    {
        return new UriBuilder(Uri.UriSchemeHttp, Hostname, GetMappedPublicPort(RestateBuilder.AdminPort)).Uri;
    }

    /// <summary>
    ///     Creates a <see cref="RestateClient" /> pointing at this container's ingress endpoint.
    ///     The caller owns the returned client and should dispose it.
    /// </summary>
    /// <returns>A new ingress client.</returns>
    [RequiresUnreferencedCode("RestateClient uses reflection-based JSON serialization.")]
    [RequiresDynamicCode("RestateClient uses reflection-based JSON serialization.")]
    public RestateClient CreateIngressClient()
    {
        return new RestateClient(GetIngressUri());
    }

    /// <summary>
    ///     Registers an SDK endpoint listening on the given test-host port as a deployment
    ///     with this Restate server, and waits until the deployment is listed by the admin API.
    /// </summary>
    /// <remarks>
    ///     The endpoint is reached from inside the container as
    ///     <c>http://host.testcontainers.internal:{hostPort}</c> via Testcontainers host-port
    ///     forwarding. The forwarding container must already be running when this container is
    ///     <em>built</em> for that hostname to resolve — call
    ///     <c>TestcontainersSettings.ExposeHostPortsAsync</c> before constructing the
    ///     <see cref="RestateBuilder" />, or use <see cref="RestateTestHarness" /> which handles
    ///     the ordering for you. Registration is retried until the endpoint responds; the
    ///     deployment list is then polled (no blind sleeps) until the deployment appears.
    /// </remarks>
    /// <param name="hostPort">The test-host port the SDK endpoint is listening on.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="TimeoutException">The deployment was not registered within two minutes.</exception>
    public async Task RegisterDeploymentAsync(int hostPort, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hostPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hostPort, ushort.MaxValue);

        await HostPortForwarding.EnsureExposedAsync((ushort)hostPort, ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RegistrationTimeout);

        try
        {
            await RegisterDeploymentCoreAsync(hostPort, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The SDK endpoint on host port {hostPort} was not registered with Restate within {RegistrationTimeout}. " +
                "Verify the endpoint is running and reachable from the container.");
        }
    }

    private async Task RegisterDeploymentCoreAsync(int hostPort, CancellationToken ct)
    {
        // Disable the 100-second per-request default timeout: the overall registration cap is
        // enforced by the linked token, and a per-request abort would otherwise surface as an
        // OperationCanceledException that escapes the retry loop and gets misreported as the
        // full registration timeout.
        using var http = new HttpClient
        {
            BaseAddress = GetAdminUri(),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        var deploymentsUri = new Uri("/deployments", UriKind.Relative);
        var payload = new JsonObject { ["uri"] = $"http://host.testcontainers.internal:{hostPort}" }.ToJsonString();

        // Retry registration until the admin API accepts it (the SDK endpoint must be
        // reachable from inside the container for discovery to succeed).
        string? deploymentId = null;
        while (deploymentId is null)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(deploymentsUri, content, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    deploymentId = JsonNode.Parse(body)?["id"]?.GetValue<string>();
                }
            }
            catch (HttpRequestException)
            {
                // Admin API not reachable yet; retry below.
            }

            if (deploymentId is null)
                await Task.Delay(RegistrationPollInterval, ct).ConfigureAwait(false);
        }

        // Poll the deployment list until the new deployment shows up.
        while (true)
        {
            try
            {
                using var response = await http.GetAsync(deploymentsUri, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var deployments = JsonNode.Parse(body)?["deployments"]?.AsArray();
                    if (deployments is not null &&
                        deployments.Any(d => deploymentId.Equals(d?["id"]?.GetValue<string>(), StringComparison.Ordinal)))
                        return;
                }
            }
            catch (HttpRequestException)
            {
                // Transient admin API failure; retry below.
            }

            await Task.Delay(RegistrationPollInterval, ct).ConfigureAwait(false);
        }
    }
}
