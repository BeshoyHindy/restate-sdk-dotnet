using System.Text.Json.Serialization;

namespace Restate.Sdk.TestServices;

[VirtualObject(Name = "ListObject")]
public sealed class ListObject
{
    private static readonly StateKey<List<string>> Items = new("list");

    [Handler(Name = "append")]
    public async Task Append(ObjectContext ctx, string value)
    {
        var items = await ctx.Get(Items) ?? [];
        items.Add(value);
        ctx.Set(Items, items);
    }

    [Handler(Name = "get")]
    public async Task<List<string>> Get(ObjectContext ctx)
    {
        return await ctx.Get(Items) ?? [];
    }

    [Handler(Name = "clear")]
    public async Task<List<string>> Clear(ObjectContext ctx)
    {
        var items = await ctx.Get(Items) ?? [];
        ctx.Clear(Items.Name);
        return items;
    }
}

public sealed record MapEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] string Value);

[VirtualObject(Name = "MapObject")]
public sealed class MapObject
{
    [Handler(Name = "set")]
    public Task Set(ObjectContext ctx, MapEntry entry)
    {
        ctx.Set(new StateKey<string>(entry.Key), entry.Value);
        return Task.CompletedTask;
    }

    [Handler(Name = "get")]
    public async Task<string> Get(ObjectContext ctx, string key)
    {
        return await ctx.Get(new StateKey<string>(key)) ?? "";
    }

    [Handler(Name = "clearAll")]
    public async Task<List<MapEntry>> ClearAll(ObjectContext ctx)
    {
        var entries = new List<MapEntry>();
        foreach (var key in await ctx.StateKeys())
        {
            var value = await ctx.Get(new StateKey<string>(key));
            entries.Add(new MapEntry(key, value ?? ""));
            ctx.Clear(key);
        }

        return entries;
    }
}
