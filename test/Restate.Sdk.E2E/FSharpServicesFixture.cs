using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FSharpProgram = Restate.Sdk.FSharp.Samples.Program;

namespace Restate.Sdk.E2E;

/// <summary>
///     Hosts the F# sample (<c>samples/FSharpServices</c>) in-process behind a real <c>restate-server</c>
///     container, exactly as <see cref="RestateContainerFixture" /> does for the C# ReplayLab sample. The
///     in-process endpoint is the same F# code (attributed handlers, the C# Restate.Sdk.FSharp runtime
///     helpers, and the Myriad-generated registration) the executable runs; the container's worker dials
///     back through <c>host.docker.internal</c>, so every invocation travels the full real path
///     (ingress → restate-server → F# endpoint → back).
/// </summary>
[SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Disposal runs through IAsyncLifetime.DisposeAsync.")]
public sealed class FSharpServicesFixture : IAsyncLifetime
{
    private const int IngressPort = 8080;
    private const int AdminPort = 9070;

    // The inactivity timeout is the suspension forcer: a workflow parked on an unresolved awakeable
    // gets its input closed after this, so the SDK suspends until the awakeable is resolved.
    private const string InactivityTimeout = "5s";
    private const string AbortTimeout = "30s";

    private WebApplication? _endpoint;
    private IContainer? _container;
    private HttpClient? _adminClient;

    public IngressClient Ingress { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // 1) Start the in-process F# endpoint on an OS-assigned ephemeral port.
        _endpoint = FSharpProgram.buildHost(0);
        await _endpoint.StartAsync();
        var endpointPort = ResolveBoundPort(_endpoint);

        // 2) Start the restate-server container with the suspension-forcing knobs and a host-gateway
        //    alias so the worker can dial back to the in-process endpoint on the host.
        _container = new ContainerBuilder()
            .WithImage(RestateContainerFixture.ImageTag)
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
        var ingressBase = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(IngressPort)}";
        Ingress = new IngressClient(ingressBase);

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

    private static int ResolveBoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("F# endpoint exposed no server addresses.");
        foreach (var address in addresses.Addresses)
        {
            var lastColon = address.LastIndexOf(':');
            if (lastColon >= 0 && int.TryParse(address[(lastColon + 1)..], out var port) && port > 0)
                return port;
        }

        throw new InvalidOperationException(
            $"Could not parse a bound port from F# endpoint addresses: [{string.Join(", ", addresses.Addresses)}]");
    }
}

/// <summary>The xunit collection that serializes the F# scenarios onto one shared fixture.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xunit collection-definition types conventionally end in 'Collection'.")]
[CollectionDefinition(Name)]
public sealed class FSharpServicesCollection : ICollectionFixture<FSharpServicesFixture>
{
    public const string Name = "fsharp-services";
}
