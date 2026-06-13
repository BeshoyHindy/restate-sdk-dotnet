using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

public class MockContextNamedSignalTests
{
    [Fact]
    public async Task NamedSignal_ReturnsSetupValue_AndCarriesName()
    {
        var ctx = new MockContext();
        ctx.SetupNamedSignal("approved");

        var signal = ctx.NamedSignal<string>("approval");
        Assert.Equal("approval", signal.Name);
        Assert.Equal("approved", await signal.Value);
    }

    [Fact]
    public async Task NamedSignal_NoSetup_ReturnsDefault()
    {
        var ctx = new MockContext();
        var signal = ctx.NamedSignal<string>("approval");
        Assert.Null(await signal.Value);
    }

    [Fact]
    public async Task SendSignal_RecordsTargetNameAndPayload()
    {
        var ctx = new MockContext();
        await ctx.SendSignal("inv-1", "approval", "payload");

        var recorded = Assert.Single(ctx.SignalSends);
        Assert.Equal("inv-1", recorded.TargetInvocationId);
        Assert.Equal("approval", recorded.Name);
        Assert.Equal("payload", recorded.Payload);
        Assert.Null(recorded.FailureReason);
    }

    [Fact]
    public async Task SendSignalFailure_RecordsReason()
    {
        var ctx = new MockContext();
        await ctx.SendSignalFailure("inv-1", "approval", "denied");

        var recorded = Assert.Single(ctx.SignalSends);
        Assert.Equal("approval", recorded.Name);
        Assert.Null(recorded.Payload);
        Assert.Equal("denied", recorded.FailureReason);
    }

    #region Keyed context types delegate through BaseContext

    [Fact]
    public async Task MockObjectContext_NamedSignalAndSend()
    {
        var ctx = new MockObjectContext();
        ctx.SetupNamedSignal("v");
        Assert.Equal("v", await ctx.NamedSignal<string>("n").Value);

        await ctx.SendSignal("inv", "n", "p");
        await ctx.SendSignalFailure("inv", "n", "r");
        Assert.Equal(2, ctx.SignalSends.Count);
    }

    [Fact]
    public async Task MockSharedObjectContext_NamedSignalAndSend()
    {
        var ctx = new MockSharedObjectContext();
        ctx.SetupNamedSignal("v");
        Assert.Equal("v", await ctx.NamedSignal<string>("n").Value);

        await ctx.SendSignal("inv", "n", "p");
        Assert.Single(ctx.SignalSends);
    }

    [Fact]
    public async Task MockWorkflowContext_NamedSignalAndSend()
    {
        var ctx = new MockWorkflowContext();
        ctx.SetupNamedSignal("v");
        Assert.Equal("v", await ctx.NamedSignal<string>("n").Value);

        await ctx.SendSignalFailure("inv", "n", "r");
        Assert.Single(ctx.SignalSends);
    }

    [Fact]
    public async Task MockSharedWorkflowContext_NamedSignalAndSend()
    {
        var ctx = new MockSharedWorkflowContext();
        ctx.SetupNamedSignal("v");
        Assert.Equal("v", await ctx.NamedSignal<string>("n").Value);

        await ctx.SendSignal("inv", "n", "p");
        Assert.Single(ctx.SignalSends);
    }

    #endregion
}
