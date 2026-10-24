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

    #region Flow-control options

    [Fact]
    public async Task Call_LimitKeyWithoutScope_Throws()
    {
        var ctx = new MockContext();
        var options = new CallOptions { LimitKey = "tenant-1" };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.Call<string>(service: "Svc", handler: "handler", request: null, options: options));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.Call<string>(service: "Obj", key: "key-1", handler: "handler", request: null, options: options));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.Call<string, string>(service: "Svc", handler: "handler", request: "req", key: null,
                options: options));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.Call<string, string>(service: "Obj", handler: "handler", request: "req", key: "key-1",
                options: options));

        Assert.Empty(ctx.Calls);
    }

    [Fact]
    public void CallFuture_LimitKeyWithoutScope_Throws()
    {
        var ctx = new MockContext();
        var options = new CallOptions { LimitKey = "tenant-1" };

        Assert.Throws<ArgumentException>(() =>
            ctx.CallFuture<string>(service: "Svc", handler: "handler", request: null, options: options));
        Assert.Throws<ArgumentException>(() =>
            ctx.CallFuture<string>(service: "Obj", key: "key-1", handler: "handler", request: null, options: options));

        Assert.Empty(ctx.Calls);
    }

    [Fact]
    public async Task Send_LimitKeyWithoutScope_Throws()
    {
        var ctx = new MockContext();
        var options = new SendOptions { LimitKey = "tenant-1" };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.Send(service: "Svc", handler: "handler", request: null, options: options));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.Send(service: "Obj", key: "key-1", handler: "handler", request: null, options: options));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.Send<string>(service: "Svc", handler: "handler", request: "req", key: null, options: options));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await ctx.Send<string>(service: "Obj", handler: "handler", request: "req", key: "key-1", options: options));

        Assert.Empty(ctx.Sends);
    }

    [Fact]
    public async Task Call_ScopeWithAndWithoutLimitKey_IsRecorded()
    {
        var ctx = new MockContext();

        await ctx.Call<string>(service: "Svc", handler: "handler", request: null,
            options: CallOptions.WithScope("orders"));
        await ctx.Call<string>(service: "Obj", key: "key-1", handler: "handler", request: null,
            options: CallOptions.WithScope("orders", "tenant-1"));
        ctx.CallFuture<string>(service: "Svc", handler: "handler", request: null,
            options: CallOptions.WithScope("orders", "tenant-2"));

        Assert.Equal(3, ctx.Calls.Count);
        Assert.Equal("orders", ctx.Calls[0].Scope);
        Assert.Null(ctx.Calls[0].LimitKey);
        Assert.Equal("orders", ctx.Calls[1].Scope);
        Assert.Equal("tenant-1", ctx.Calls[1].LimitKey);
        Assert.Equal("orders", ctx.Calls[2].Scope);
        Assert.Equal("tenant-2", ctx.Calls[2].LimitKey);
    }

    [Fact]
    public async Task Send_ScopeWithAndWithoutLimitKey_IsRecorded()
    {
        var ctx = new MockContext();

        await ctx.Send(service: "Svc", handler: "handler", request: null,
            options: SendOptions.WithScope("orders"));
        await ctx.Send(service: "Obj", key: "key-1", handler: "handler", request: null,
            options: SendOptions.WithScope("orders", "tenant-1"));

        Assert.Equal(2, ctx.Sends.Count);
        Assert.Equal("orders", ctx.Sends[0].Scope);
        Assert.Null(ctx.Sends[0].LimitKey);
        Assert.Equal("orders", ctx.Sends[1].Scope);
        Assert.Equal("tenant-1", ctx.Sends[1].LimitKey);
    }

    [Fact]
    public async Task TypedSend_RecordsEveryOption()
    {
        var ctx = new MockContext();
        var options = new SendOptions
        {
            Delay = TimeSpan.FromMinutes(5),
            IdempotencyKey = "idem-send",
            Scope = "orders",
            LimitKey = "tenant-1"
        };

        await ctx.Send<string>(service: "Obj", handler: "handler", request: "payload", key: "key-1", options: options);

        var send = Assert.Single(ctx.Sends);
        Assert.Equal("Obj", send.Service);
        Assert.Equal("key-1", send.Key);
        Assert.Equal("handler", send.Handler);
        Assert.Equal("payload", send.Request);
        Assert.Equal(TimeSpan.FromMinutes(5), send.Delay);
        Assert.Equal("idem-send", send.IdempotencyKey);
        Assert.Equal("orders", send.Scope);
        Assert.Equal("tenant-1", send.LimitKey);
    }

    #endregion

    #region Results and failures through every overload

    [Fact]
    public async Task Call_EveryOverload_ResolvesConfiguredResult()
    {
        var ctx = new MockContext();
        ctx.SetupCall("Svc", "handler", "unkeyed");
        ctx.SetupCall("Obj", "key-1", "handler", "keyed");

        var options = CallOptions.WithScope("orders", "tenant-1");

        Assert.Equal("unkeyed", await ctx.Call<string>("Svc", "handler"));
        Assert.Equal("keyed", await ctx.Call<string>("Obj", "key-1", "handler"));
        Assert.Equal("unkeyed",
            await ctx.Call<string>(service: "Svc", handler: "handler", request: null, options: options));
        Assert.Equal("keyed",
            await ctx.Call<string>(service: "Obj", key: "key-1", handler: "handler", request: null, options: options));
        Assert.Equal("unkeyed", await ctx.Call<string, string>("Svc", "handler", "req"));
        Assert.Equal("keyed", await ctx.Call<string, string>("Obj", "handler", "req", "key-1"));
        Assert.Equal("unkeyed",
            await ctx.Call<string, string>(service: "Svc", handler: "handler", request: "req", key: null,
                options: options));
        Assert.Equal("keyed",
            await ctx.Call<string, string>(service: "Obj", handler: "handler", request: "req", key: "key-1",
                options: options));
        Assert.Equal("unkeyed", await ctx.CallFuture<string>("Svc", "handler").GetResult());
        Assert.Equal("keyed", await ctx.CallFuture<string>("Obj", "key-1", "handler").GetResult());
        Assert.Equal("unkeyed",
            await ctx.CallFuture<string>(service: "Svc", handler: "handler", request: null, options: options)
                .GetResult());
        Assert.Equal("keyed",
            await ctx.CallFuture<string>(service: "Obj", key: "key-1", handler: "handler", request: null,
                options: options).GetResult());

        Assert.Equal(12, ctx.Calls.Count);
    }

    [Fact]
    public async Task Call_EveryOverload_ThrowsConfiguredFailure()
    {
        var ctx = new MockContext();
        ctx.SetupCallFailure("Svc", "handler", new TerminalException("unkeyed boom", 500));
        ctx.SetupCallFailure("Obj", "key-1", "handler", new TerminalException("keyed boom", 500));

        var options = CallOptions.WithScope("orders");

        await AssertThrows(() => ctx.Call<string>("Svc", "handler").AsTask(), "unkeyed boom");
        await AssertThrows(() => ctx.Call<string>("Obj", "key-1", "handler").AsTask(), "keyed boom");
        await AssertThrows(
            () => ctx.Call<string>(service: "Svc", handler: "handler", request: null, options: options).AsTask(),
            "unkeyed boom");
        await AssertThrows(
            () => ctx.Call<string>(service: "Obj", key: "key-1", handler: "handler", request: null, options: options)
                .AsTask(), "keyed boom");
        await AssertThrows(() => ctx.Call<string, string>("Svc", "handler", "req").AsTask(), "unkeyed boom");
        await AssertThrows(() => ctx.Call<string, string>("Obj", "handler", "req", "key-1").AsTask(), "keyed boom");
        await AssertThrows(
            () => ctx.Call<string, string>(service: "Svc", handler: "handler", request: "req", key: null,
                options: options).AsTask(), "unkeyed boom");
        await AssertThrows(
            () => ctx.Call<string, string>(service: "Obj", handler: "handler", request: "req", key: "key-1",
                options: options).AsTask(), "keyed boom");
        await AssertThrows(() => ctx.CallFuture<string>("Svc", "handler").GetResult().AsTask(), "unkeyed boom");
        await AssertThrows(() => ctx.CallFuture<string>("Obj", "key-1", "handler").GetResult().AsTask(), "keyed boom");
        await AssertThrows(
            () => ctx.CallFuture<string>(service: "Svc", handler: "handler", request: null, options: options)
                .GetResult().AsTask(), "unkeyed boom");
        await AssertThrows(
            () => ctx.CallFuture<string>(service: "Obj", key: "key-1", handler: "handler", request: null,
                options: options).GetResult().AsTask(), "keyed boom");

        Assert.Equal(12, ctx.Calls.Count);

        static async Task AssertThrows(Func<Task> call, string message)
        {
            var ex = await Assert.ThrowsAsync<TerminalException>(call);
            Assert.Equal(message, ex.Message);
        }
    }

    #endregion
}
