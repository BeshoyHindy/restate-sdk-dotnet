using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Restate.Sdk.Client;

namespace Restate.Sdk.Tests.Client;

/// <summary>
///     Plan 07 §1.3 client-hosting lane. <see cref="RestateClient" /> and
///     <see cref="ServiceHandle" /> are the ingress helper the §2 E2E suite drives against a real
///     restate-server; the testcontainer suite does NOT feed the unit-coverage report, so these
///     in-process tests cover the request-shaping and response-handling logic directly with a
///     <see cref="CapturingHandler" /> mock <see cref="HttpMessageHandler" /> that records the
///     outgoing <see cref="HttpRequestMessage" /> and replays a canned response.
///     <para>
///         The assertions pin the ingress wire contract: the URL path
///         (<c>/{service}/{handler}</c> for services, <c>/{service}/{key}/{handler}</c> for keyed
///         objects/workflows, <c>/restate/...</c> for lifecycle), the HTTP method, the
///         <c>idempotency-key</c> and <c>x-restate-mode</c> headers, the optional <c>?delay=</c>
///         query, and the JSON body. Every public/internal member of both types is exercised.
///     </para>
/// </summary>
[SuppressMessage("Trimming", "IL2026",
    Justification = "RestateClient uses reflection-based JSON; acceptable under the test host.")]
[SuppressMessage("AOT", "IL3050",
    Justification = "RestateClient uses reflection-based JSON; acceptable under the test host.")]
public class RestateClientTests
{
    private const string BaseUrl = "http://localhost:8080";

    private static (RestateClient client, CapturingHandler handler) NewClient(
        HttpStatusCode status = HttpStatusCode.OK, string responseJson = "null")
    {
        var handler = new CapturingHandler(status, responseJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        return (new RestateClient(http), handler);
    }

    // ---- Construction overloads --------------------------------------------------------------

    [Fact]
    public void Ctor_StringBaseUrl_DelegatesToUriOverload()
    {
        // The string ctor forwards to the Uri ctor (line 23 → 30): it owns the HttpClient and sets
        // BaseAddress. Constructing then disposing exercises both the ctor chain and Dispose.
        using var client = new RestateClient(BaseUrl);
        Assert.NotNull(client);
    }

    [Fact]
    public void Ctor_UriBaseUrl_OwnsClient_DisposeIsIdempotent()
    {
        // The Uri ctor sets _ownsClient = true, so Dispose() disposes the internal HttpClient.
        var client = new RestateClient(new Uri(BaseUrl));
        client.Dispose();
        client.Dispose(); // second dispose must be safe.
    }

    [Fact]
    public void Ctor_HttpClient_DoesNotOwnClient_DisposeLeavesHttpClientUsable()
    {
        // The HttpClient ctor sets _ownsClient = false, so Dispose() must NOT dispose the caller's
        // HttpClient. We prove it by reusing the same HttpClient after the RestateClient is disposed.
        var handler = new CapturingHandler(HttpStatusCode.OK, "\"ok\"");
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };

        var client = new RestateClient(http);
        client.Dispose();

        // If Dispose() had disposed `http`, building/sending a request would throw
        // ObjectDisposedException. It must not.
        using var probe = new HttpRequestMessage(HttpMethod.Get, "/probe");
        var send = http.SendAsync(probe);
        Assert.NotNull(send);
    }

    // ---- ServiceHandle path building ---------------------------------------------------------

