using System.Net;
using DotNet.Testcontainers.Configurations;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Restate.Sdk.Hosting;

namespace Restate.Sdk.Testing.Containers.Tests;

public sealed class RestateContainerTests
{
    [DockerFact]
    public async Task Container_starts_and_admin_health_returns_ok()
    {
        await using var container = new RestateBuilder().Build();
        await container.StartAsync();

        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri(container.GetAdminUri(), "health"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Both ports are bound to random host ports and exposed as URIs.
        Assert.NotEqual(0, container.GetIngressUri().Port);
        Assert.NotEqual(0, container.GetAdminUri().Port);
        Assert.NotEqual(container.GetIngressUri().Port, container.GetAdminUri().Port);
    }

    [DockerFact]
    public async Task Register_deployment_works_when_caller_exposed_the_port_directly()
    {
        // The documented standalone (non-harness) flow from RegisterDeploymentAsync's remarks:
        // expose the host port yourself, then build the container, then register. The internal
        // re-expose inside RegisterDeploymentAsync must tolerate the already-forwarded port
        // (regression: it previously failed with an SshException remote-bind conflict).
        var app = RestateHost.CreateBuilder()
            .WithPort(0)
            .AddService<HarnessGreeterService>()
            .Build();
        await using var _ = app;
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var hostPort = BindingAddress.Parse(address).Port;

        await TestcontainersSettings.ExposeHostPortsAsync((ushort)hostPort);

        await using var container = new RestateBuilder().Build();
        await container.StartAsync();
        await container.RegisterDeploymentAsync(hostPort);

        using var client = container.CreateIngressClient();
        var reply = await client
            .Service("HarnessGreeterService")
            .Call<GreetReply>("Greet", new GreetRequest("Layer1"));

        Assert.Equal("Hello, Layer1!", reply.Message);

        await app.StopAsync();
    }

    [DockerFact]
    public async Task Ingress_client_immediate_and_delayed_sends_return_attachable_invocations()
    {
        var app = RestateHost.CreateBuilder()
            .WithPort(0)
            .AddService<HarnessGreeterService>()
            .Build();
        await using var _ = app;
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var hostPort = BindingAddress.Parse(address).Port;

        await TestcontainersSettings.ExposeHostPortsAsync((ushort)hostPort);

        await using var container = new RestateBuilder().Build();
        await container.StartAsync();
        await container.RegisterDeploymentAsync(hostPort);

        using var client = container.CreateIngressClient();
        var immediateInvocationId = await client
            .Service("HarnessGreeterService")
            .Send("Greet", new GreetRequest("Immediate"));
        var delayedInvocationId = await client
            .Service("HarnessGreeterService")
            .Send("Greet", new GreetRequest("Delayed"), delay: TimeSpan.FromMilliseconds(100));

        Assert.False(string.IsNullOrWhiteSpace(immediateInvocationId));
        Assert.False(string.IsNullOrWhiteSpace(delayedInvocationId));

        var immediateReply = await client.Attach<GreetReply>(immediateInvocationId);
        var delayedReply = await client.Attach<GreetReply>(delayedInvocationId);

        Assert.Equal("Hello, Immediate!", immediateReply.Message);
        Assert.Equal("Hello, Delayed!", delayedReply.Message);

        await app.StopAsync();
    }
}
