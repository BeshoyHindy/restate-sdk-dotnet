using System.Text.Json.Serialization;

namespace Restate.Sdk.TestServices;

public sealed record ResolveSignalRequest(
    [property: JsonPropertyName("invocationId")] string InvocationId,
    [property: JsonPropertyName("signalName")] string SignalName,
    [property: JsonPropertyName("value")] string Value);

public sealed record RejectSignalRequest(
    [property: JsonPropertyName("invocationId")] string InvocationId,
    [property: JsonPropertyName("signalName")] string SignalName,
    [property: JsonPropertyName("reason")] string Reason);

[Service(Name = "TestUtilsService")]
public sealed class TestUtilsService
{
    [Handler(Name = "echo")]
    public Task<string> Echo(Context ctx, string input)
    {
        return Task.FromResult(input);
    }

    [Handler(Name = "uppercaseEcho")]
    public Task<string> UppercaseEcho(Context ctx, string input)
    {
        return Task.FromResult(input.ToUpperInvariant());
    }

    [Handler(Name = "echoHeaders")]
    public Task<Dictionary<string, string>> EchoHeaders(Context ctx)
    {
        return Task.FromResult(new Dictionary<string, string>(ctx.Headers));
    }

    // The SDK only has a JSON serde, so this cannot yet echo raw application/octet-stream bodies.
    [Handler(Name = "rawEcho")]
    public Task<byte[]> RawEcho(Context ctx, byte[] input)
    {
        return Task.FromResult(input);
    }

    [Handler(Name = "sleepConcurrently")]
    public async Task SleepConcurrently(Context ctx, List<long> millisDuration)
    {
        var timers = millisDuration.Select(ms => ctx.Timer(TimeSpan.FromMilliseconds(ms))).ToArray();
        await foreach (var (_, error) in ctx.WaitAll(timers))
        {
            if (error is not null)
                throw error;
        }
    }

    [Handler(Name = "countExecutedSideEffects")]
    public async Task<int> CountExecutedSideEffects(Context ctx, int increments)
    {
        var invoked = 0;
        for (var i = 0; i < increments; i++)
        {
            await ctx.Run("count", () =>
            {
                invoked++;
                return Task.CompletedTask;
            });
        }

        return invoked;
    }

    [Handler(Name = "cancelInvocation")]
    public async Task CancelInvocation(Context ctx, string invocationId)
    {
        await ctx.CancelInvocation(invocationId);
    }

    [Handler(Name = "resolveSignal")]
    public async Task ResolveSignal(Context ctx, ResolveSignalRequest request)
    {
        await ctx.ResolveSignal(request.InvocationId, request.SignalName, request.Value);
    }

    [Handler(Name = "rejectSignal")]
    public async Task RejectSignal(Context ctx, RejectSignalRequest request)
    {
        await ctx.RejectSignal(request.InvocationId, request.SignalName, request.Reason);
    }
}
