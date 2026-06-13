using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;

namespace Restate.Sdk.Client;

/// <summary>
///     HTTP client for the Restate ingress API.
///     Use this to invoke Restate services from outside the Restate runtime.
/// </summary>
[RequiresUnreferencedCode("RestateClient uses reflection-based JSON serialization.")]
[RequiresDynamicCode("RestateClient uses reflection-based JSON serialization.")]
public sealed class RestateClient : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = CreateJsonOptions();
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>
    ///     Creates a new Restate ingress client pointing at the given base URL.
    /// </summary>
    /// <param name="baseUrl">The Restate ingress URL (e.g., "http://localhost:8080").</param>
    public RestateClient(string baseUrl) : this(new Uri(baseUrl))
    {
    }

    /// <summary>
    ///     Creates a new Restate ingress client pointing at the given base URL.
    /// </summary>
    public RestateClient(Uri baseUrl)
    {
        _http = new HttpClient { BaseAddress = baseUrl };
        _ownsClient = true;
    }

    /// <summary>
    ///     Creates a new Restate ingress client using an existing <see cref="HttpClient" />.
    ///     The caller retains ownership of the HttpClient.
    /// </summary>
    public RestateClient(HttpClient httpClient)
    {
        _http = httpClient;
        _ownsClient = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        // populateMissingResolver: the parameterless MakeReadOnly() throws on .NET 10 when the
        // options carries no TypeInfoResolver, even with reflection-based JSON enabled. Pass true so
        // the reflection-based DefaultJsonTypeInfoResolver is attached (the ingress client always
        // runs in a reflection-enabled host; AOT consumers use the source-generated client path).
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Gets a service handle for invoking handlers on a stateless service.</summary>
    public ServiceHandle Service(string serviceName)
    {
        return new ServiceHandle(this, serviceName, null);
    }

    /// <summary>Gets an object handle for invoking handlers on a virtual object with the given key.</summary>
    public ServiceHandle VirtualObject(string serviceName, string key)
    {
        return new ServiceHandle(this, serviceName, key);
    }

    /// <summary>Gets a workflow handle for invoking handlers on a workflow with the given key.</summary>
    public ServiceHandle Workflow(string serviceName, string key)
    {
        return new ServiceHandle(this, serviceName, key);
    }

    /// <summary>
    ///     Attaches to a running invocation by ID and awaits its result.
    /// </summary>
    public async Task<TResponse> Attach<TResponse>(string invocationId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/restate/invocation/{invocationId}/attach", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>(s_jsonOptions, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Gets the output of a completed invocation, or throws if not yet available.
    /// </summary>
    public async Task<TResponse> GetOutput<TResponse>(string invocationId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/restate/invocation/{invocationId}/output", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>(s_jsonOptions, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Cancels a running invocation by ID.
    /// </summary>
    public async Task Cancel(string invocationId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/restate/invocation/{invocationId}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    internal async Task<TResponse> CallAsync<TResponse>(string path, object? request, CancellationToken ct)
    {
        HttpResponseMessage response;
        if (request is not null)
            response = await _http.PostAsJsonAsync(path, request, s_jsonOptions, ct).ConfigureAwait(false);
        else
            response = await _http.PostAsync(path, null, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>(s_jsonOptions, ct).ConfigureAwait(false))!;
    }

    internal async Task<string> SendAsync(string path, object? request, TimeSpan? delay, string? idempotencyKey,
        CancellationToken ct)
    {
        var url = delay.HasValue ? $"{path}?delay={delay.Value.TotalMilliseconds:F0}ms" : path;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        if (request is not null)
            httpRequest.Content = JsonContent.Create(request, options: s_jsonOptions);

        httpRequest.Headers.Add("x-restate-mode", "fire-and-forget");
        if (idempotencyKey is not null)
            httpRequest.Headers.Add("idempotency-key", idempotencyKey);

        var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // The ingress returns the invocation ID in the response body
        var body = await response.Content.ReadFromJsonAsync<SendResponse>(s_jsonOptions, ct).ConfigureAwait(false);
        return body?.InvocationId ?? "";
    }

    private sealed record SendResponse(string InvocationId);
}

/// <summary>
///     Handle for invoking handlers on a specific service (optionally with a key).
/// </summary>
public readonly record struct ServiceHandle
{
    private readonly RestateClient _client;
    private readonly string? _key;
    private readonly string _service;

    internal ServiceHandle(RestateClient client, string service, string? key)
    {
        _client = client;
        _service = service;
        _key = key;
    }

    /// <summary>Calls a handler and returns the response.</summary>
    public Task<TResponse> Call<TResponse>(string handler, object? request = null, CancellationToken ct = default)
    {
        return _client.CallAsync<TResponse>(BuildPath(handler), request, ct);
    }

    /// <summary>Sends a one-way invocation and returns the invocation ID.</summary>
    public Task<string> Send(string handler, object? request = null, TimeSpan? delay = null,
        string? idempotencyKey = null, CancellationToken ct = default)
    {
        return _client.SendAsync(BuildPath(handler), request, delay, idempotencyKey, ct);
    }

    private string BuildPath(string handler)
    {
        return _key is not null ? $"/{_service}/{_key}/{handler}" : $"/{_service}/{handler}";
    }
}