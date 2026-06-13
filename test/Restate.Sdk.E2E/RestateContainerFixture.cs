// =============================================================================================
//  §2.7 image-tag verification outcomes (recorded per plan 07 §2.7)
//  ---------------------------------------------------------------------------------------------
//  Verified locally on 2026-06-13 against ImageTag = docker.io/restatedev/restate:1.4:
//
//   (a) Tag exists and runs:  `restate-server 1.4.4`.
//       It speaks the fork's service protocol: ReplayLab's /discover manifest advertises
//       minProtocolVersion=5 / maxProtocolVersion=6, and the server negotiates the per-invocation
//       protocol via the request Content-Type `application/vnd.restate.invocation.v5`. NOTE: the
//       SDK previously hardcoded the RESPONSE content type to `...invocation.v6`, which 1.4.4
//       rejected with RT0012 ("unexpected content type"); the endpoint now echoes the request's
//       negotiated version (RestateEndpointRouteBuilderExtensions.NegotiateInvocationContentType),
//       which is what unblocks every scenario below.
//
//   (b) Env knobs accepted (the suspension forcers): the container starts cleanly with
//       RESTATE_WORKER__INVOKER__INACTIVITY_TIMEOUT=5s and RESTATE_WORKER__INVOKER__ABORT_TIMEOUT=30s
//       — restate-server logs a hard error on an unknown config key, and there is none. With the
//       5s inactivity timeout an 8s handler sleep forces a genuine suspend → resume (verified: E1
//       returns in ~8s with probe attempt=2, run:a=1, run:b=1).
//
//   (c) Eager-state disable/limit knob for the E4 container variant: NOT pursued here. A healthy
//       1.4.4 resume carries COMPLETE eager state, so no container scenario can reach B7's lazy
//       fallthrough; that path is owned deterministically by the in-process P4 harness, and the
//       real-server lazy path is the documented residual gap (plan 07 §3.1(1)). E4 therefore
//       asserts only state continuity + a genuine suspend/resume (attempt>=2), per §2.4 E4's
//       "honest scope" note. No silent knob-variant is added.
//
//  If the tag ever needs bumping, change the single `ImageTag` constant below AND the two other
//  greppable sites: .github/workflows/e2e.yml (pre-pull) and .github/scripts/integration-test.sh.
// =============================================================================================

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ReplayLab;
using Xunit;

namespace Restate.Sdk.E2E;

/// <summary>
///     Stands up the full E2E rig for the ReplayLab replay scenarios:
///     <list type="number">
///         <item>
///             the ReplayLab services hosted IN-PROCESS (so tests read <see cref="ExecutionProbe" />
///             directly) on an OS-assigned ephemeral port;
///         </item>
///         <item>a real <c>restate-server</c> container with a 5s inactivity timeout (the suspension forcer);</item>
///         <item>
///             the container's worker reaching the in-process endpoint through
///             <c>host.docker.internal</c> (mapped to the host gateway), with the deployment registered
///             via the admin API.
///         </item>
///     </list>
///     A real server — not the test — decides batch composition, notification ordering, EOF timing
///     and retry cadence, which is the only way to genuinely exercise B1/B2/B3/B8 end to end.
/// </summary>
[SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposal is performed through IAsyncLifetime.DisposeAsync, which xunit invokes "
                    + "after the collection completes; a synchronous IDisposable would race the "
                    + "async container/endpoint teardown.")]
public sealed class RestateContainerFixture : IAsyncLifetime
{
    /// <summary>
    ///     The pinned restate-server image. SINGLE source of truth — see the §2.7 header block and
    ///     the two other greppable sites (e2e.yml pre-pull, integration-test.sh).
    /// </summary>
    public const string ImageTag = "docker.io/restatedev/restate:1.4";

    // Container-internal ports (restate-server defaults): ingress and admin.
    private const int IngressPort = 8080;
    private const int AdminPort = 9070;

