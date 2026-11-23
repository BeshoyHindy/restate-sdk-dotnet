namespace Restate.Sdk.TestServices;

[VirtualObject(Name = "AwakeableHolder")]
public sealed class AwakeableHolder
{
    private static readonly StateKey<string> Id = new("id");

    [Handler(Name = "hold")]
    public Task Hold(ObjectContext ctx, string id)
    {
        ctx.Set(Id, id);
        return Task.CompletedTask;
    }

    [Handler(Name = "hasAwakeable")]
    public async Task<bool> HasAwakeable(ObjectContext ctx)
    {
        return await ctx.Get(Id) is not null;
    }

    [Handler(Name = "unlock")]
    public async Task Unlock(ObjectContext ctx, string payload)
    {
        var id = await ctx.Get(Id) ?? throw new TerminalException("No awakeable is registered");
        ctx.ResolveAwakeable(id, payload);
    }
}
