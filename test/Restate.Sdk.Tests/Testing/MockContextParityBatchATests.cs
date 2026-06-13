using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

/// <summary>
///     Mock-context coverage for the Batch A public surface (G4-G7): CallHandle, custom call/send
///     headers, the SendOptions-based Send overloads, and Attach/GetOutput-by-target. These exercise
///     the public context API (MockContext + the keyed Mock*Context delegation) the protocol-level
///     <see cref="StateMachine.ParityBatchATests" /> cannot reach.
/// </summary>
public class MockContextParityBatchATests
{
    // ---- G4: CallHandle ----------------------------------------------------------------------

    [Fact]
    public async Task CallHandle_Unkeyed_ReturnsResultAndSyntheticInvocationId()
    {
        var ctx = new MockContext();
        ctx.SetupCall<string>("Svc", "handler", "result");

        var handle = ctx.CallHandle<string>("Svc", "handler", "req");

        Assert.Equal("result", await handle.GetResponseAsync());
        Assert.StartsWith("mock-inv-", await handle.GetInvocationIdAsync());
        var recorded = Assert.Single(ctx.Calls);
        Assert.Equal("Svc", recorded.Service);
        Assert.Null(recorded.Key);
        Assert.Equal("handler", recorded.Handler);
    }

    [Fact]
    public async Task CallHandle_Keyed_RecordsKeyAndOptions()
    {
        var ctx = new MockContext();
        ctx.SetupCall<int>("Obj", "k-1", "process", 7);

        var handle = ctx.CallHandle<int>("Obj", "k-1", "process", null,
            CallOptions.WithIdempotencyKey("idem-h"));

        Assert.Equal(7, await handle.GetResponseAsync());
        var recorded = Assert.Single(ctx.Calls);
        Assert.Equal("k-1", recorded.Key);
        Assert.Equal("idem-h", recorded.IdempotencyKey);
    }

    [Fact]
    public async Task CallHandle_FailureSetup_Throws()
    {
        var ctx = new MockContext();
        ctx.SetupCallFailure("Svc", "handler", new TerminalException("boom", 500));

        Assert.Throws<TerminalException>(() => ctx.CallHandle<string>("Svc", "handler"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CallHandle_OnKeyedContext_DelegatesThroughBaseContext()
    {
        var ctx = new MockObjectContext();
        ctx.SetupCall<string>("Svc", "h", "r");

        var handle = ctx.CallHandle<string>("Svc", "h");
        Assert.Equal("r", await handle.GetResponseAsync());
        Assert.Single(ctx.Calls);
    }

    // ---- G5: headers on CallOptions / SendOptions --------------------------------------------

    [Fact]
    public async Task Call_WithHeaders_RecordsHeaders()
    {
        var ctx = new MockContext();
        ctx.SetupCall<string>("Svc", "h", "v");
        var headers = new Dictionary<string, string> { ["x-a"] = "1" };

        await ctx.Call<string>("Svc", "k", "h", null, CallOptions.WithHeaders(headers));

        var recorded = Assert.Single(ctx.Calls);
        Assert.NotNull(recorded.Headers);
        Assert.Equal("1", recorded.Headers!["x-a"]);
    }

    [Fact]
    public async Task Send_WithSendOptionsHeaders_RecordsHeaders()
    {
        var ctx = new MockContext();
        var headers = new Dictionary<string, string> { ["x-b"] = "2" };

        await ctx.Send("Svc", "h", null, SendOptions.WithHeaders(headers));

        var recorded = Assert.Single(ctx.Sends);
        Assert.Equal("2", recorded.Headers!["x-b"]);
    }

    [Fact]
    public async Task Send_Keyed_WithSendOptions_RecordsKeyDelayAndHeaders()
    {
        var ctx = new MockContext();
        var headers = new Dictionary<string, string> { ["x-c"] = "3" };
        var options = new SendOptions
        {
            Delay = TimeSpan.FromSeconds(5),
            IdempotencyKey = "idem-s",
            Headers = headers
        };

        await ctx.Send("Svc", "k-9", "h", (object?)"req", options);

        var recorded = Assert.Single(ctx.Sends);
        Assert.Equal("k-9", recorded.Key);
        Assert.Equal(TimeSpan.FromSeconds(5), recorded.Delay);
        Assert.Equal("idem-s", recorded.IdempotencyKey);
        Assert.Equal("3", recorded.Headers!["x-c"]);
    }

    [Fact]
    public async Task Send_WithSendOptions_OnKeyedContext_DelegatesThroughBaseContext()
    {
        var ctx = new MockSharedObjectContext();
        var headers = new Dictionary<string, string> { ["x-d"] = "4" };

        await ctx.Send("Svc", "h", null, SendOptions.WithHeaders(headers));
        await ctx.Send("Svc", "k", "h", (object?)null, SendOptions.WithHeaders(headers));

        Assert.Equal(2, ctx.Sends.Count);
        Assert.All(ctx.Sends, s => Assert.Equal("4", s.Headers!["x-d"]));
    }

    // ---- G6/G7: Attach / GetOutput by target -------------------------------------------------

    [Fact]
    public async Task Attach_ByTarget_OnMock_ReturnsDefault()
    {
        var ctx = new MockContext();
        var result = await ctx.Attach<string>(AttachTarget.WorkflowId("Wf", "k"));
        Assert.Null(result);
    }

    [Fact]
    public async Task GetOutput_ByTarget_OnMock_ReturnsDefault()
    {
        var ctx = new MockContext();
        var result = await ctx.GetOutput<int>(AttachTarget.IdempotencyId("Svc", "h", "idem"));
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task Attach_ByTarget_OnKeyedContext_DelegatesThroughBaseContext()
    {
        var ctx = new MockWorkflowContext();
        var attached = await ctx.Attach<string>(AttachTarget.InvocationId("inv-x"));
        var output = await ctx.GetOutput<string>(AttachTarget.WorkflowId("Wf", "k"));
        Assert.Null(attached);
        Assert.Null(output);
    }

    // ---- AttachTarget factory guards ---------------------------------------------------------

    [Fact]
    public void AttachTarget_Factories_RejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => AttachTarget.InvocationId(null!));
        Assert.Throws<ArgumentNullException>(() => AttachTarget.WorkflowId(null!, "k"));
        Assert.Throws<ArgumentNullException>(() => AttachTarget.WorkflowId("n", null!));
        Assert.Throws<ArgumentNullException>(() => AttachTarget.IdempotencyId(null!, "h", "idem"));
        Assert.Throws<ArgumentNullException>(() => AttachTarget.IdempotencyId("s", null!, "idem"));
        Assert.Throws<ArgumentNullException>(() => AttachTarget.IdempotencyId("s", "h", null!));
    }
}
