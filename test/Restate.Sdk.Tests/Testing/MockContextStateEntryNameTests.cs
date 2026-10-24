using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

/// <summary>
///     The mock contexts accept an entry name on every state operation, so a handler written
///     against the named overloads is unit-testable unchanged. The name has no effect on the
///     in-memory state; the mocks only record it for verification.
/// </summary>
public class MockContextStateEntryNameTests
{
    private static readonly StateKey<int> Count = new("count");

    [Fact]
    public async Task MockObjectContext_NamedStateOperations_BehaveLikeUnnamedOnes()
    {
        var ctx = new MockObjectContext();

        ctx.Set(Count, 1, "save-count");
        Assert.Equal(1, await ctx.Get(Count, "load-count"));
        Assert.Equal(["count"], await ctx.StateKeys("list-keys"));

        ctx.Clear("count", "reset-count");
        Assert.False(ctx.HasState("count"));

        ctx.Set(Count, 2, "save-count-again");
        ctx.ClearAll("wipe-state");
        Assert.Empty(await ctx.StateKeys());

        Assert.Equal(
            ["save-count", "load-count", "list-keys", "reset-count", "save-count-again", "wipe-state"],
            ctx.StateEntryNames);
    }

    [Fact]
    public async Task MockWorkflowContext_NamedStateOperations_BehaveLikeUnnamedOnes()
    {
        var ctx = new MockWorkflowContext();

        ctx.Set(Count, 1, "save-count");
        Assert.Equal(1, await ctx.Get(Count, "load-count"));
        Assert.Equal(["count"], await ctx.StateKeys("list-keys"));

        ctx.Clear("count", "reset-count");
        Assert.False(ctx.HasState("count"));

        ctx.Set(Count, 2, "save-count-again");
        ctx.ClearAll("wipe-state");
        Assert.Empty(await ctx.StateKeys());

        Assert.Equal(
            ["save-count", "load-count", "list-keys", "reset-count", "save-count-again", "wipe-state"],
            ctx.StateEntryNames);
    }

    [Fact]
    public async Task MockSharedObjectContext_NamedStateReads_BehaveLikeUnnamedOnes()
    {
        var ctx = new MockSharedObjectContext();
        ctx.SetupState(Count, 7);

        Assert.Equal(7, await ctx.Get(Count, "load-count"));
        Assert.Equal(["count"], await ctx.StateKeys("list-keys"));
        Assert.Equal(["load-count", "list-keys"], ctx.StateEntryNames);
    }

    [Fact]
    public async Task MockSharedWorkflowContext_NamedStateReads_BehaveLikeUnnamedOnes()
    {
        var ctx = new MockSharedWorkflowContext();
        ctx.SetupState(Count, 7);

        Assert.Equal(7, await ctx.Get(Count, "load-count"));
        Assert.Equal(["count"], await ctx.StateKeys("list-keys"));
        Assert.Equal(["load-count", "list-keys"], ctx.StateEntryNames);
    }

    [Fact]
    public async Task MockObjectContext_UnnamedStateOperations_RecordNoEntryNames()
    {
        var ctx = new MockObjectContext();

        ctx.Set(Count, 1);
        Assert.Equal(1, await ctx.Get(Count));
        Assert.Equal(["count"], await ctx.StateKeys());
        ctx.Clear("count");
        ctx.ClearAll();

        Assert.Empty(ctx.StateEntryNames);
    }

    [Fact]
    public async Task MockObjectContext_EmptyEntryName_IsNotRecorded()
    {
        var ctx = new MockObjectContext();

        ctx.Set(Count, 1, null);
        Assert.Equal(1, await ctx.Get(Count, ""));

        Assert.Empty(ctx.StateEntryNames);
    }
}
