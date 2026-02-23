using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

public class MockContextCancelInvocationTests
{
    [Fact]
    public async Task CancelInvocation_RecordsCancellation()
    {
        var ctx = new MockContext();

        await ctx.CancelInvocation("inv-123");

        Assert.Single(ctx.Cancellations);
        Assert.Equal("inv-123", ctx.Cancellations[0]);
    }

    [Fact]
    public async Task CancelInvocation_MultipleCancellations_AllRecorded()
    {
        var ctx = new MockContext();

        await ctx.CancelInvocation("inv-1");
        await ctx.CancelInvocation("inv-2");
        await ctx.CancelInvocation("inv-3");

        Assert.Equal(3, ctx.Cancellations.Count);
        Assert.Equal("inv-1", ctx.Cancellations[0]);
        Assert.Equal("inv-2", ctx.Cancellations[1]);
        Assert.Equal("inv-3", ctx.Cancellations[2]);
    }

    #region Keyed context types

    [Fact]
    public async Task MockObjectContext_CancelInvocation_RecordsCancellation()
    {
        var ctx = new MockObjectContext();

        await ctx.CancelInvocation("inv-obj-1");

        Assert.Single(ctx.Cancellations);
        Assert.Equal("inv-obj-1", ctx.Cancellations[0]);
    }

    [Fact]
    public async Task MockSharedObjectContext_CancelInvocation_RecordsCancellation()
    {
        var ctx = new MockSharedObjectContext();

        await ctx.CancelInvocation("inv-shared-1");

        Assert.Single(ctx.Cancellations);
        Assert.Equal("inv-shared-1", ctx.Cancellations[0]);
    }

    [Fact]
    public async Task MockWorkflowContext_CancelInvocation_RecordsCancellation()
    {
        var ctx = new MockWorkflowContext();

        await ctx.CancelInvocation("inv-wf-1");

        Assert.Single(ctx.Cancellations);
        Assert.Equal("inv-wf-1", ctx.Cancellations[0]);
    }

    [Fact]
    public async Task MockSharedWorkflowContext_CancelInvocation_RecordsCancellation()
    {
        var ctx = new MockSharedWorkflowContext();

        await ctx.CancelInvocation("inv-swf-1");

        Assert.Single(ctx.Cancellations);
        Assert.Equal("inv-swf-1", ctx.Cancellations[0]);
    }

    #endregion
}
