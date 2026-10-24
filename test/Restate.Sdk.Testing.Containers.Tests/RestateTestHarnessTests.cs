using Restate.Sdk.Client;
using Xunit.Abstractions;

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
        // xUnit v2 initializes class fixtures even when every test in the class is skipped,
        // so gate here as well: without this, Docker-less machines would still boot the SDK
        // endpoint and probe for a Docker daemon before the [DockerFact] skips kick in.
        if (!DockerDetection.IsDockerAvailable)
            return;

        Harness = await RestateTestHarness.StartAsync(b => b
            .AddService<HarnessGreeterService>()
            .AddService<HarnessBlockingService>()
            .AddService<HarnessScopedService>()
            .AddService<HarnessSignalService>()
            .AddService<HarnessSignalResolverService>()
            .AddVirtualObject<HarnessCounterObject>());
    }

    public async Task DisposeAsync()
    {
        if (Harness is not null)
            await Harness.DisposeAsync();
    }
}

/// <summary>
///     Shares one harness — one container, one registered deployment — across every test class
///     that needs it, so adding a scenario class does not add a container to the CI run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SharedRestateHarness : ICollectionFixture<RestateHarnessFixture>
{
    public const string Name = "restate-harness";
}

[Collection(SharedRestateHarness.Name)]
public sealed class RestateTestHarnessTests
{
    private readonly RestateHarnessFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RestateTestHarnessTests(RestateHarnessFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
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

    [DockerFact]
    public async Task Send_round_trips_on_the_canonical_route_for_a_service_and_a_virtual_object()
    {
        // An idempotency key gives the invocation completion retention, which is what makes its
        // result readable afterwards: the ingress serves no output for a plain send.
        var sendId = await _fixture.Harness.Client
            .Service("HarnessGreeterService")
            .Send("Greet", new GreetRequest("Sent"), idempotencyKey: "canonical-send-service");
        Assert.NotEmpty(sendId);

        var counter = _fixture.Harness.Client.VirtualObject("HarnessCounterObject", "send-key-1");
        var objectSendId = await counter.Send("Add", 7, idempotencyKey: "canonical-send-object");
        Assert.NotEmpty(objectSendId);

        // The send is accepted asynchronously; attaching to it proves it reached the handler.
        Assert.Equal("Hello, Sent!", (await _fixture.Harness.Client.Attach<GreetReply>(sendId)).Message);
        Assert.Equal(7, await _fixture.Harness.Client.Attach<int>(objectSendId));
    }

    [DockerFact]
    public async Task Virtual_object_key_with_reserved_characters_addresses_its_own_state()
    {
        var reserved = _fixture.Harness.Client.VirtualObject("HarnessCounterObject", "a/b c?d");
        var plain = _fixture.Harness.Client.VirtualObject("HarnessCounterObject", "a");

        Assert.Equal(4, await reserved.Call<int>("Add", 4));
        Assert.Equal(1, await plain.Call<int>("Add", 1));

        // Each key keeps its own state: the reserved characters did not split the key into
        // extra path segments, nor truncate it at the "/" or the "?".
        Assert.Equal(4, await reserved.Call<int>("Get"));
        Assert.Equal(1, await plain.Call<int>("Get"));
    }

    [DockerFact]
    public async Task Attach_and_get_output_resolve_a_finished_send()
    {
        var invocationId = await _fixture.Harness.Client
            .Service("HarnessGreeterService")
            .Send("Greet", new GreetRequest("Attached"), idempotencyKey: "attach-and-output");

        var attached = await _fixture.Harness.Client.Attach<GreetReply>(invocationId);
        Assert.Equal("Hello, Attached!", attached.Message);

        // Attach returned, so the invocation is complete and its output is readable.
        var output = await _fixture.Harness.Client.GetOutput<GreetReply>(invocationId);
        Assert.Equal("Hello, Attached!", output.Message);
    }

    [DockerFact]
    public async Task Cancel_of_a_blocked_invocation_is_rejected_by_the_ingress()
    {
        var invocationId = await _fixture.Harness.Client
            .Service("HarnessBlockingService")
            .Send("Block");

        // The ingress serves no cancel verb — cancellation is an admin-API operation — so the
        // deprecated invocation route is all the client can address, and the ingress rejects it.
        // Pinned here so the route can be revisited if the ingress ever grows a cancel verb.
        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => _fixture.Harness.Client.Cancel(invocationId));

        _output.WriteLine($"Cancel response: {ex.StatusCode}, source={ex.ErrorSource ?? "<none>"}, " +
                          $"code={ex.ErrorCode ?? "<none>"}, message={ex.Message}");
        Assert.NotNull(ex.StatusCode);
        Assert.InRange((int)ex.StatusCode.Value, 400, 499);
    }
}
