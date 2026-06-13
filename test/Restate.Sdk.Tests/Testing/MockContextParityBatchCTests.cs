using Restate.Sdk.Testing;

namespace Restate.Sdk.Tests.Testing;

/// <summary>
///     Parity Batch C — exercises the new public-context overloads through the mock contexts, which do
///     NOT override them and therefore run the base-class DEFAULT bodies (the virtual/default-interface
///     fallbacks that forward to the existing 2-arg members):
///
///       * G32 — typed <c>Clear&lt;T&gt;(StateKey&lt;T&gt;)</c> forwards to <c>Clear(string)</c>.
///       * G28 — <c>RejectAwakeable(id, reason, code)</c> forwards to <c>RejectAwakeable(id, reason)</c>.
///       * G30 — <c>RejectPromise(name, reason, code)</c> forwards to <c>RejectPromise(name, reason)</c>.
/// </summary>
public class MockContextParityBatchCTests
{
    // G32 — typed Clear<T> on the object context clears the same key as Clear(string).
    [Fact]
    public void MockObjectContext_TypedClear_RemovesState()
    {
        var ctx = new MockObjectContext();
        var key = new StateKey<int>("count");
        ctx.Set(key, 7);
        Assert.True(ctx.HasState("count"));

        ctx.Clear(key);   // base ObjectContext.Clear<T> default → Clear("count")

        Assert.False(ctx.HasState("count"));
    }

    // G32 — the typed Clear<T> default body is reachable via the IObjectContext interface too.
    [Fact]
    public void IObjectContext_TypedClear_ForwardsToClearString()
    {
        IObjectContext ctx = new MockObjectContext();
        var key = new StateKey<string>("v");
        ctx.Set(key, "hello");
        ctx.Clear(key);   // IObjectContext.Clear<T> default-interface body → Clear("v")

        Assert.False(((MockObjectContext)ctx).HasState("v"));
    }

    // G28 — the custom-code RejectAwakeable default body forwards to the 2-arg override (no throw).
    [Fact]
    public void MockContext_RejectAwakeable_WithCode_Delegates()
    {
        var ctx = new MockContext();
        // Context.RejectAwakeable(id, reason, code) default → RejectAwakeable(id, reason).
        ctx.RejectAwakeable("ak_x", "gone", code: 410);
    }

    // G28 — the IContext default-interface RejectAwakeable(code) body forwards to the 2-arg member.
    [Fact]
    public void IContext_RejectAwakeable_WithCode_Delegates()
    {
        IContext ctx = new MockContext();
        ctx.RejectAwakeable("ak_y", "gone", code: 404);
    }

    // G30 — the custom-code RejectPromise default body on the workflow context forwards (no throw).
    [Fact]
    public async Task MockWorkflowContext_RejectPromise_WithCode_Delegates()
    {
        var ctx = new MockWorkflowContext();
        // WorkflowContext.RejectPromise(name, reason, code) default → RejectPromise(name, reason).
        await ctx.RejectPromise("approval", "denied", code: 409);
    }

    // G30 — the shared-workflow context default body forwards too.
    [Fact]
    public async Task MockSharedWorkflowContext_RejectPromise_WithCode_Delegates()
    {
        var ctx = new MockSharedWorkflowContext();
        await ctx.RejectPromise("approval", "denied", code: 409);
    }

    // G30 — the ISharedWorkflowContext default-interface RejectPromise(code) body forwards.
    [Fact]
    public async Task ISharedWorkflowContext_RejectPromise_WithCode_Delegates()
    {
        ISharedWorkflowContext ctx = new MockSharedWorkflowContext();
        await ctx.RejectPromise("approval", "denied", code: 409);
    }
}
