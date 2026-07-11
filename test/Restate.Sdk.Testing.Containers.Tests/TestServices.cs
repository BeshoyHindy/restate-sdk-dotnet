namespace Restate.Sdk.Testing.Containers.Tests;

public sealed record GreetRequest(string Name);

public sealed record GreetReply(string Message);

/// <summary>A trivial stateless service used to prove ingress round-trips through the harness.</summary>
[Service]
public sealed class HarnessGreeterService
{
    [Handler]
    public Task<GreetReply> Greet(Context ctx, GreetRequest request)
    {
        return Task.FromResult(new GreetReply($"Hello, {request.Name}!"));
    }
}

/// <summary>A virtual object used to prove state persists across calls to the same key.</summary>
[VirtualObject]
public sealed class HarnessCounterObject
{
    private static readonly StateKey<int> Count = new("count");

    [Handler]
    public async Task<int> Add(ObjectContext ctx, int delta)
    {
        var next = await ctx.Get(Count) + delta;
        ctx.Set(Count, next);
        return next;
    }

    [SharedHandler]
    public async Task<int> Get(SharedObjectContext ctx)
    {
        return await ctx.Get(Count);
    }
}
