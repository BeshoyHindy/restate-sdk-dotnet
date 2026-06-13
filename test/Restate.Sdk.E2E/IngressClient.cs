using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Restate.Sdk.E2E;

/// <summary>
///     The deserialized result of an ingress invocation plus the wall-clock elapsed time, so a
///     scenario can assert both the durable post-condition and (loosely) that a suspend/resume
///     round trip actually happened.
/// </summary>
/// <typeparam name="T">The expected response shape.</typeparam>
public readonly record struct IngressResult<T>(T Value, TimeSpan Elapsed);

/// <summary>
///     A thin <see cref="HttpClient" /> wrapper over the restate-server ingress: invoke a handler,
///     fire-and-forget via the <c>/send</c> form, resolve/reject awakeables. Request bodies are
///     serialized with the SAME camelCase policy the SDK's source-generated serializer context uses
///     (the ReplayLab wire field is <c>probeId</c>, not <c>ProbeId</c>), so what the test sends is
///     exactly what the handler deserializes.
/// </summary>
public sealed class IngressClient : IDisposable
{
    // Matches the SDK's generated JsonSerializerContext options (camelCase). Kept local so the test
    // does not depend on internal SDK serializer state.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public IngressClient(string ingressBase)
    {
        // The server abort/retry cadence (a parked handler resumes only after the awaited
        // notification materializes) dominates latency, so the per-call timeout is generous.
        _http = new HttpClient
        {
            BaseAddress = new Uri(ingressBase),
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    /// <summary>
    ///     Invokes <c>{service}[/{key}]/{handler}</c> and awaits the durable result, returning it
    ///     deserialized as <typeparamref name="TResponse" /> along with the elapsed time. An
    ///     idempotency key (typically the probe id) makes the call safe to retry.
    /// </summary>
    public async Task<IngressResult<TResponse>> InvokeAsync<TResponse>(
        string service, string handler, object? body = null,
        string? idempotencyKey = null, string? key = null, CancellationToken ct = default)
    {
        var path = key is null ? $"/{service}/{handler}" : $"/{service}/{key}/{handler}";
        var stopwatch = Stopwatch.StartNew();
        using var response = await SendAsync(path, body, idempotencyKey, ct);
        stopwatch.Stop();

        var payload = await response.Content.ReadAsStringAsync(ct);
        var value = payload.Length == 0
            ? default!
            : JsonSerializer.Deserialize<TResponse>(payload, JsonOptions)!;
        return new IngressResult<TResponse>(value, stopwatch.Elapsed);
    }

    /// <summary>
    ///     Fire-and-forget submission via <c>{service}[/{key}]/{handler}/send</c>: the call returns
    ///     as soon as the server accepts the invocation, without waiting for its result. Used to
    ///     start a long-parked handler (E2/E8) so the test can then drive the wake-up out of band.
    /// </summary>
    public async Task SendAsync(
        string service, string handler, object? body = null,
        string? idempotencyKey = null, string? key = null, CancellationToken ct = default)
    {
        var path = key is null ? $"/{service}/{handler}/send" : $"/{service}/{key}/{handler}/send";
        using var _ = await SendAsync(path, body, idempotencyKey, ct);
    }

    /// <summary>
    ///     Fire-and-forget submission that RETURNS the server-assigned invocation id from the
    ///     <c>/send</c> response body (<c>{ "invocationId": "inv_..." }</c>). Used by E9 to learn the
    ///     parent invocation's id so the test can cancel it through the invocation route.
    /// </summary>
    public async Task<string> SendReturningIdAsync(
        string service, string handler, object? body = null,
        string? idempotencyKey = null, string? key = null, CancellationToken ct = default)
    {
        var path = key is null ? $"/{service}/{handler}/send" : $"/{service}/{key}/{handler}/send";
        using var response = await SendAsync(path, body, idempotencyKey, ct);
        var send = await response.Content.ReadFromJsonAsync<SendAck>(JsonOptions, ct);
        return send?.InvocationId
               ?? throw new InvalidOperationException($"/send to {path} returned no invocationId.");
    }

    /// <summary>The <c>/send</c> response body: the server-assigned invocation id of the accepted invocation.</summary>
    private sealed record SendAck(string InvocationId);

    /// <summary>
    ///     Attaches to a keyed workflow run and awaits its durable result. A workflow run handler
    ///     completes once per key; re-invoking <c>Run</c> returns 409 Conflict, so the result is
    ///     read through the dedicated attach route
    ///     (<c>GET /restate/workflow/{workflow}/{key}/attach</c>) instead.
    /// </summary>
    public async Task<TResponse> AttachWorkflowAsync<TResponse>(
        string workflow, string key, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"/restate/workflow/{workflow}/{key}/attach", ct);
        await EnsureSuccessAsync(response, $"attach workflow {workflow}/{key}", ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        return payload.Length == 0
            ? default!
            : JsonSerializer.Deserialize<TResponse>(payload, JsonOptions)!;
    }

    /// <summary>Resolves an awakeable through ingress with a JSON payload (E2's wake-up).</summary>
    public async Task ResolveAwakeableAsync(string awakeableId, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"/restate/awakeables/{awakeableId}/resolve", content, ct);
        await EnsureSuccessAsync(response, $"resolve awakeable {awakeableId}", ct);
    }

    /// <summary>Rejects an awakeable through ingress with a failure reason.</summary>
    public async Task RejectAwakeableAsync(string awakeableId, string reason, CancellationToken ct = default)
    {
        using var content = new StringContent(reason, Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync($"/restate/awakeables/{awakeableId}/reject", content, ct);
        await EnsureSuccessAsync(response, $"reject awakeable {awakeableId}", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("idempotency-key", idempotencyKey);

        var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"POST {path}", ct);
        return response;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        // Carry the StatusCode on the exception so callers can distinguish a benign 4xx (e.g. E8's
        // second promise-resolution returning 409 "promise already completed") from a 5xx server
        // crash. Without it ex.StatusCode is null and any guard keyed on the status mis-fires.
        throw new HttpRequestException(
            $"{what} failed: {(int)response.StatusCode} {response.StatusCode}\n{body}",
            inner: null, statusCode: response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}