    [Fact]
    public async Task Service_Call_PostsToServiceHandlerPath_WithJsonBody()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "\"pong\"");
        using var _ = client;

        var result = await client.Service("Greeter").Call<string>("Greet", new { name = "World" });

        Assert.Equal("pong", result);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/Greeter/Greet", handler.LastRequest.RequestUri!.AbsolutePath);
        // Body is camelCase-serialized JSON of the request object.
        Assert.Contains("\"name\":\"World\"", handler.LastBody);
        Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task VirtualObject_Call_PostsToKeyedPath()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "42");
        using var _ = client;

        var result = await client.VirtualObject("Counter", "user-1").Call<int>("Add", new { delta = 5 });

        Assert.Equal(42, result);
        // Keyed path interpolates the key: /{service}/{key}/{handler}.
        Assert.Equal("/Counter/user-1/Add", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Workflow_Call_PostsToKeyedPath()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "\"approved\"");
        using var _ = client;

        var result = await client.Workflow("Approval", "wf-7").Call<string>("Run", new { value = 1 });

        Assert.Equal("approved", result);
        Assert.Equal("/Approval/wf-7/Run", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Call_NullRequest_PostsWithoutBody()
    {
        // CallAsync's request-is-null arm uses PostAsync(path, null) — no JSON content at all.
        var (client, handler) = NewClient(HttpStatusCode.OK, "\"hi\"");
        using var _ = client;

        var result = await client.Service("Greeter").Call<string>("Ping");

        Assert.Equal("hi", result);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/Greeter/Ping", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Null(handler.LastRequest.Content); // PostAsync(path, null) → no content
    }

    [Fact]
    public async Task Call_NonSuccessStatus_ThrowsHttpRequestException()
    {
        // EnsureSuccessStatusCode must surface a 500 as an HttpRequestException, not a deserialize.
        var (client, handler) = NewClient(HttpStatusCode.InternalServerError, "boom");
        using var _ = client;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.Service("Greeter").Call<string>("Greet", new { name = "x" }));
        Assert.NotNull(handler.LastRequest);
    }

    // ---- ServiceHandle.Send ------------------------------------------------------------------

    [Fact]
    public async Task Send_NoDelayNoIdempotency_SetsFireAndForgetHeaderAndReturnsInvocationId()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "{\"invocationId\":\"inv_abc\"}");
        using var _ = client;

        var id = await client.Service("Greeter").Send("Greet", new { name = "World" });

        Assert.Equal("inv_abc", id);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/Greeter/Greet", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Empty(handler.LastRequest.RequestUri.Query); // no ?delay= when delay is null
        Assert.Equal("fire-and-forget", handler.HeaderValue("x-restate-mode"));
        Assert.False(handler.HasHeader("idempotency-key")); // omitted when null
        Assert.Contains("\"name\":\"World\"", handler.LastBody);
    }

    [Fact]
    public async Task Send_WithDelay_AppendsDelayQueryInMilliseconds()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "{\"invocationId\":\"inv_d\"}");
        using var _ = client;

        var id = await client.Service("Greeter").Send("Greet", new { name = "y" },
            delay: TimeSpan.FromSeconds(2));

        Assert.Equal("inv_d", id);
        // 2s → "2000ms" with F0 formatting.
        Assert.Contains("delay=2000ms", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Send_WithIdempotencyKey_AddsHeader()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "{\"invocationId\":\"inv_k\"}");
        using var _ = client;

        var id = await client.Service("Greeter").Send("Greet", new { name = "z" },
            idempotencyKey: "key-123");

        Assert.Equal("inv_k", id);
        Assert.Equal("key-123", handler.HeaderValue("idempotency-key"));
    }

    [Fact]
    public async Task Send_NullRequest_OmitsBodyButStillSetsHeaders()
    {
        // SendAsync's request-is-null arm leaves httpRequest.Content null.
        var (client, handler) = NewClient(HttpStatusCode.OK, "{\"invocationId\":\"inv_n\"}");
        using var _ = client;

        var id = await client.Service("Greeter").Send("Ping");

        Assert.Equal("inv_n", id);
        Assert.Null(handler.LastRequest!.Content); // no JSON body
        Assert.Equal("fire-and-forget", handler.HeaderValue("x-restate-mode"));
    }

    [Fact]
    public async Task Send_KeyedObject_BuildsKeyedPath()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "{\"invocationId\":\"inv_o\"}");
        using var _ = client;

        await client.VirtualObject("Counter", "u9").Send("Add", new { delta = 1 });

        Assert.Equal("/Counter/u9/Add", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Send_EmptyResponseBody_ReturnsEmptyString()
    {
        // body deserializes to null (the ingress returned a JSON null) → the `?? ""` fallback arm.
        var (client, handler) = NewClient(HttpStatusCode.OK, "null");
        using var _ = client;

        var id = await client.Service("Greeter").Send("Greet", new { name = "q" });

        Assert.Equal("", id);
        Assert.NotNull(handler.LastRequest);
    }

    [Fact]
    public async Task Send_NonSuccessStatus_Throws()
    {
        var (client, _) = NewClient(HttpStatusCode.BadGateway, "down");
        using var _c = client;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.Service("Greeter").Send("Greet", new { name = "x" }));
    }

    // ---- Lifecycle endpoints (Attach / GetOutput / Cancel) -----------------------------------

    [Fact]
    public async Task Attach_GetsAttachPath_AndDeserializesResult()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "\"result-value\"");
        using var _ = client;

        var result = await client.Attach<string>("inv_xyz");

        Assert.Equal("result-value", result);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/restate/invocation/inv_xyz/attach", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetOutput_GetsOutputPath_AndDeserializesResult()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "123");
        using var _ = client;

        var result = await client.GetOutput<int>("inv_out");

        Assert.Equal(123, result);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/restate/invocation/inv_out/output", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Cancel_DeletesInvocationPath()
    {
        var (client, handler) = NewClient(HttpStatusCode.OK, "");
        using var _ = client;

        await client.Cancel("inv_cancel");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("/restate/invocation/inv_cancel", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Attach_NonSuccessStatus_Throws()
    {
        var (client, _) = NewClient(HttpStatusCode.NotFound, "missing");
        using var _c = client;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.Attach<string>("inv_404"));
    }

    [Fact]
    public async Task GetOutput_NonSuccessStatus_Throws()
    {
        var (client, _) = NewClient(HttpStatusCode.Conflict, "not-ready");
        using var _c = client;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetOutput<int>("inv_pending"));
    }

    [Fact]
    public async Task Cancel_NonSuccessStatus_Throws()
    {
        var (client, _) = NewClient(HttpStatusCode.InternalServerError, "err");
        using var _c = client;

        await Assert.ThrowsAsync<HttpRequestException>(() => client.Cancel("inv_bad"));
    }

    [Fact]
    public async Task Call_HonorsCancellationToken()
    {
        // A cancelled token must abort the request before any response is produced.
        var handler = new CapturingHandler(HttpStatusCode.OK, "\"never\"");
        var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        using var client = new RestateClient(http);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.Service("Greeter").Call<string>("Greet", new { name = "x" }, cts.Token));
    }

    /// <summary>
    ///     Records the outgoing request (URI, method, headers, buffered body) and replays a canned
    ///     status + JSON body. Buffering the content in <see cref="SendAsync" /> is required because
    ///     the request content stream is disposed once the call returns.
    /// </summary>
    private sealed class CapturingHandler(HttpStatusCode status, string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string LastBody { get; private set; } = "";

        public bool HasHeader(string name) =>
            LastRequest?.Headers.TryGetValues(name, out _) ?? false;

        public string? HeaderValue(string name) =>
            LastRequest is not null && LastRequest.Headers.TryGetValues(name, out var values)
                ? string.Join(",", values)
                : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            LastBody = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : "";

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
