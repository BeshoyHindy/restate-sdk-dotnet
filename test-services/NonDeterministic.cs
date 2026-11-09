using System.Collections.Concurrent;
using System.Text.Json;

namespace Restate.Sdk.TestServices;

/// <summary>
///     Takes a different path on every other attempt, so a replay diverges from the journal
///     and the SDK must report a journal mismatch instead of silently continuing.
/// </summary>
[VirtualObject(Name = "NonDeterministic")]
public sealed class NonDeterministic
{
    private static readonly ConcurrentDictionary<string, int> InvokeCounts = new();

    [Handler(Name = "setDifferentKey")]
    public async Task SetDifferentKey(ObjectContext ctx)
    {
        ctx.Set(new StateKey<string>(DoLeftAction(ctx) ? "a" : "b"), "my-state");
        await ctx.Sleep(TimeSpan.FromMilliseconds(100));
        await IncrementCounter(ctx);
    }

    [Handler(Name = "backgroundInvokeWithDifferentTargets")]
    public async Task BackgroundInvokeWithDifferentTargets(ObjectContext ctx)
    {
        await ctx.Send("Counter", "abc", DoLeftAction(ctx) ? "get" : "reset", (object?)null);
        await ctx.Sleep(TimeSpan.FromMilliseconds(100));
        await IncrementCounter(ctx);
    }

    [Handler(Name = "callDifferentMethod")]
    public async Task CallDifferentMethod(ObjectContext ctx)
    {
        await ctx.Call<JsonElement?>("Counter", "abc", DoLeftAction(ctx) ? "get" : "reset");
        await ctx.Sleep(TimeSpan.FromMilliseconds(100));
        await IncrementCounter(ctx);
    }

    [Handler(Name = "eitherSleepOrCall")]
    public async Task EitherSleepOrCall(ObjectContext ctx)
    {
        if (DoLeftAction(ctx))
            await ctx.Sleep(TimeSpan.FromMilliseconds(100));
        else
            await ctx.Call<JsonElement?>("Counter", "abc", "get");
        await ctx.Sleep(TimeSpan.FromMilliseconds(100));
        await IncrementCounter(ctx);
    }

    private static bool DoLeftAction(ObjectContext ctx)
    {
        return InvokeCounts.AddOrUpdate(ctx.Key, 1, (_, count) => count + 1) % 2 == 1;
    }

    private static async Task IncrementCounter(ObjectContext ctx)
    {
        await ctx.Send("Counter", ctx.Key, "add", 1L);
    }
}
