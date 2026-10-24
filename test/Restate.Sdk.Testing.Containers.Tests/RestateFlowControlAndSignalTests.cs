using Restate.Sdk.Client;
using Xunit.Abstractions;

namespace Restate.Sdk.Testing.Containers.Tests;

/// <summary>
///     The protocol V7 features — the flow-control scope, the limit key, and signals — against a
///     real restate-server. restate-server 1.8 negotiates V7 by default, so these are the first
///     scenarios that exercise them end to end rather than at the wire-format level.
/// </summary>
[Collection(SharedRestateHarness.Name)]
public sealed class RestateFlowControlAndSignalTests
{
    /// <summary>How long a test waits for an invocation it sent to reach its handler.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly RestateHarnessFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RestateFlowControlAndSignalTests(RestateHarnessFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    private RestateClient Client => _fixture.Harness.Client;

    // ------- Scope -------

    [DockerFact]
    public async Task Scoped_call_and_send_from_the_ingress_reach_the_handler()
    {
        var reply = await Client
            .Service(nameof(HarnessScopedService))
            .Call<string>(nameof(HarnessScopedService.Echo), "scoped-call",
                CallOptions.WithScope(HarnessScopedService.Scope));
        Assert.Equal("echo:scoped-call", reply);

        // The idempotency key gives the send completion retention, so its result is readable.
        var invocationId = await Client
            .Service(nameof(HarnessScopedService))
            .Send(nameof(HarnessScopedService.Echo), "scoped-send",
                new SendOptions
                {
                    Scope = HarnessScopedService.Scope,
                    IdempotencyKey = "scoped-send"
                });
        Assert.Equal("echo:scoped-send", await Client.Attach<string>(invocationId));
    }

    [DockerFact]
    public async Task Scoped_call_from_a_handler_reaches_the_target()
    {
        // The inner call carries CallCommand.scope, which only exists on a protocol V7 stream.
        var reply = await Client
            .Service(nameof(HarnessScopedService))
            .Call<string>(nameof(HarnessScopedService.CallScoped), "from-handler");

        Assert.Equal("echo:from-handler", reply);
    }

    [DockerFact]
    public void Limit_key_without_a_scope_is_rejected_before_a_request_is_made()
    {
        // The server honours a limit key only inside a scope, so the client refuses to send one
        // without a scope rather than silently dropping the flow control the caller asked for.
        // The throw is synchronous: no HTTP request leaves the client.
        var handle = Client.Service(nameof(HarnessScopedService));

        Assert.Throws<ArgumentException>(() =>
        {
            _ = handle.Call<string>(nameof(HarnessScopedService.Echo), "unscoped",
                new CallOptions { LimitKey = HarnessScopedService.LimitKey });
        });

        Assert.Throws<ArgumentException>(() =>
        {
            _ = handle.Send(nameof(HarnessScopedService.Echo), "unscoped",
                new SendOptions { LimitKey = HarnessScopedService.LimitKey });
        });
    }

    // ------- Limit key -------

    [DockerFact]
    public async Task Call_with_a_scope_and_limit_key_completes_from_the_ingress_and_from_a_handler()
    {
        var fromIngress = await Client
            .Service(nameof(HarnessScopedService))
            .Call<string>(nameof(HarnessScopedService.Echo), "limited-ingress",
                CallOptions.WithScope(HarnessScopedService.Scope, HarnessScopedService.LimitKey));
        Assert.Equal("echo:limited-ingress", fromIngress);

        var fromHandler = await Client
            .Service(nameof(HarnessScopedService))
            .Call<string>(nameof(HarnessScopedService.CallScopedWithLimitKey), "limited-handler");
        Assert.Equal("echo:limited-handler", fromHandler);
    }

    // ------- Signals -------

    [DockerFact]
    public async Task Named_signal_resolved_from_another_handler_completes_the_awaiting_invocation()
    {
        var token = $"named-{Guid.NewGuid():N}";
        var invocationId = await StartSignalHandlerAsync(nameof(HarnessSignalService.AwaitNamed), token);

        var resolved = await Client
            .Service(nameof(HarnessSignalResolverService))
            .Call<string>(nameof(HarnessSignalResolverService.Resolve),
                new ResolveSignalRequest(invocationId, "go", "proceed"));
        Assert.Equal("resolved", resolved);

        Assert.Equal("proceed", await Client.Attach<string>(invocationId));
    }

    [DockerFact]
    public async Task Unnamed_signal_resolved_by_index_from_another_handler_completes_the_awaiting_invocation()
    {
        var token = $"unnamed-{Guid.NewGuid():N}";
        var invocationId = await StartSignalHandlerAsync(nameof(HarnessSignalService.AwaitUnnamed), token);

        // The index comes from the handle the awaiting handler got back, not from a literal.
        var signalIndex = HarnessSignalService.UnnamedSignalIndex(token);

        var resolved = await Client
            .Service(nameof(HarnessSignalResolverService))
            .Call<string>(nameof(HarnessSignalResolverService.ResolveByIndex),
                new ResolveSignalByIndexRequest(invocationId, signalIndex, "proceed-unnamed"));
        Assert.Equal("resolved", resolved);

        Assert.Equal("proceed-unnamed", await Client.Attach<string>(invocationId));
    }

    [DockerFact]
    public async Task Rejected_named_signal_fails_the_awaiting_invocation()
    {
        var token = $"rejected-{Guid.NewGuid():N}";
        var invocationId = await StartSignalHandlerAsync(nameof(HarnessSignalService.AwaitNamed), token);

        var rejected = await Client
            .Service(nameof(HarnessSignalResolverService))
            .Call<string>(nameof(HarnessSignalResolverService.Reject),
                new RejectSignalRequest(invocationId, "go", "not approved"));
        Assert.Equal("rejected", rejected);

        // The awaiting handler's signal future throws a TerminalException carrying the reason,
        // so the invocation ends in failure and the ingress reports it on attach.
        var ex = await Assert.ThrowsAsync<RestateIngressException>(() => Client.Attach<string>(invocationId));

        _output.WriteLine($"Attach after rejection: {ex.StatusCode}, code={ex.ErrorCode ?? "<none>"}, " +
                          $"message={ex.Message}");
        Assert.NotNull(ex.StatusCode);
        Assert.Contains("not approved", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Sends a signal-awaiting handler from the ingress and waits until it has started, so the
    ///     signal is resolved on an invocation the server has already begun. Returns its id.
    /// </summary>
    private async Task<string> StartSignalHandlerAsync(string handler, string token)
    {
        var invocationId = await Client
            .Service(nameof(HarnessSignalService))
            .Send(handler, new SignalStartRequest(token), idempotencyKey: token);

        Assert.NotEmpty(invocationId);
        await HarnessSignalService.Started(token).WaitAsync(StartTimeout);
        return invocationId;
    }
}
