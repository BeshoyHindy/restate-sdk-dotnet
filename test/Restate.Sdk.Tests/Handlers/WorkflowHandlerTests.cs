using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Handlers;

// Inline handler for testing — workflow with state tracking and promises
[Workflow]
file class SignupWorkflow
{
    private static readonly StateKey<string> Status = new("status");
    private static readonly StateKey<string> AccountIdKey = new("accountId");

    [Handler]
    public async Task<string> Run(WorkflowContext ctx, string email)
    {
        ctx.Set(Status, "creating-account");

        var accountId = await ctx.Run("create-account",
            () => $"acct-{email.GetHashCode():X}");

        ctx.Set(AccountIdKey, accountId);
        ctx.Set(Status, "awaiting-verification");

        // Create an awakeable — the workflow suspends here until resolved externally
        var awakeable = ctx.Awakeable<string>();

        await ctx.Run("send-verification-email",
            () => $"sent-to-{email}-callback-{awakeable.Id}");

        // Wait for external verification (email click)
        var verificationCode = await awakeable.Value;

        ctx.Set(Status, "verified");

        await ctx.Run("activate-account",
            () => $"activated-{accountId}");

        ctx.Set(Status, "completed");

        return accountId;
    }

    [SharedHandler]
    public async Task<string> GetStatus(SharedWorkflowContext ctx)
    {
        return await ctx.Get(Status) ?? "unknown";
    }

    [SharedHandler]
    public async Task ResolveVerification(SharedWorkflowContext ctx, string code)
    {
        await ctx.ResolvePromise("verification", code);
    }
}

public class WorkflowHandlerTests
{
    [Fact]
    public async Task Run_CompletesFullLifecycle()
    {
        var ctx = new MockWorkflowContext("alice@example.com");
        ctx.SetupAwakeable("verification-code-123");

        var workflow = new SignupWorkflow();
        var result = await workflow.Run(ctx, "alice@example.com");

        Assert.NotNull(result);
        Assert.StartsWith("acct-", result);
    }

    [Fact]
    public async Task Run_SetsStatusThroughLifecycle()
    {
        var ctx = new MockWorkflowContext("bob@example.com");
        ctx.SetupAwakeable("code-456");

        var workflow = new SignupWorkflow();
        await workflow.Run(ctx, "bob@example.com");

        // After completion, status should be "completed"
        Assert.Equal("completed", ctx.GetStateValue<string>("status"));
    }

    [Fact]
    public async Task Run_StoresAccountId()
    {
        var ctx = new MockWorkflowContext("charlie@example.com");
        ctx.SetupAwakeable("code-789");

        var workflow = new SignupWorkflow();
        var accountId = await workflow.Run(ctx, "charlie@example.com");

        Assert.Equal(accountId, ctx.GetStateValue<string>("accountId"));
    }

    [Fact]
    public async Task GetStatus_ReturnsCurrentStep()
    {
        var ctx = new MockSharedWorkflowContext("user@example.com");
        ctx.SetupState(new StateKey<string>("status"), "awaiting-verification");

        var workflow = new SignupWorkflow();
        var status = await workflow.GetStatus(ctx);

        Assert.Equal("awaiting-verification", status);
    }

    [Fact]
    public async Task GetStatus_DefaultsToUnknown()
    {
        var ctx = new MockSharedWorkflowContext("new-user");

        var workflow = new SignupWorkflow();
        var status = await workflow.GetStatus(ctx);

        Assert.Equal("unknown", status);
    }

    [Fact]
    public async Task ResolveVerification_ResolvesPromise()
    {
        var ctx = new MockSharedWorkflowContext("user@example.com");

        var workflow = new SignupWorkflow();
        await workflow.ResolveVerification(ctx, "my-code");

        Assert.True(ctx.IsPromiseResolved("verification"));
        Assert.Equal("my-code", ctx.GetPromiseValue<string>("verification"));
    }
}