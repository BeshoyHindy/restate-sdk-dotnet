using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

/// <summary>
///     End-to-end handler simulation tests combining multiple new features,
///     plus RecordedCall property tests.
/// </summary>
public class MockContextIntegrationTests
{
    #region RecordedCall — IdempotencyKey property

    [Fact]
    public void RecordedCall_IdempotencyKey_DefaultsToNull()
    {
        var call = new RecordedCall("Svc", null, "handler", null);

        Assert.Null(call.IdempotencyKey);
    }

    [Fact]
    public void RecordedCall_IdempotencyKey_CanBeSet()
    {
        var call = new RecordedCall("Svc", null, "handler", null, "my-key");

        Assert.Equal("my-key", call.IdempotencyKey);
    }

    #endregion

    #region End-to-end handler simulations

    [Fact]
    public async Task Handler_CanUseSyncRunWithRetryPolicy()
    {
        var ctx = new MockContext();
        Func<string> action = () => "Hello, World!";

        var greeting = await ctx.Run("build-greeting", action, RetryPolicy.FixedAttempts(3));

        Assert.Equal("Hello, World!", greeting);
    }

    [Fact]
    public async Task Handler_CanUseAsyncRunWithRetryPolicy()
    {
        var ctx = new MockContext();
        Func<Task<int>> action = async () =>
        {
            await Task.Delay(1);
            return 42;
        };

        var result = await ctx.Run("fetch-data", action, RetryPolicy.Default);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Handler_CanCallWithIdempotencyKey()
    {
        var ctx = new MockContext();
        ctx.SetupCall<string>("PaymentService", "charge", "txn-abc");

        var txnId = await ctx.Call<string>(
            service: "PaymentService",
            handler: "charge",
            request: new { Amount = 100 },
            options: CallOptions.WithIdempotencyKey("order-123")
        );

        Assert.Equal("txn-abc", txnId);
        Assert.Equal("order-123", ctx.Calls[0].IdempotencyKey);
    }

    [Fact]
    public async Task Handler_CanCancelInvocationAndVerify()
    {
        var ctx = new MockContext();

        await ctx.Send(
            service: "ReminderService",
            handler: "sendReminder",
            request: (object?)"data",
            delay: TimeSpan.FromHours(1)
        );

        await ctx.CancelInvocation("inv-to-cancel");

        Assert.Single(ctx.Sends);
        Assert.Single(ctx.Cancellations);
        Assert.Equal("inv-to-cancel", ctx.Cancellations[0]);
    }

    [Fact]
    public async Task Handler_CombinedFeatures_AllWorkTogether()
    {
        var ctx = new MockContext();
        ctx.SetupCall<string>("ExternalService", "process", "processed-data");

        // 1. Sync run with retry
        Func<string> prepareAction = () => "input-data";
        var prepared = await ctx.Run("prepare", prepareAction, RetryPolicy.FixedAttempts(2));

        // 2. Call with idempotency key
        var result = await ctx.Call<string>(
            service: "ExternalService",
            handler: "process",
            request: (object?)prepared,
            options: CallOptions.WithIdempotencyKey("unique-id")
        );

        // 3. Cancel a previous invocation
        await ctx.CancelInvocation("old-inv-123");

        Assert.Equal("input-data", prepared);
        Assert.Equal("processed-data", result);
        Assert.Single(ctx.Calls);
        Assert.Equal("unique-id", ctx.Calls[0].IdempotencyKey);
        Assert.Single(ctx.Cancellations);
    }

    #endregion
}
