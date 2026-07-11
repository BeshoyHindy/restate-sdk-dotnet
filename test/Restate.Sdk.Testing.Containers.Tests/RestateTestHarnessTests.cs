namespace Restate.Sdk.Testing.Containers.Tests;

/// <summary>
///     Starts one harness (SDK endpoint + Restate container + registered deployment)
///     shared by all tests in <see cref="RestateTestHarnessTests" />, following the
///     IAsyncLifetime fixture pattern documented on <see cref="RestateTestHarness" />.
/// </summary>
public sealed class RestateHarnessFixture : IAsyncLifetime
{
    public RestateTestHarness Harness { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Harness = await RestateTestHarness.StartAsync(b => b
            .AddService<HarnessGreeterService>()
            .AddVirtualObject<HarnessCounterObject>());
    }

    public async Task DisposeAsync()
    {
        if (Harness is not null)
            await Harness.DisposeAsync();
    }
}

public sealed class RestateTestHarnessTests : IClassFixture<RestateHarnessFixture>
{
    private readonly RestateHarnessFixture _fixture;

    public RestateTestHarnessTests(RestateHarnessFixture fixture)
    {
        _fixture = fixture;
    }

    [DockerFact]
    public async Task Harness_registers_service_and_ingress_call_round_trips()
    {
        var reply = await _fixture.Harness.Client
            .Service("HarnessGreeterService")
            .Call<GreetReply>("Greet", new GreetRequest("Restate"));

        Assert.Equal("Hello, Restate!", reply.Message);
    }

    [DockerFact]
    public async Task Virtual_object_state_persists_across_calls()
    {
        var counter = _fixture.Harness.Client.VirtualObject("HarnessCounterObject", "counter-key-1");

        var first = await counter.Call<int>("Add", 2);
        var second = await counter.Call<int>("Add", 3);

        Assert.Equal(2, first);
        Assert.Equal(5, second);

        // A shared read confirms the state was durably persisted, not just returned.
        var current = await counter.Call<int>("Get");
        Assert.Equal(5, current);
    }
}
