using System.Text.Json;

namespace Restate.Sdk.TestServices;

[VirtualObject(Name = "KillTestRunner")]
public sealed class KillTestRunner
{
    [Handler(Name = "startCallTree")]
    public async Task StartCallTree(ObjectContext ctx)
    {
        await ctx.Call<JsonElement?>("KillTestSingleton", ctx.Key, "recursiveCall");
    }
}

[VirtualObject(Name = "KillTestSingleton")]
public sealed class KillTestSingleton
{
    [Handler(Name = "recursiveCall")]
    public async Task RecursiveCall(ObjectContext ctx)
    {
        var awakeable = ctx.Awakeable<string>();
        await ctx.Send("AwakeableHolder", ctx.Key, "hold", (object?)awakeable.Id);
        await awakeable.Value;

        await ctx.Call<JsonElement?>("KillTestSingleton", ctx.Key, "recursiveCall");
    }

    [Handler(Name = "isUnlocked")]
    public Task IsUnlocked(ObjectContext ctx)
    {
        return Task.CompletedTask;
    }
}
