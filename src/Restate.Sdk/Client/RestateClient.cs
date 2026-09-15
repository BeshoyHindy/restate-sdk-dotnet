using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Restate.Sdk.Internal;

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

    /// <summary>Response header carrying the Restate error source (restate-server 1.7.4 and newer).</summary>
    private const string ErrorSourceHeader = "x-restate-error-source";

    /// <summary>
    ///     Request header carrying the flow-control limit key. Matches sdk-go's ingress client,
    ///     which sends it as a header rather than the equivalent "limit-key" query parameter.
    /// </summary>
    private const string LimitKeyHeader = "x-restate-limit-key";

    private static JsonSerializerOptions? s_reflectionJsonOptions;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly JsonSerializerOptions? _reflectionJsonOptions;

    /// <summary>
    ///     Creates a new Restate ingress client pointing at the given base URL.
    /// </summary>
    /// <param name="baseUrl">The Restate ingress URL (e.g., "http://localhost:8080").</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseUrl" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseUrl" /> is empty.</exception>
    /// <exception cref="UriFormatException"><paramref name="baseUrl" /> is not a valid URI.</exception>
    public RestateClient(string baseUrl) : this(CreateBaseUri(baseUrl))
    {
    }

    /// <summary>
    ///     Creates a new Restate ingress client pointing at the given base URL.
    /// </summary>
    /// <param name="baseUrl">The Restate ingress URL (e.g., "http://localhost:8080").</param>
    /// <param name="options">Options controlling reflection-based JSON serialization.</param>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="baseUrl" /> or <paramref name="options" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="baseUrl" /> is empty.</exception>
    /// <exception cref="UriFormatException"><paramref name="baseUrl" /> is not a valid URI.</exception>
    public RestateClient(string baseUrl, RestateClientOptions options) : this(CreateBaseUri(baseUrl), options)
    {
    }

    /// <summary>
    ///     Creates a new Restate ingress client pointing at the given base URL.
    /// </summary>
    /// <param name="baseUrl">The Restate ingress URI (e.g., <c>http://localhost:8080</c>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="baseUrl" /> is <see langword="null" />.</exception>
    public RestateClient(Uri baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        _http = new HttpClient { BaseAddress = baseUrl };
        _ownsClient = true;
    }

    /// <summary>
    ///     Creates a new Restate ingress client pointing at the given base URL.
    /// </summary>
    /// <param name="baseUrl">The Restate ingress URI (e.g., <c>http://localhost:8080</c>).</param>
    /// <param name="options">Options controlling reflection-based JSON serialization.</param>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="baseUrl" /> or <paramref name="options" /> is <see langword="null" />.
    /// </exception>
    public RestateClient(Uri baseUrl, RestateClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(options);
        _http = new HttpClient { BaseAddress = baseUrl };
        _ownsClient = true;
        _reflectionJsonOptions = options.JsonSerializerOptions;
    }

    /// <summary>
    ///     Creates a new Restate ingress client using an existing <see cref="HttpClient" />.
    ///     The caller retains ownership of the HttpClient.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use; its <see cref="HttpClient.BaseAddress" /> must point at the ingress.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient" /> is <see langword="null" />.</exception>
    public RestateClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
        _ownsClient = false;
    }

    /// <summary>
    ///     Creates a new Restate ingress client using an existing <see cref="HttpClient" />.
    ///     The caller retains ownership of the HttpClient.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use; its <see cref="HttpClient.BaseAddress" /> must point at the ingress.</param>
    /// <param name="options">Options controlling reflection-based JSON serialization.</param>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="httpClient" /> or <paramref name="options" /> is <see langword="null" />.
    /// </exception>
    public RestateClient(HttpClient httpClient, RestateClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _http = httpClient;
        _ownsClient = false;
        _reflectionJsonOptions = options.JsonSerializerOptions;
    }

    /// <summary>
    ///     Throws a <see cref="RestateIngressException" /> when the ingress returned a non-success
    ///     status, carrying everything the server reported about it: the
    ///     <c>x-restate-error-source</c> header and the <c>source</c>, <c>code</c> and
    ///     <c>message</c> fields of the JSON error body (restate-server 1.7.4 and newer).
    ///     Servers that report none of it still produce the exception, with nulls.
    ///     Nothing on this path throws on its own — a body that cannot be read or parsed must
    ///     never mask the failure it describes.
    /// </summary>
    private static async Task EnsureIngressSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var headerSource = response.Headers.TryGetValues(ErrorSourceHeader, out var values)
            ? values.FirstOrDefault()
            : null;

        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Unreadable body (transport failure, bad encoding): report the status alone.
        }

        var (bodyMessage, bodySource, code) = ParseErrorBody(body);

        var message = bodyMessage
                      ?? (string.IsNullOrWhiteSpace(body)
                          ? $"The Restate ingress request failed with status {(int)response.StatusCode} ({response.StatusCode})."
                          : body);

        throw new RestateIngressException(message, response.StatusCode, headerSource ?? bodySource, code);
    }

    /// <summary>
    ///     Reads <c>message</c>, <c>source</c> and <c>code</c> out of a JSON error body. Returns
    ///     nulls for anything absent, and for a body that is not JSON at all or is truncated.
    ///     Uses <see cref="JsonDocument" /> rather than a deserialized shape: it needs no
    ///     reflection (AOT-safe), and it reads <c>code</c> whether the server sends it as a JSON
    ///     string or a number instead of failing the whole parse.
    /// </summary>
    private static (string? Message, string? Source, string? Code) ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null, null);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            return (ReadText(document.RootElement, "message"),
                ReadText(document.RootElement, "source"),
                ReadText(document.RootElement, "code"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        static string? ReadText(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }
    }
    private static Uri CreateBaseUri(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        return new Uri(baseUrl);
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
    private JsonSerializerOptions GetReflectionJsonOptions()
    {
        if (_reflectionJsonOptions is not null) return _reflectionJsonOptions;

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
    /// <exception cref="ArgumentNullException"><paramref name="invocationId" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="invocationId" /> is empty.</exception>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public async Task<TResponse> Attach<TResponse>(string invocationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invocationId);
        using var response = await _http.GetAsync($"/restate/invocation/{invocationId}/attach", ct)
            .ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<TResponse>(GetReflectionJsonOptions(), ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Attaches to a running invocation by ID and awaits its result.
    ///     AOT-safe: deserializes the response using the provided <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="invocationId" /> or <paramref name="responseTypeInfo" /> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="invocationId" /> is empty.</exception>
    public async Task<TResponse> Attach<TResponse>(string invocationId, JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invocationId);
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        using var response = await _http.GetAsync($"/restate/invocation/{invocationId}/attach", ct)
            .ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Gets the output of a completed invocation, or throws if not yet available.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="invocationId" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="invocationId" /> is empty.</exception>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public async Task<TResponse> GetOutput<TResponse>(string invocationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invocationId);
        using var response = await _http.GetAsync($"/restate/invocation/{invocationId}/output", ct)
            .ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<TResponse>(GetReflectionJsonOptions(), ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Gets the output of a completed invocation, or throws if not yet available.
    ///     AOT-safe: deserializes the response using the provided <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="invocationId" /> or <paramref name="responseTypeInfo" /> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="invocationId" /> is empty.</exception>
    public async Task<TResponse> GetOutput<TResponse>(string invocationId, JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invocationId);
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        using var response = await _http.GetAsync($"/restate/invocation/{invocationId}/output", ct)
            .ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    ///     Cancels a running invocation by ID.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="invocationId" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="invocationId" /> is empty.</exception>
    public async Task Cancel(string invocationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(invocationId);
        using var response = await _http.DeleteAsync($"/restate/invocation/{invocationId}", ct)
            .ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves a signal on a running invocation with a value.
    ///     <para>
    ///         <paramref name="signalId" /> is the id an invocation hands out for an unnamed
    ///         signal — <c>ctx.Awakeable&lt;T&gt;().Id</c>. The ingress addresses signals by id
    ///         only: a signal awaited by name can be resolved from a handler
    ///         (<c>InvocationHandle.ResolveSignal</c>) but has no ingress route.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="signalId" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException"><paramref name="signalId" /> is empty.</exception>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public async Task ResolveSignal<T>(string signalId, T value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalId);
        using var content = JsonContent.Create(value, options: GetReflectionJsonOptions());
        using var response = await _http.PostAsync(SignalPath(signalId, "resolve"), content, ct)
            .ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Resolves a signal on a running invocation with a value.
    ///     AOT-safe: serializes the value using the provided <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="signalId" /> or <paramref name="valueTypeInfo" /> is null.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="signalId" /> is empty.</exception>
    public async Task ResolveSignal<T>(string signalId, T value, JsonTypeInfo<T> valueTypeInfo,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalId);
        ArgumentNullException.ThrowIfNull(valueTypeInfo);
        using var content = JsonContent.Create(value, valueTypeInfo);
        using var response = await _http.PostAsync(SignalPath(signalId, "resolve"), content, ct)
            .ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Rejects a signal on a running invocation: the handler awaiting it fails with a terminal
    ///     error carrying <paramref name="reason" />.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="signalId" /> or <paramref name="reason" /> is <see langword="null" />.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="signalId" /> is empty.</exception>
    public async Task RejectSignal(string signalId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalId);
        ArgumentNullException.ThrowIfNull(reason);

        // The reject endpoint takes the reason as the raw body, not JSON.
        using var content = new StringContent(reason, Encoding.UTF8, "text/plain");
        using var response = await _http.PostAsync(SignalPath(signalId, "reject"), content, ct)
            .ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     The ingress route for completing a signal by id, which the server serves under
    ///     <c>/restate/awakeables</c> for both signal ids and legacy awakeable ids.
    /// </summary>
    private static string SignalPath(string signalId, string verb)
    {
        return $"/restate/awakeables/{signalId}/{verb}";
    }

    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    internal async Task<TResponse> CallAsync<TResponse>(string path, object? request, string? idempotencyKey,
        string? limitKey, CancellationToken ct)
    {
        using var httpRequest = CreateInvocationRequest(path, null, idempotencyKey, limitKey);
        if (request is not null)
            httpRequest.Content = JsonContent.Create(request, options: GetReflectionJsonOptions());

        using var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<TResponse>(GetReflectionJsonOptions(), ct).ConfigureAwait(false))!;
    }

    internal async Task<TResponse> CallAsync<TRequest, TResponse>(string path, TRequest? request,
        JsonTypeInfo<TRequest> requestTypeInfo, JsonTypeInfo<TResponse> responseTypeInfo, string? idempotencyKey,
        string? limitKey, CancellationToken ct)
    {
        using var httpRequest = CreateInvocationRequest(path, null, idempotencyKey, limitKey);
        if (request is not null)
            httpRequest.Content = JsonContent.Create(request, requestTypeInfo);

        using var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false))!;
    }

    internal async Task<TResponse> CallAsync<TResponse>(string path, JsonTypeInfo<TResponse> responseTypeInfo,
        string? idempotencyKey, string? limitKey, CancellationToken ct)
    {
        using var httpRequest = CreateInvocationRequest(path, null, idempotencyKey, limitKey);
        using var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(responseTypeInfo, ct).ConfigureAwait(false))!;
    }

    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    internal async Task<string> SendAsync(string sendPath, object? request, TimeSpan? delay, string? idempotencyKey,
        string? limitKey, CancellationToken ct)
    {
        using var httpRequest = CreateInvocationRequest(sendPath, delay, idempotencyKey, limitKey);
        if (request is not null)
            httpRequest.Content = JsonContent.Create(request, options: GetReflectionJsonOptions());

        return await SendCoreAsync(httpRequest, ct).ConfigureAwait(false);
    }

    internal async Task<string> SendAsync<TRequest>(string sendPath, TRequest? request,
        JsonTypeInfo<TRequest> requestTypeInfo, TimeSpan? delay, string? idempotencyKey, string? limitKey,
        CancellationToken ct)
    {
        using var httpRequest = CreateInvocationRequest(sendPath, delay, idempotencyKey, limitKey);
        if (request is not null)
            httpRequest.Content = JsonContent.Create(request, requestTypeInfo);

        return await SendCoreAsync(httpRequest, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds the POST for a call or send. <paramref name="path" /> is already complete — the
    ///     scope, and for a send the "/send" suffix or "send" verb, are part of it (see
    ///     <see cref="ServiceHandle" />).
    /// </summary>
    private static HttpRequestMessage CreateInvocationRequest(string path, TimeSpan? delay, string? idempotencyKey,
        string? limitKey)
    {
        var url = delay.HasValue
            ? $"{path}?delay={delay.Value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)}ms"
            : path;

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        if (idempotencyKey is not null)
            httpRequest.Headers.Add("idempotency-key", idempotencyKey);
        if (limitKey is not null)
            httpRequest.Headers.Add(LimitKeyHeader, limitKey);

        return httpRequest;
    }

    private async Task<string> SendCoreAsync(HttpRequestMessage httpRequest, CancellationToken ct)
    {
        using var response = await _http.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await EnsureIngressSuccessAsync(response, ct).ConfigureAwait(false);

        // The ingress returns the invocation ID in the response body
        var body = await response.Content
            .ReadFromJsonAsync(RestateClientJsonContext.Default.SendResponse, ct).ConfigureAwait(false);
        var invocationId = body?.InvocationId;
        if (string.IsNullOrWhiteSpace(invocationId))
            throw new JsonException("Restate ingress send response did not contain a non-empty invocationId.");

        return invocationId;
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
        return _client.CallAsync<TResponse>(BuildPath(handler, null, false), request, null, null, ct);
    }

    /// <summary>
    ///     Calls a handler with call options (idempotency key, scope, limit key) and returns the
    ///     response.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="options" /> carries a limit key without a scope.
    /// </exception>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public Task<TResponse> Call<TResponse>(string handler, object? request, CallOptions options,
        CancellationToken ct = default)
    {
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));
        return _client.CallAsync<TResponse>(BuildPath(handler, options.Scope, false), request,
            options.IdempotencyKey, options.LimitKey, ct);
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
        return _client.CallAsync(BuildPath(handler, null, false), request, requestTypeInfo, responseTypeInfo,
            null, null, ct);
    }

    /// <summary>
    ///     Calls a handler with call options (idempotency key, scope, limit key) and returns the
    ///     response. AOT-safe: serializes the request and deserializes the response using the
    ///     provided <see cref="JsonTypeInfo{T}" /> instances.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    ///     <paramref name="requestTypeInfo" /> or <paramref name="responseTypeInfo" /> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="options" /> carries a limit key without a scope.
    /// </exception>
    public Task<TResponse> Call<TRequest, TResponse>(string handler, TRequest? request,
        JsonTypeInfo<TRequest> requestTypeInfo, JsonTypeInfo<TResponse> responseTypeInfo, CallOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));
        return _client.CallAsync(BuildPath(handler, options.Scope, false), request, requestTypeInfo,
            responseTypeInfo, options.IdempotencyKey, options.LimitKey, ct);
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
        return _client.CallAsync(BuildPath(handler, null, false), responseTypeInfo, null, null, ct);
    }

    /// <summary>
    ///     Calls a handler that takes no request payload, with call options (idempotency key,
    ///     scope, limit key). AOT-safe: deserializes the response using the provided
    ///     <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="responseTypeInfo" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="options" /> carries a limit key without a scope.
    /// </exception>
    public Task<TResponse> Call<TResponse>(string handler, JsonTypeInfo<TResponse> responseTypeInfo,
        CallOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(responseTypeInfo);
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));
        return _client.CallAsync(BuildPath(handler, options.Scope, false), responseTypeInfo,
            options.IdempotencyKey, options.LimitKey, ct);
    }

    /// <summary>Sends a one-way invocation and returns the invocation ID.</summary>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public Task<string> Send(string handler, object? request = null, TimeSpan? delay = null,
        string? idempotencyKey = null, CancellationToken ct = default)
    {
        return _client.SendAsync(BuildPath(handler, null, true), request, delay, idempotencyKey, null, ct);
    }

    /// <summary>
    ///     Sends a one-way invocation with send options (delay, idempotency key, scope, limit key)
    ///     and returns the invocation ID.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <paramref name="options" /> carries a limit key without a scope.
    /// </exception>
    [RequiresUnreferencedCode(ReflectionJsonMessage)]
    [RequiresDynamicCode(ReflectionJsonMessage)]
    public Task<string> Send(string handler, object? request, SendOptions options, CancellationToken ct = default)
    {
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));
        return _client.SendAsync(BuildPath(handler, options.Scope, true), request, options.Delay,
            options.IdempotencyKey, options.LimitKey, ct);
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
        return _client.SendAsync(BuildPath(handler, null, true), request, requestTypeInfo, delay, idempotencyKey,
            null, ct);
    }

    /// <summary>
    ///     Sends a one-way invocation with send options (delay, idempotency key, scope, limit key)
    ///     and returns the invocation ID. AOT-safe: serializes the request using the provided
    ///     <see cref="JsonTypeInfo{T}" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="requestTypeInfo" /> is null.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="options" /> carries a limit key without a scope.
    /// </exception>
    public Task<string> Send<TRequest>(string handler, TRequest? request, JsonTypeInfo<TRequest> requestTypeInfo,
        SendOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestTypeInfo);
        FlowControl.Validate(options.Scope, options.LimitKey, nameof(options));
        return _client.SendAsync(BuildPath(handler, options.Scope, true), request, requestTypeInfo, options.Delay,
            options.IdempotencyKey, options.LimitKey, ct);
    }

    /// <summary>
    ///     Builds the ingress path. A scoped invocation uses the versioned ingress API, which
    ///     carries the scope and the verb in the path — the convention sdk-go's makeIngressUrl and
    ///     the Java client both follow, and the only form the runtime accepts a scope in:
    ///     <c>/restate/scope/{scope}/{call|send}/{service}[/{key}]/{handler}</c>.
    ///     An unscoped invocation keeps the unversioned <c>/{service}[/{key}]/{handler}[/send]</c>.
    /// </summary>
    private string BuildPath(string handler, string? scope, bool send)
    {
        if (scope is not null)
        {
            var verb = send ? "send" : "call";
            return _key is not null
                ? $"/restate/scope/{scope}/{verb}/{_service}/{_key}/{handler}"
                : $"/restate/scope/{scope}/{verb}/{_service}/{handler}";
        }

        var path = _key is not null ? $"/{_service}/{_key}/{handler}" : $"/{_service}/{handler}";
        return send ? $"{path}/send" : path;
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
