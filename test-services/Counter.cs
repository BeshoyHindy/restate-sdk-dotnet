using System.Text.Json.Serialization;

namespace Restate.Sdk.TestServices;

public sealed record CounterUpdateResponse(
    [property: JsonPropertyName("oldValue")] long OldValue,
    [property: JsonPropertyName("newValue")] long NewValue);

[VirtualObject(Name = "Counter")]
public sealed class Counter
{
    private static readonly StateKey<long> Value = new("counter");

    [Handler(Name = "reset")]
    public Task Reset(ObjectContext ctx)
    {
        ctx.Clear(Value.Name);
        return Task.CompletedTask;
    }

    [SharedHandler(Name = "get")]
    public async Task<long> Get(SharedObjectContext ctx)
    {
        return await ctx.Get(Value);
    }

    [Handler(Name = "add")]
    public async Task<CounterUpdateResponse> Add(ObjectContext ctx, long value)
    {
        var oldValue = await ctx.Get(Value);
        var newValue = oldValue + value;
        ctx.Set(Value, newValue);
        return new CounterUpdateResponse(oldValue, newValue);
    }

    [Handler(Name = "addThenFail")]
    public async Task AddThenFail(ObjectContext ctx, long value)
    {
        var oldValue = await ctx.Get(Value);
        ctx.Set(Value, oldValue + value);
        throw new TerminalException(ctx.Key);
    }
}
