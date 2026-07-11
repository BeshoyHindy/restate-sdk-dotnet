using System.Net;

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
}
