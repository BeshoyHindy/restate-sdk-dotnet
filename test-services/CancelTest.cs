using System.Text.Json;

namespace Restate.Sdk.TestServices;

[VirtualObject(Name = "CancelTestRunner")]
public sealed class CancelTestRunner
{
    private static readonly StateKey<bool> Canceled = new("state");

    [Handler(Name = "startTest")]
    public async Task StartTest(ObjectContext ctx, string operation)
    {
        try
        {
            await ctx.Call<string, JsonElement?>("CancelTestBlockingService", "block", operation, ctx.Key);
        }
        catch (TerminalException e) when (e.Code == 409)
        {
            ctx.Set(Canceled, true);
        }
    }

    [Handler(Name = "verifyTest")]
    public async Task<bool> VerifyTest(ObjectContext ctx)
    {
        return await ctx.Get(Canceled);
    }
}

[VirtualObject(Name = "CancelTestBlockingService")]
public sealed class CancelTestBlockingService
{
    [Handler(Name = "block")]
    public async Task Block(ObjectContext ctx, string operation)
    {
        var awakeable = ctx.Awakeable<string>();
        await ctx.Call<string, JsonElement?>("AwakeableHolder", "hold", awakeable.Id, ctx.Key);
        await awakeable.Value;

        switch (operation)
        {
            case "CALL":
                await ctx.Call<string, JsonElement?>("CancelTestBlockingService", "block", operation, ctx.Key);
                break;
            case "SLEEP":
                await ctx.Sleep(TimeSpan.FromDays(1024));
                break;
            case "AWAKEABLE":
                await ctx.Awakeable<string>().Value;
                break;
            default:
                throw new TerminalException($"Unknown operation '{operation}'");
        }
    }

    [Handler(Name = "isUnlocked")]
    public Task IsUnlocked(ObjectContext ctx)
    {
        return Task.CompletedTask;
    }
}
