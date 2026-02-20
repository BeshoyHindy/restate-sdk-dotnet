using Restate.Sdk;

namespace NativeAotCounter;

/// <summary>
///     A Virtual Object that maintains a durable counter per key, compiled with NativeAOT.
/// </summary>
[VirtualObject]
public sealed class CounterObject
{
    private static readonly StateKey<int> Count = new("count");

    [Handler]
    public async Task<int> Add(ObjectContext ctx, int delta)
    {
        var current = await ctx.Get(Count);
        var next = current + delta;
        ctx.Set(Count, next);
        return next;
    }

    [Handler]
    public Task Reset(ObjectContext ctx)
    {
        ctx.ClearAll();
        return Task.CompletedTask;
    }

    [SharedHandler]
    public async Task<int> Get(SharedObjectContext ctx)
    {
        return await ctx.Get(Count);
    }
}