    // The inactivity timeout is the suspension forcer: any handler parked >= this with no traffic
    // gets its input closed by the server, so the post-fix SDK emits a SuspensionMessage. Every
    // ReplayLab sleep is 8s, comfortably above this 5s, so suspension is deterministic.
    private const string InactivityTimeout = "5s";
    private const string AbortTimeout = "30s";

    private WebApplication? _endpoint;
    private IContainer? _container;
    private HttpClient? _adminClient;

    /// <summary>The ingress base URL (<c>http://localhost:{mappedIngressPort}</c>) for driving handlers.</summary>
    public string IngressBase { get; private set; } = string.Empty;

    /// <summary>An <see cref="IngressClient" /> bound to <see cref="IngressBase" />, shared by all scenarios.</summary>
    public IngressClient Ingress { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // 1) Start the in-process ReplayLab endpoint on an ephemeral port and read the bound port
        //    from the server addresses feature (ListenAnyIP(0) → an OS-assigned port).
        _endpoint = ReplayLabHost.Build(0);
        await _endpoint.StartAsync();
        var endpointPort = ResolveBoundPort(_endpoint);

        // 2) Start the container with the suspension-forcing knobs and a host-gateway alias so the
        //    container's worker can dial back to the in-process endpoint on the host.
        _container = new ContainerBuilder()
            .WithImage(ImageTag)
            .WithPortBinding(IngressPort, true)
            .WithPortBinding(AdminPort, true)
            .WithEnvironment("RESTATE_WORKER__INVOKER__INACTIVITY_TIMEOUT", InactivityTimeout)
            .WithEnvironment("RESTATE_WORKER__INVOKER__ABORT_TIMEOUT", AbortTimeout)
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPort(AdminPort).ForPath("/health")))
            .Build();
        await _container.StartAsync();

        var adminBase = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(AdminPort)}";
        IngressBase = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(IngressPort)}";
        Ingress = new IngressClient(IngressBase);

        // 3) Register the deployment: the container reaches the host endpoint via host.docker.internal.
        _adminClient = new HttpClient { BaseAddress = new Uri(adminBase) };
        await RegisterDeploymentAsync(endpointPort);
    }

    public async Task DisposeAsync()
    {
        Ingress?.Dispose();
        _adminClient?.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
        if (_endpoint is not null)
        {
            await _endpoint.StopAsync();
            await _endpoint.DisposeAsync();
        }
    }

    /// <summary>
    ///     POSTs the deployment registration to the admin API and fails fast (with the response
    ///     body) on a non-success status so a registration problem surfaces immediately rather than
    ///     as a downstream invocation timeout.
    /// </summary>
    private async Task RegisterDeploymentAsync(int endpointPort)
    {
        var uri = $"http://host.docker.internal:{endpointPort}";
        using var response = await _adminClient!.PostAsJsonAsync("/deployments", new { uri });
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Deployment registration of '{uri}' failed: {(int)response.StatusCode} {response.StatusCode}\n{body}");
        }
    }

    /// <summary>
    ///     Reads the OS-assigned port the in-process endpoint bound to. With <c>ListenAnyIP(0)</c>
    ///     the address feature reports e.g. <c>http://[::]:54321</c>; we parse the port off the end.
    /// </summary>
    private static int ResolveBoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("ReplayLab endpoint exposed no server addresses.");
        foreach (var address in addresses.Addresses)
        {
            // The last ':' separates the port; Uri can't parse the wildcard host forms directly.
            var lastColon = address.LastIndexOf(':');
            if (lastColon >= 0 && int.TryParse(address[(lastColon + 1)..], out var port) && port > 0)
                return port;
        }

        throw new InvalidOperationException(
            $"Could not parse a bound port from ReplayLab addresses: [{string.Join(", ", addresses.Addresses)}]");
    }
}

/// <summary>
///     The xunit collection that serializes every E2E scenario onto the single shared
///     <see cref="RestateContainerFixture" /> (one container, one in-proc endpoint).
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xunit collection-definition types conventionally end in 'Collection'.")]
[CollectionDefinition(Name)]
public sealed class RestateContainerCollection : ICollectionFixture<RestateContainerFixture>
{
    public const string Name = "restate-container";
}
