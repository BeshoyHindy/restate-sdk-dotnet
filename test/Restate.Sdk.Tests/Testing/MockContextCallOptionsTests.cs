using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

public class MockContextCallOptionsTests
{
    #region Unkeyed service calls

    [Fact]
    public async Task Call_WithCallOptions_RecordsIdempotencyKey()
    {
        var ctx = new MockContext();
        ctx.SetupCall<string>("Svc", "handler", "result");

        await ctx.Call<string>(
            service: "Svc",
            handler: "handler",
            request: null,
            options: CallOptions.WithIdempotencyKey("idem-key-1")
        );

        Assert.Single(ctx.Calls);
        Assert.Equal("idem-key-1", ctx.Calls[0].IdempotencyKey);
    }

    [Fact]
    public async Task Call_WithoutCallOptions_HasNullIdempotencyKey()
    {
        var ctx = new MockContext();
        ctx.SetupCall<string>("Svc", "handler", "result");

        await ctx.Call<string>("Svc", "handler");

        Assert.Single(ctx.Calls);
        Assert.Null(ctx.Calls[0].IdempotencyKey);
    }

    [Fact]
    public async Task Call_WithCallOptions_FailureStillThrows()
    {
        var ctx = new MockContext();
        ctx.SetupCallFailure("Svc", "handler", new TerminalException("fail", 500));

        var ex = await Assert.ThrowsAsync<TerminalException>(
            async () =>
                await ctx.Call<string>(
                    service: "Svc",
                    handler: "handler",
                    request: null,
                    options: CallOptions.WithIdempotencyKey("idem-fail")
                )
        );

        Assert.Equal("fail", ex.Message);
        Assert.Single(ctx.Calls);
        Assert.Equal("idem-fail", ctx.Calls[0].IdempotencyKey);
    }

    #endregion

    #region Keyed service calls

    [Fact]
    public async Task Call_Keyed_WithCallOptions_RecordsIdempotencyKey()
    {
        var ctx = new MockContext();
        ctx.SetupCall<int>("Obj", "key-1", "process", 42);

        var result = await ctx.Call<int>(
            "Obj",
            "key-1",
            "process",
            null,
            CallOptions.WithIdempotencyKey("idem-key-2")
        );

        Assert.Equal(42, result);
        Assert.Single(ctx.Calls);
        Assert.Equal("Obj", ctx.Calls[0].Service);
        Assert.Equal("key-1", ctx.Calls[0].Key);
        Assert.Equal("process", ctx.Calls[0].Handler);
        Assert.Equal("idem-key-2", ctx.Calls[0].IdempotencyKey);
    }

    #endregion

    #region Keyed context types

    [Fact]
    public async Task MockObjectContext_Call_WithCallOptions_RecordsIdempotencyKey()
    {
        var ctx = new MockObjectContext();
        ctx.SetupCall<string>("Svc", "handler", "result");

        await ctx.Call<string>(
            service: "Svc",
            handler: "handler",
            request: null,
            options: CallOptions.WithIdempotencyKey("obj-idem")
        );

        Assert.Single(ctx.Calls);
        Assert.Equal("obj-idem", ctx.Calls[0].IdempotencyKey);
    }

    [Fact]
    public async Task MockSharedObjectContext_Call_WithCallOptions_RecordsIdempotencyKey()
    {
        var ctx = new MockSharedObjectContext();
        ctx.SetupCall<int>("Svc", "read", 10);

        await ctx.Call<int>(
            service: "Svc",
            handler: "read",
            request: null,
            options: CallOptions.WithIdempotencyKey("shared-idem")
        );

        Assert.Single(ctx.Calls);
        Assert.Equal("shared-idem", ctx.Calls[0].IdempotencyKey);
    }

    [Fact]
    public async Task MockWorkflowContext_Call_WithCallOptions_RecordsIdempotencyKey()
    {
        var ctx = new MockWorkflowContext();
        ctx.SetupCall<string>("Svc", "start", "wf-result");

        await ctx.Call<string>(
            service: "Svc",
            handler: "start",
            request: null,
            options: CallOptions.WithIdempotencyKey("wf-idem")
        );

        Assert.Single(ctx.Calls);
        Assert.Equal("wf-idem", ctx.Calls[0].IdempotencyKey);
    }

    #endregion
}
