using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Restate.Sdk.TestServices;

// Kotlin serializes ByteArray as a JSON array of signed bytes.
public sealed record ProxyRequest(
    [property: JsonPropertyName("serviceName")] string ServiceName,
    [property: JsonPropertyName("virtualObjectKey")] string? VirtualObjectKey,
    [property: JsonPropertyName("handlerName")] string HandlerName,
    [property: JsonPropertyName("message")] sbyte[] Message,
    [property: JsonPropertyName("delayMillis")] int? DelayMillis,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("limitKey")] string? LimitKey);

public sealed record ManyCallRequest(
    [property: JsonPropertyName("proxyRequest")] ProxyRequest ProxyRequest,
    [property: JsonPropertyName("oneWayCall")] bool OneWayCall,
    [property: JsonPropertyName("awaitAtTheEnd")] bool AwaitAtTheEnd);

/// <summary>
///     Forwards a request to an arbitrary handler. The SDK has no raw-bytes call API, so the
///     message (always a JSON document in the suite) is parsed and re-serialized as JSON.
/// </summary>
[Service(Name = "Proxy")]
public sealed class Proxy
{
    [Handler(Name = "call")]
    public async Task<sbyte[]> Call(Context ctx, ProxyRequest request)
    {
        return ToBytes(await CallFuture(ctx, request).GetResult());
    }

    [Handler(Name = "oneWayCall")]
    public async Task<string> OneWayCall(Context ctx, ProxyRequest request)
    {
        var handle = await Send(ctx, request);
        return handle.InvocationId;
    }

    [Handler(Name = "manyCalls")]
    public async Task ManyCalls(Context ctx, List<ManyCallRequest> requests)
    {
        var toAwait = new List<IDurableFuture<JsonElement?>>();
        foreach (var request in requests)
        {
            if (request.OneWayCall)
            {
                await Send(ctx, request.ProxyRequest);
                continue;
            }

            var future = CallFuture(ctx, request.ProxyRequest);
            if (request.AwaitAtTheEnd)
                toAwait.Add(future);
        }

        foreach (var future in toAwait)
            await future.GetResult();
    }

    private static IDurableFuture<JsonElement?> CallFuture(Context ctx, ProxyRequest request)
    {
        var options = new CallOptions
        {
            IdempotencyKey = request.IdempotencyKey, Scope = request.Scope, LimitKey = request.LimitKey,
        };
        var body = ToJson(request.Message);
        return request.VirtualObjectKey is { } key
            ? ctx.CallFuture<JsonElement?>(request.ServiceName, key, request.HandlerName, body, options)
            : ctx.CallFuture<JsonElement?>(request.ServiceName, request.HandlerName, body, options);
    }

    private static ValueTask<InvocationHandle> Send(Context ctx, ProxyRequest request)
    {
        var options = new SendOptions
        {
            Delay = request.DelayMillis is { } delay ? TimeSpan.FromMilliseconds(delay) : null,
            IdempotencyKey = request.IdempotencyKey,
            Scope = request.Scope,
            LimitKey = request.LimitKey,
        };
        var body = ToJson(request.Message);
        return request.VirtualObjectKey is { } key
            ? ctx.Send(request.ServiceName, key, request.HandlerName, body, options)
            : ctx.Send(request.ServiceName, request.HandlerName, body, options);
    }

    private static JsonElement? ToJson(sbyte[] message)
    {
        if (message.Length == 0)
            return null;
        var bytes = Array.ConvertAll(message, b => (byte)b);
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    private static sbyte[] ToBytes(JsonElement? response)
    {
        if (response is not { } json)
            return [];
        return Array.ConvertAll(Encoding.UTF8.GetBytes(json.GetRawText()), b => (sbyte)b);
    }
}
