# Integration Testing with Testcontainers

`Restate.Sdk.Testing.Containers` runs a real Restate server in Docker for the duration of a test
run, registers your Endpoint against it, and hands back an Ingress client. Where the mock contexts
in [Testing](testing.md) exercise a handler's logic in isolation, this exercises the whole loop:
the protocol, the journal, state persistence, and the Restate runtime itself.

```bash
dotnet add package Restate.Sdk.Testing.Containers
```

Docker has to be available on the machine running the tests.

## One call to start everything

`RestateTestHarness.StartAsync` starts your services as an Endpoint on an ephemeral port, starts
the Restate container, forwards the port into the container network, registers the deployment, and
waits for it to be live.

```csharp
using Restate.Sdk.Testing.Containers;

await using var harness = await RestateTestHarness.StartAsync(b => b.AddService<GreeterService>());

var reply = await harness.Client.Service("GreeterService").Call<string>("Greet", "World");
```

The harness exposes exactly two things: `Client`, an Ingress `RestateClient` pointing at the
container, and `Container`, the running `RestateContainer`. Disposing it stops the Endpoint, then
the container, and disposes the client.

## One container per test class

Starting a container per test is slow. The package deliberately does not reference a test
framework, so wire it into whatever fixture mechanism your framework offers. With xUnit, an
`IAsyncLifetime` class fixture serves every test in the class from one container:

```csharp
using Restate.Sdk.Testing.Containers;

public sealed class RestateFixture : IAsyncLifetime
{
    public RestateTestHarness Harness { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Harness = await RestateTestHarness.StartAsync(b => b.AddService<GreeterService>());
    }

    public async Task DisposeAsync()
    {
        await Harness.DisposeAsync();
    }
}

public sealed class GreeterIntegrationTests : IClassFixture<RestateFixture>
{
    private readonly RestateFixture _fixture;

    public GreeterIntegrationTests(RestateFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Greet_round_trips_through_the_ingress()
    {
        var reply = await _fixture.Harness.Client
            .Service("GreeterService")
            .Call<string>("Greet", "World");

        Assert.Equal("Hello, World!", reply);
    }
}
```

Register every Service, Virtual Object, and Workflow the class needs in the one `StartAsync`
call — the builder passed to it is the same `RestateHostBuilder` used for production hosting, so
`AddService`, `AddVirtualObject`, and `AddWorkflow` all apply.

On a machine without Docker these tests will fail rather than skip. If your suite has to run in
both places, gate them behind a custom `FactAttribute` that sets `Skip` when no Docker daemon
answers, and gate the fixture's `InitializeAsync` on the same check — xUnit initializes class
fixtures even when every test in the class is skipped.

## Options

```csharp
await using var harness = await RestateTestHarness.StartAsync(
    b => b.AddService<GreeterService>(),
    new RestateTestHarnessOptions
    {
        Image = "docker.io/restatedev/restate:1.7",
        StartupTimeout = TimeSpan.FromMinutes(5),
    });
```

`Image` defaults to `RestateBuilder.RestateImage` — currently `docker.io/restatedev/restate:1.7`,
pinned to a minor version rather than `latest` so a server release cannot turn a green suite red
overnight. Set it explicitly, as above, when your suite wants to pin a version of its own.

`StartupTimeout` defaults to two minutes and covers the whole startup — Endpoint, container, and
deployment registration. Overrunning it throws `TimeoutException`, and everything already started
is torn down.

## Driving the container directly

`RestateBuilder` is a standard Testcontainers module, so it composes with the rest of a
Testcontainers setup — networks, resource reaping, custom wait strategies. Both the ingress port
(8080) and the admin port (9070) are bound to random free host ports, and the container is ready
once the admin `/health` endpoint returns `200 OK`.

```csharp
await using var container = new RestateBuilder().Build();
await container.StartAsync();

var ingress = container.GetIngressUri();
var admin = container.GetAdminUri();

using var client = container.CreateIngressClient();
```

Reach for this instead of the harness when the test needs a Restate server but not an Endpoint of
its own — asserting on the admin API, or pointing several Endpoints at one server.

To register an Endpoint you host yourself, forward its port *before* the container is built.
Containers only learn the `host.testcontainers.internal` address when the port-forwarding
container is already running at the time their builder is initialized:

```csharp
using DotNet.Testcontainers.Configurations;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Restate.Sdk.Hosting;
using Restate.Sdk.Testing.Containers;

var app = RestateHost.CreateBuilder()
    .WithPort(0)
    .AddService<GreeterService>()
    .Build();
await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()!.Addresses.First();
var hostPort = BindingAddress.Parse(address).Port;

// Forward the port before the container is built: the container only learns
// host.testcontainers.internal at builder-initialization time.
await TestcontainersSettings.ExposeHostPortsAsync((ushort)hostPort);

await using var container = new RestateBuilder().Build();
await container.StartAsync();
await container.RegisterDeploymentAsync(hostPort);

using var client = container.CreateIngressClient();
var reply = await client.Service("GreeterService").Call<string>("Greet", "World");
```

`RegisterDeploymentAsync` retries until the admin API accepts the deployment — the Endpoint has to
be reachable from inside the container for discovery to succeed — and then polls the deployment
list until it appears. No blind sleeps, and it gives up with a `TimeoutException` after two
minutes. Forwarding a port that is already forwarded is tolerated, so the call is safe to make
whichever way the port was exposed.

## Choosing between mock contexts and the harness

| | `Restate.Sdk.Testing` | `Restate.Sdk.Testing.Containers` |
|---|---|---|
| What runs | Your handler, in-process | Your Endpoint plus a real Restate server |
| Startup | None | Pulls and starts a container |
| Speed | Milliseconds | Seconds |
| Needs Docker | No | Yes |
| Journal, replay, retries | Simulated by the mock | Real |
| State across calls | Whatever the mock is seeded with | Durably persisted by the server |
| Calls to other services | Stubbed with `SetupCall` | Actually invoked |

A unit test with `MockContext` answers "does this handler compute the right thing?":

```csharp
using Restate.Sdk.Testing;

var ctx = new MockContext();
var service = new GreeterService();

var reply = await service.Greet(ctx, "World");

Assert.Equal("Hello, World!", reply);
```

An integration test answers "does this survive contact with Restate?" — that the handler is
discovered under the name you expect, that its input and output serialize the way the Ingress
sends them, that Virtual Object state actually persists across invocations, and that a Workflow
resumes where it left off.

Most suites want both: mock contexts for the branches and edge cases of each handler, and a
smaller set of container-backed tests covering the wiring. Reach for the container first whenever
a bug would live in the gap between your handler and the runtime — naming, serialization, state,
or ordering.

## Next Steps

- [Testing](testing.md) — mock contexts for unit tests
- [Service Types](service-types.md) — what state each service type persists
