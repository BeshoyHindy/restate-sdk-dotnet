using Restate.Sdk.Client;

namespace Restate.Sdk.Testing.Containers.Tests;

/// <summary>
///     The ingress lookup route and the scope-bound client against a real restate-server. The
///     lookup route exists from restate-server 1.7 on, and the harness runs 1.8.
/// </summary>
[Collection(SharedRestateHarness.Name)]
public sealed class RestateLookupTests
{
    /// <summary>How long a test waits for an invocation it sent to reach its handler.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly RestateHarnessFixture _fixture;

    public RestateLookupTests(RestateHarnessFixture fixture)
    {
        _fixture = fixture;
    }

    private RestateClient Client => _fixture.Harness.Client;

    [DockerFact]
    public async Task Workflow_lookup_finds_the_running_invocation_by_name_and_key()
    {
        var key = $"lookup-{Guid.NewGuid():N}";
        var workflow = Client.Workflow(nameof(HarnessLookupWorkflow), key);

        var submitted = await workflow.Send(nameof(HarnessLookupWorkflow.Run), new SignalStartRequest(key));
        await HarnessLookupWorkflow.Started(key).WaitAsync(StartTimeout);

        // The caller did not keep the id the send returned: the workflow name and key are enough.
        var found = await Client.LookupWorkflow(nameof(HarnessLookupWorkflow), key);
        Assert.Equal(submitted, found.InvocationId);

        var attached = Client.Attach<string>(found.InvocationId);

        var accepted = await workflow.Call<string>(nameof(HarnessLookupWorkflow.Release), "released");
        Assert.Equal("accepted", accepted);

        Assert.Equal("released", await attached.WaitAsync(StartTimeout));
    }

    [DockerFact]
    public async Task Idempotent_invocation_lookup_finds_the_invocation_the_key_addresses()
    {
        var idempotencyKey = $"idem-{Guid.NewGuid():N}";

        var submitted = await Client
            .Service(nameof(HarnessGreeterService))
            .Send(nameof(HarnessGreeterService.Greet), new GreetRequest("Ada"), idempotencyKey: idempotencyKey);

        var found = await Client.LookupIdempotentInvocation(
            nameof(HarnessGreeterService), nameof(HarnessGreeterService.Greet), idempotencyKey);
        Assert.Equal(submitted, found.InvocationId);

        var reply = await Client.Attach<GreetReply>(found.InvocationId);
        Assert.Equal("Hello, Ada!", reply.Message);
    }

    [DockerFact]
    public async Task A_scope_bound_client_scopes_every_invocation_it_makes()
    {
        var scoped = Client.WithScope(HarnessScopedService.Scope);

        var reply = await scoped
            .Service(nameof(HarnessScopedService))
            .Call<string>(nameof(HarnessScopedService.Echo), "bound-scope");
        Assert.Equal("echo:bound-scope", reply);

        var invocationId = await scoped
            .Service(nameof(HarnessScopedService))
            .Send(nameof(HarnessScopedService.Echo), "bound-scope-send", idempotencyKey: "bound-scope-send");
        Assert.Equal("echo:bound-scope-send", await Client.Attach<string>(invocationId));
    }
}
