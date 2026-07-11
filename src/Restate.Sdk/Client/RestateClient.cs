using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Restate.Sdk.Client;

/// <summary>
///     HTTP client for the Restate ingress API.
///     Use this to invoke Restate services from outside the Restate runtime.
///     <para>
///         Overloads without <see cref="JsonTypeInfo{T}" /> parameters use reflection-based JSON serialization
///         and are not compatible with Native AOT or trimming. For AOT applications, use the overloads that
///         accept <see cref="JsonTypeInfo{T}" /> from a source-generated <c>JsonSerializerContext</c>.
///     </para>
/// </summary>
public sealed class RestateClient : IDisposable
{
    private const string ReflectionJsonMessage =
        "This overload uses reflection-based JSON serialization. Use the JsonTypeInfo overload for Native AOT.";

    private static JsonSerializerOptions? s_reflectionJsonOptions;
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

    /// <summary>
    ///     Lazily creates the reflection-based serializer options so that AOT applications
    ///     using only the <see cref="JsonTypeInfo{T}" /> overloads never touch the reflection resolver.
    ///     The benign race on first use produces equivalent instances.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    private static JsonSerializerOptions GetReflectionJsonOptions()
    {
        return s_reflectionJsonOptions ??= CreateReflectionJsonOptions();

        [RequiresUnreferencedCode(ReflectionJsonMessage)]
        [RequiresDynamicCode(ReflectionJsonMessage)]
        static JsonSerializerOptions CreateReflectionJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            };
            options.MakeReadOnly();
            return options;
        }
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
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public async Task<TResponse> Attach<TResponse>(string invocationId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/restate/invocation/{invocationId}/attach", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>(GetReflectionJsonOptions(), ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Attaches to a running invocation by ID and awaits its result.
    ///     AOT-safe: deserializes the response using the provided <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="responseTypeInfo" /> is null.</exception>
    public async Task<TResponse> Attach<TResponse>(string invocationId, JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        var response = await _http.GetAsync($"/restate/invocation/{invocationId}/attach", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Gets the output of a completed invocation, or throws if not yet available.
    /// </summary>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public async Task<TResponse> GetOutput<TResponse>(string invocationId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/restate/invocation/{invocationId}/output", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>(GetReflectionJsonOptions(), ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Gets the output of a completed invocation, or throws if not yet available.
    ///     AOT-safe: deserializes the response using the provided <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="responseTypeInfo" /> is null.</exception>
    public async Task<TResponse> GetOutput<TResponse>(string invocationId, JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        var response = await _http.GetAsync($"/restate/invocation/{invocationId}/output", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Cancels a running invocation by ID.
    /// </summary>
    public async Task Cancel(string invocationId, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/restate/invocation/{invocationId}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    internal async Task<TResponse> CallAsync<TResponse>(string path, object? request, CancellationToken ct)
    {
        HttpResponseMessage response;
        if (request is not null)
            response = await _http.PostAsJsonAsync(path, request, GetReflectionJsonOptions(), ct).ConfigureAwait(false);
        else
            response = await _http.PostAsync(path, null, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TResponse>(GetReflectionJsonOptions(), ct).ConfigureAwait(false))!;
    }

    internal async Task<TResponse> CallAsync<TRequest, TResponse>(string path, TRequest? request,
        JsonTypeInfo<TRequest> requestTypeInfo, JsonTypeInfo<TResponse> responseTypeInfo, CancellationToken ct)
    {
        HttpResponseMessage response;
        if (request is not null)
            response = await _http.PostAsJsonAsync(path, request, requestTypeInfo, ct).ConfigureAwait(false);
        else
            response = await _http.PostAsync(path, null, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false))!;
    }

    internal async Task<TResponse> CallAsync<TResponse>(string path, JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct)
    {
        var response = await _http.PostAsync(path, null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false))!;
    }

    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    internal async Task<string> SendAsync(string path, object? request, TimeSpan? delay, string? idempotencyKey,
        CancellationToken ct)
    {
        using var httpRequest = CreateSendRequest(path, delay, idempotencyKey);
        if (request is not null)
            httpRequest.Content = JsonContent.Create(request, options: GetReflectionJsonOptions());

        return await SendCoreAsync(httpRequest, ct).ConfigureAwait(false);
    }

    internal async Task<string> SendAsync<TRequest>(string path, TRequest? request,
        JsonTypeInfo<TRequest> requestTypeInfo, TimeSpan? delay, string? idempotencyKey, CancellationToken ct)
    {
        using var httpRequest = CreateSendRequest(path, delay, idempotencyKey);
        if (request is not null)
            httpRequest.Content = JsonContent.Create(request, requestTypeInfo);

        return await SendCoreAsync(httpRequest, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateSendRequest(string path, TimeSpan? delay, string? idempotencyKey)
    {
        var url = delay.HasValue ? $"{path}?delay={delay.Value.TotalMilliseconds:F0}ms" : path;

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("x-restate-mode", "fire-and-forget");
        if (idempotencyKey is not null)
            httpRequest.Headers.Add("idempotency-key", idempotencyKey);

        return httpRequest;
    }

    private async Task<string> SendCoreAsync(HttpRequestMessage httpRequest, CancellationToken ct)
    {
        var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // The ingress returns the invocation ID in the response body
        var body = await response.Content
            .ReadFromJsonAsync(RestateClientJsonContext.Default.SendResponse, ct).ConfigureAwait(false);
        return body?.InvocationId ?? "";
    }
}

/// <summary>
///     Handle for invoking handlers on a specific service (optionally with a key).
/// </summary>
public readonly record struct ServiceHandle
{
    private const string ReflectionJsonMessage =
        "This overload uses reflection-based JSON serialization. Use the JsonTypeInfo overload for Native AOT.";

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
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public Task<TResponse> Call<TResponse>(string handler, object? request = null, CancellationToken ct = default)
    {
        return _client.CallAsync<TResponse>(BuildPath(handler), request, ct);
    }

    /// <summary>
    ///     Calls a handler and returns the response.
    ///     AOT-safe: serializes the request and deserializes the response using the provided
    ///     <see cref="JsonTypeInfo{T}" /> instances.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="requestTypeInfo" /> or <paramref name="responseTypeInfo" /> is null.
    /// </exception>
    public Task<TResponse> Call<TRequest, TResponse>(string handler, TRequest? request,
        JsonTypeInfo<TRequest> requestTypeInfo, JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        return _client.CallAsync(BuildPath(handler), request, requestTypeInfo, responseTypeInfo, ct);
    }

    /// <summary>
    ///     Calls a handler that takes no request payload and returns the response.
    ///     AOT-safe: deserializes the response using the provided <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="responseTypeInfo" /> is null.</exception>
    public Task<TResponse> Call<TResponse>(string handler, JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        return _client.CallAsync(BuildPath(handler), responseTypeInfo, ct);
    }

    /// <summary>Sends a one-way invocation and returns the invocation ID.</summary>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public Task<string> Send(string handler, object? request = null, TimeSpan? delay = null,
        string? idempotencyKey = null, CancellationToken ct = default)
    {
        return _client.SendAsync(BuildPath(handler), request, delay, idempotencyKey, ct);
    }

    /// <summary>
    ///     Sends a one-way invocation and returns the invocation ID.
    ///     AOT-safe: serializes the request using the provided <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="requestTypeInfo" /> is null.</exception>
    public Task<string> Send<TRequest>(string handler, TRequest? request, JsonTypeInfo<TRequest> requestTypeInfo,
        TimeSpan? delay = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        return _client.SendAsync(BuildPath(handler), request, requestTypeInfo, delay, idempotencyKey, ct);
    }

    private string BuildPath(string handler)
    {
        return _key is not null ? $"/{_service}/{_key}/{handler}" : $"/{_service}/{handler}";
    }
}

/// <summary>Response body returned by the ingress for fire-and-forget sends.</summary>
internal sealed record SendResponse(string InvocationId);

/// <summary>
///     Source-generated JSON context for the ingress client's own wire types.
///     Keeps invocation-ID parsing reflection-free so the AOT-safe overloads work under Native AOT.
/// </summary>
[JsonSerializable(typeof(SendResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class RestateClientJsonContext : JsonSerializerContext;
