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
}
