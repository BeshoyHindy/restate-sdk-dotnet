using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Restate.Sdk.Client;

namespace Restate.Sdk.Tests.Client;

internal sealed record GreetRequest(string Name);

internal sealed record GreetResponse(string Message);

[JsonSerializable(typeof(GreetRequest))]
[JsonSerializable(typeof(GreetResponse))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ClientTestJsonContext : JsonSerializerContext;

public class RestateClientTests
{
    private static readonly Uri AdminAddress = new("http://localhost:9070");

    private static (RestateClient Client, StubHandler Handler) CreateClient(RestateClientOptions? options = null)
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = options is null ? new RestateClient(http) : new RestateClient(http, options);
        return (client, handler);
    }

    /// <summary>
    ///     A client that can reach the admin API, which is what the operations the ingress does
    ///     not serve — currently cancellation — require.
    /// </summary>
    private static (RestateClient Client, StubHandler Handler) CreateAdminCapableClient()
    {
        return CreateClient(new RestateClientOptions { AdminAddress = AdminAddress });
    }

    // ── AOT-safe (JsonTypeInfo) overloads ──

    [Fact]
    public async Task Call_TypeInfo_PostsToServicePath()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello Ada"}""";

        var response = await client.Service("Greeter").Call(
            "Greet", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/restate/call/Greeter/Greet", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("""{"name":"Ada"}""", handler.LastRequestBody);
        Assert.Equal("hello Ada", response.Message);
    }

    [Fact]
    public async Task Call_TypeInfo_VirtualObject_PostsToKeyedPath()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"ok"}""";

        await client.VirtualObject("Counter", "my-key").Call(
            "Add", new GreetRequest("x"),
            ClientTestJsonContext.Default.GreetRequest, ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal("/restate/call/Counter/my-key/Add", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Call_TypeInfo_NoRequest_PostsEmptyBody()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"empty"}""";

        var response = await client.Workflow("Signup", "wf-1").Call(
            "Status", ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal("/restate/call/Signup/wf-1/Status", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Null(handler.LastRequestBody);
        Assert.Equal("empty", response.Message);
    }

    [Fact]
    public async Task Send_TypeInfo_PostsToSendPath_WithoutLegacyModeHeader_AndReturnsInvocationId()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-123"}""";

        var invocationId = await client.Service("Greeter").Send(
            "Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest);

        Assert.Equal("inv-123", invocationId);
        Assert.Equal("/restate/send/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.False(handler.LastRequest.Headers.Contains("x-restate-mode"));
        Assert.Equal("""{"name":"Ada"}""", handler.LastRequestBody);
    }

    [Fact]
    public async Task Send_TypeInfo_WithDelay_AppendsDelayQuery()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-1"}""";

        await client.Service("Greeter").Send(
            "Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest,
            delay: TimeSpan.FromSeconds(2));

        Assert.Equal("/restate/send/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("?delay=2000ms", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Send_TypeInfo_WithIdempotencyKey_SetsHeader()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-1"}""";

        await client.VirtualObject("Greeter", "ada").Send(
            "Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest,
            idempotencyKey: "key-42");

        Assert.Equal("/restate/send/Greeter/ada/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("key-42", Assert.Single(handler.LastRequest!.Headers.GetValues("idempotency-key")));
    }

    [Fact]
    public async Task Attach_TypeInfo_GetsAttachPath()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"attached"}""";

        var response = await client.Attach("inv-9", ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/restate/attach/inv-9", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("attached", response.Message);
    }

    [Fact]
    public async Task GetOutput_TypeInfo_GetsOutputPath()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"done"}""";

        var response = await client.GetOutput("inv-9", ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal("/restate/output/inv-9", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("done", response.Message);
    }

    // ── Failure modes ──

    [Fact]
    public async Task Call_NullLiteralRequest_BindsToTypeInfoOverload_ThrowsWithoutSending()
    {
        var (client, handler) = CreateClient();

        // A null argument is a better match for JsonTypeInfo<TResponse> than for object?, so this
        // binds to the AOT overload; it must fail fast before any HTTP request is issued.
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Service("Greeter").Call<GreetResponse>("Greet", null!));

        Assert.Equal("responseTypeInfo", ex.ParamName);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Send_NullThirdArgument_BindsToTypeInfoOverload_ThrowsWithoutSending()
    {
        var (client, handler) = CreateClient();

        // With a typed request, a null third argument binds to the JsonTypeInfo overload
        // (not the reflection overload's TimeSpan? delay); it must fail fast before sending.
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Service("Greeter").Send("Greet", new GreetRequest("Ada"), null!));

        Assert.Equal("requestTypeInfo", ex.ParamName);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Call_TypeInfo_ErrorStatus_StaysCatchableAsHttpRequestException()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        handler.ResponseBody = """{"message":"boom"}""";

        // The typed exception derives from HttpRequestException, so callers that caught what
        // EnsureSuccessStatusCode threw keep working.
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse));

        Assert.IsType<RestateIngressException>(ex);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"invocationId\":\"\"}")]
    [InlineData("{\"invocationId\":\"   \"}")]
    public async Task Send_TypeInfo_MissingInvocationId_ThrowsJsonException(string responseBody)
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = responseBody;

        await Assert.ThrowsAsync<JsonException>(() => client.Service("Greeter").Send(
            "Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest));
    }

    // ── Reflection overloads (behavioral parity) ──

    [Fact]
    public async Task Call_Reflection_PostsCamelCasedBody()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        var response = await client.Service("Greeter").Call<GreetResponse>("Greet", new GreetRequest("Ada"));

        Assert.Equal("/restate/call/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("""{"name":"Ada"}""", handler.LastRequestBody);
        Assert.Equal("hello", response.Message);
    }

    [Fact]
    public async Task Send_Reflection_ReturnsInvocationId()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-7"}""";

        var invocationId = await client.Service("Greeter").Send("Greet", new GreetRequest("Ada"));

        Assert.Equal("inv-7", invocationId);
        Assert.Equal("/restate/send/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.False(handler.LastRequest.Headers.Contains("x-restate-mode"));
    }

    [Fact]
    public async Task Reflection_CustomJsonOptions_ApplyToCallRequestAndResponse()
    {
        var options = new RestateClientOptions { JsonSerializerOptions = JsonSerializerOptions.Default };
        var (client, handler) = CreateClient(options);
        handler.ResponseBody = """{"Message":"hello Ada"}""";

        var response = await client.Service("Greeter").Call<GreetResponse>("Greet", new GreetRequest("Ada"));

        Assert.Equal("""{"Name":"Ada"}""", handler.LastRequestBody);
        Assert.Equal("hello Ada", response.Message);
    }

    [Fact]
    public async Task Reflection_DefaultClientOptions_PreserveCamelCaseBehavior()
    {
        var (client, handler) = CreateClient(new RestateClientOptions());
        handler.ResponseBody = """{"message":"hello Ada"}""";

        var response = await client.Service("Greeter").Call<GreetResponse>("Greet", new GreetRequest("Ada"));

        Assert.Equal("""{"name":"Ada"}""", handler.LastRequestBody);
        Assert.Equal("hello Ada", response.Message);
    }

    [Fact]
    public async Task TypeInfoOverloads_IgnoreReflectionJsonOptions()
    {
        var options = new RestateClientOptions { JsonSerializerOptions = JsonSerializerOptions.Default };
        var (client, handler) = CreateClient(options);
        handler.ResponseBody = """{"message":"hello Ada"}""";

        var response = await client.Service("Greeter").Call(
            "Greet", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal("""{"name":"Ada"}""", handler.LastRequestBody);
        Assert.Equal("hello Ada", response.Message);
    }

    [Fact]
    public async Task Reflection_CustomJsonOptions_ApplyToSendRequest()
    {
        var options = new RestateClientOptions { JsonSerializerOptions = JsonSerializerOptions.Default };
        var (client, handler) = CreateClient(options);
        handler.ResponseBody = """{"invocationId":"inv-custom"}""";

        var invocationId = await client.Service("Greeter").Send("Greet", new GreetRequest("Ada"));

        Assert.Equal("inv-custom", invocationId);
        Assert.Equal("""{"Name":"Ada"}""", handler.LastRequestBody);
    }

    [Fact]
    public async Task Reflection_CustomJsonOptions_ApplyToInvocationResponses()
    {
        var options = new RestateClientOptions { JsonSerializerOptions = JsonSerializerOptions.Default };
        var (client, handler) = CreateClient(options);
        handler.ResponseBody = """{"Message":"attached"}""";

        var attached = await client.Attach<GreetResponse>("inv-1");
        handler.ResponseBody = """{"Message":"output"}""";
        var output = await client.GetOutput<GreetResponse>("inv-1");

        Assert.Equal("attached", attached.Message);
        Assert.Equal("output", output.Message);
    }

    [Fact]
    public async Task Cancel_PatchesTheAdminCancelRoute()
    {
        var (client, handler) = CreateAdminCapableClient();

        await client.Cancel("inv-5");

        Assert.Equal(HttpMethod.Patch, handler.LastRequest!.Method);
        Assert.Equal("http://localhost:9070/invocations/inv-5/cancel", handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Cancel_WithoutAnAdminAddress_ThrowsWithoutSending()
    {
        var (client, handler) = CreateClient();

        // The ingress serves no cancel route, so a client that cannot reach the admin API must
        // say so rather than send a request every server version answers with an error.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.Cancel("inv-5"));

        Assert.Contains(nameof(RestateClientOptions.AdminAddress), ex.Message, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Cancel_AdminAddressWithAPathPrefix_KeepsThePrefix()
    {
        // An admin API behind a reverse proxy keeps its prefix, whether or not the configured
        // address ends in a slash: dropping it would address a route on the proxy itself.
        foreach (var address in new[] { "http://gateway/restate-admin", "http://gateway/restate-admin/" })
        {
            var (client, handler) = CreateClient(new RestateClientOptions { AdminAddress = new Uri(address) });

            await client.Cancel("inv-6");

            Assert.Equal("http://gateway/restate-admin/invocations/inv-6/cancel",
                handler.LastRequest!.RequestUri!.AbsoluteUri);
        }
    }

    [Fact]
    public async Task Responses_AreDisposed_AfterEveryClientOperation()
    {
        var (client, handler) = CreateClient();

        handler.ResponseBody = """{"message":"called"}""";
        await client.Service("Greeter").Call<GreetResponse>("Greet");
        Assert.True(handler.LastResponseContent!.IsDisposed);

        handler.ResponseBody = """{"invocationId":"inv-send"}""";
        await client.Service("Greeter").Send("Greet");
        Assert.True(handler.LastResponseContent!.IsDisposed);

        handler.ResponseBody = """{"message":"attached"}""";
        await client.Attach<GreetResponse>("inv-attach");
        Assert.True(handler.LastResponseContent!.IsDisposed);

        handler.ResponseBody = """{"message":"output"}""";
        await client.GetOutput<GreetResponse>("inv-output");
        Assert.True(handler.LastResponseContent!.IsDisposed);

        // Cancellation goes to the admin API, so it needs a client that can reach it.
        var (adminClient, adminHandler) = CreateAdminCapableClient();
        await adminClient.Cancel("inv-cancel");
        Assert.True(adminHandler.LastResponseContent!.IsDisposed);
    }

    // ── No-option overloads match their option-taking twins ──

    /// <summary>
    ///     Everything the ingress sees of a request: the no-option overloads delegate to the
    ///     option-taking ones, so for equivalent options both must put the same bytes on the wire.
    /// </summary>
    private static (string Method, string Url, string? Body, string? IdempotencyKey, string? LimitKey)
        Fingerprint(StubHandler handler)
    {
        var request = handler.LastRequest!;
        return (
            request.Method.Method,
            request.RequestUri!.AbsoluteUri,
            handler.LastRequestBody,
            request.Headers.TryGetValues("idempotency-key", out var idempotency) ? idempotency.Single() : null,
            request.Headers.TryGetValues("x-restate-limit-key", out var limit) ? limit.Single() : null);
    }

    [Fact]
    public async Task CallOverloads_WithoutOptions_SendWhatDefaultOptionsSend()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello Ada"}""";
        var handle = client.VirtualObject("Counter", "my-key");

        await handle.Call<GreetResponse>("Greet", new GreetRequest("Ada"));
        var reflection = Fingerprint(handler);
        await handle.Call<GreetResponse>("Greet", new GreetRequest("Ada"), default(CallOptions));
        Assert.Equal(Fingerprint(handler), reflection);

        await handle.Call("Greet", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, ClientTestJsonContext.Default.GreetResponse);
        var typed = Fingerprint(handler);
        await handle.Call("Greet", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, ClientTestJsonContext.Default.GreetResponse,
            default(CallOptions));
        Assert.Equal(Fingerprint(handler), typed);

        await handle.Call("Status", ClientTestJsonContext.Default.GreetResponse);
        var noRequest = Fingerprint(handler);
        await handle.Call("Status", ClientTestJsonContext.Default.GreetResponse, default(CallOptions));
        Assert.Equal(Fingerprint(handler), noRequest);
    }

    [Fact]
    public async Task SendOverloads_WithoutOptions_SendWhatTheEquivalentSendOptionsSend()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-1"}""";
        var handle = client.VirtualObject("Counter", "my-key");

        await handle.Send("Greet", new GreetRequest("Ada"));
        var reflection = Fingerprint(handler);
        await handle.Send("Greet", new GreetRequest("Ada"), default(SendOptions));
        Assert.Equal(Fingerprint(handler), reflection);

        await handle.Send("Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest);
        var typed = Fingerprint(handler);
        await handle.Send("Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest,
            default(SendOptions));
        Assert.Equal(Fingerprint(handler), typed);
    }

    [Fact]
    public async Task SendOverloads_LooseDelayAndIdempotencyKey_MapOntoSendOptions()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-1"}""";
        var handle = client.Service("Greeter");
        var delay = TimeSpan.FromSeconds(30);
        var options = new SendOptions { Delay = delay, IdempotencyKey = "order-1" };

        await handle.Send("Greet", new GreetRequest("Ada"), delay, "order-1");
        var reflection = Fingerprint(handler);
        await handle.Send("Greet", new GreetRequest("Ada"), options);
        Assert.Equal(Fingerprint(handler), reflection);

        await handle.Send("Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest,
            delay, "order-1");
        var typed = Fingerprint(handler);
        await handle.Send("Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest, options);
        Assert.Equal(Fingerprint(handler), typed);

        // The mapping is observable, not just self-consistent.
        Assert.Equal("?delay=30000ms", handler.LastRequest!.RequestUri!.Query);
        Assert.Equal("order-1", typed.IdempotencyKey);
    }

    [Fact]
    public async Task AotOverloads_WithoutOptions_StillRejectANullTypeInfo()
    {
        var (client, handler) = CreateClient();
        var handle = client.Service("Greeter");

        // The null checks now run in the option-taking twin, so they must still name the parameter
        // the caller passed and still fail before a request is made.
        Assert.Equal("requestTypeInfo", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            handle.Call<GreetRequest, GreetResponse>("Greet", new GreetRequest("Ada"), null!,
                ClientTestJsonContext.Default.GreetResponse))).ParamName);
        Assert.Equal("responseTypeInfo", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            handle.Call<GreetRequest, GreetResponse>("Greet", new GreetRequest("Ada"),
                ClientTestJsonContext.Default.GreetRequest, null!))).ParamName);
        Assert.Equal("responseTypeInfo", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            handle.Call("Status", (JsonTypeInfo<GreetResponse>)null!))).ParamName);
        Assert.Equal("requestTypeInfo", (await Assert.ThrowsAsync<ArgumentNullException>(() =>
            handle.Send("Greet", new GreetRequest("Ada"), null!))).ParamName);

        Assert.Null(handler.LastRequest);
    }

    // ── Argument validation ──

    [Fact]
    public void Constructor_NullArguments_ThrowWithDomainParamNames()
    {
        Assert.Equal("baseUrl",
            Assert.Throws<ArgumentNullException>(() => new RestateClient((string)null!)).ParamName);
        Assert.Equal("baseUrl",
            Assert.Throws<ArgumentNullException>(() => new RestateClient((Uri)null!)).ParamName);
        Assert.Equal("httpClient",
            Assert.Throws<ArgumentNullException>(() => new RestateClient((HttpClient)null!)).ParamName);
        Assert.Throws<ArgumentException>(() => new RestateClient(""));

        Assert.Equal("options",
            Assert.Throws<ArgumentNullException>(() => new RestateClient("http://localhost:8080", null!)).ParamName);
        Assert.Equal("options",
            Assert.Throws<ArgumentNullException>(() => new RestateClient(
                new Uri("http://localhost:8080"), null!)).ParamName);
        Assert.Equal("options",
            Assert.Throws<ArgumentNullException>(() => new RestateClient(
                new HttpClient(), null!)).ParamName);
    }

    [Fact]
    public void ClientOptions_NullJsonSerializerOptions_Throws()
    {
        var options = new RestateClientOptions();

        var ex = Assert.Throws<ArgumentNullException>(() => options.JsonSerializerOptions = null!);

        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void ClientOptions_RelativeAdminAddress_ThrowsAndNullIsAccepted()
    {
        var options = new RestateClientOptions();

        // A relative address cannot name a host, so it would compose a request URI that only
        // fails once sent; and null is the default, meaning "no admin API configured".
        var ex = Assert.Throws<ArgumentException>(
            () => options.AdminAddress = new Uri("/invocations", UriKind.Relative));
        Assert.Equal("value", ex.ParamName);

        Assert.Null(options.AdminAddress);
        options.AdminAddress = AdminAddress;
        Assert.Equal(AdminAddress, options.AdminAddress);
        options.AdminAddress = null;
        Assert.Null(options.AdminAddress);
    }

    [Fact]
    public async Task InvocationOperations_NullOrEmptyInvocationId_ThrowWithoutSending()
    {
        var (client, handler) = CreateClient();

        // A null/empty id would silently build "/restate/attach/" with an empty id and surface as an
        // opaque 404 from the server — it must fail fast at the call site instead.
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.Attach<GreetResponse>(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Attach<GreetResponse>(""));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Attach("", ClientTestJsonContext.Default.GreetResponse));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetOutput<GreetResponse>(""));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetOutput("", ClientTestJsonContext.Default.GreetResponse));
        // Cancel checks its argument before it checks for an admin address, so an empty id
        // reports the caller's mistake rather than the missing configuration.
        await Assert.ThrowsAsync<ArgumentException>(() => client.Cancel(""));

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public void HandleFactories_NullOrEmptyServiceName_ThrowWithoutSending()
    {
        var (client, handler) = CreateClient();

        // An empty name would build "/restate/call//Greet" and a null one would only fail inside
        // Uri.EscapeDataString, naming its own parameter instead of the caller's.
        Assert.Equal("serviceName", Assert.Throws<ArgumentNullException>(() => client.Service(null!)).ParamName);
        Assert.Equal("serviceName", Assert.Throws<ArgumentException>(() => client.Service("")).ParamName);
        Assert.Equal("serviceName",
            Assert.Throws<ArgumentNullException>(() => client.VirtualObject(null!, "my-key")).ParamName);
        Assert.Equal("serviceName",
            Assert.Throws<ArgumentException>(() => client.VirtualObject("", "my-key")).ParamName);
        Assert.Equal("serviceName",
            Assert.Throws<ArgumentNullException>(() => client.Workflow(null!, "wf-1")).ParamName);
        Assert.Equal("serviceName", Assert.Throws<ArgumentException>(() => client.Workflow("", "wf-1")).ParamName);

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task KeyedHandleFactories_NullOrEmptyKey_ThrowInsteadOfMisroutingAsUnkeyed()
    {
        var (client, handler) = CreateClient();

        // A keyless handle on a keyed service used to be built happily and then addressed the
        // unkeyed route "/restate/call/X/Greet", which only the server rejects.
        Assert.Equal("key", Assert.Throws<ArgumentNullException>(() => client.VirtualObject("X", null!)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => client.VirtualObject("X", "")).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentNullException>(() => client.Workflow("X", null!)).ParamName);
        Assert.Equal("key", Assert.Throws<ArgumentException>(() => client.Workflow("X", "")).ParamName);

        Assert.Null(handler.LastRequest);

        // The keyed route is still what a key does produce.
        handler.ResponseBody = """{"message":"ok"}""";
        await client.VirtualObject("X", "k").Call("Greet", ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal("/restate/call/X/k/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CallAndSend_NullOrEmptyHandler_ThrowWithoutSending(string? handlerName)
    {
        var (client, handler) = CreateClient();
        var handle = client.VirtualObject("Counter", "my-key");
        var expected = handlerName is null ? typeof(ArgumentNullException) : typeof(ArgumentException);

        async Task Rejects(Func<Task> invoke)
        {
            // Every overload reports the caller's own parameter, before a path is built.
            var ex = await Assert.ThrowsAnyAsync<ArgumentException>(invoke);
            Assert.Equal(expected, ex.GetType());
            Assert.Equal("handler", ex.ParamName);
        }

        await Rejects(() => handle.Call<GreetResponse>(handlerName!, new GreetRequest("Ada")));
        await Rejects(() => handle.Call<GreetResponse>(handlerName!, new GreetRequest("Ada"), default(CallOptions)));
        await Rejects(() => handle.Call(handlerName!, new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, ClientTestJsonContext.Default.GreetResponse));
        await Rejects(() => handle.Call(handlerName!, new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, ClientTestJsonContext.Default.GreetResponse,
            default(CallOptions)));
        await Rejects(() => handle.Call(handlerName!, ClientTestJsonContext.Default.GreetResponse));
        await Rejects(() => handle.Call(handlerName!, ClientTestJsonContext.Default.GreetResponse,
            default(CallOptions)));

        await Rejects(() => handle.Send(handlerName!, new GreetRequest("Ada")));
        await Rejects(() => handle.Send(handlerName!, new GreetRequest("Ada"), default(SendOptions)));
        await Rejects(() => handle.Send(handlerName!, new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest));
        await Rejects(() => handle.Send(handlerName!, new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, default(SendOptions)));

        Assert.Null(handler.LastRequest);
    }

    // ── Ingress error responses ──

    [Fact]
    public async Task ErrorResponse_WithSourceHeaderAndJsonFields_PopulatesEveryProperty()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        handler.ResponseHeaders["x-restate-error-source"] = "ingress";
        handler.ResponseBody = """{"message":"service not found","source":"ingress","code":"RT0003"}""";

        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal("ingress", ex.ErrorSource);
        Assert.Equal("RT0003", ex.ErrorCode);
        Assert.Equal("service not found", ex.Message);
    }

    [Fact]
    public async Task ErrorResponse_SourceOnlyInBody_IsStillReported()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        handler.ResponseBody = """{"message":"handler failed","source":"invocation","code":500}""";

        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse));

        Assert.Equal("invocation", ex.ErrorSource);
        // A code sent as a JSON number is carried as its text rather than losing the whole body.
        Assert.Equal("500", ex.ErrorCode);
        Assert.Equal("handler failed", ex.Message);
    }

    [Fact]
    public async Task ErrorResponse_PreSourceServer_HasNullSourceAndCode()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        // restate-server before 1.7.4: no header, no source/code fields.
        handler.ResponseBody = """{"message":"boom"}""";

        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Null(ex.ErrorSource);
        Assert.Null(ex.ErrorCode);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task ErrorResponse_NonJsonBody_UsesBodyAsMessage()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        handler.ResponseContentType = "text/plain";
        handler.ResponseBody = "upstream exploded";

        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse));

        Assert.Equal("upstream exploded", ex.Message);
        Assert.Null(ex.ErrorSource);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public async Task ErrorResponse_TruncatedJsonBody_UsesBodyAsMessage()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        handler.ResponseBody = """{"message":"boom","sou""";

        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse));

        // The parse failure must not surface: the raw body is the best message available.
        Assert.Equal("""{"message":"boom","sou""", ex.Message);
        Assert.Null(ex.ErrorSource);
        Assert.Null(ex.ErrorCode);
    }

    [Fact]
    public async Task ErrorResponse_EmptyBody_DescribesTheStatusCode()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.ServiceUnavailable;
        handler.ResponseBody = "";

        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Contains("503", ex.Message);
    }

    [Fact]
    public async Task SuccessResponse_ReturnsResultWithoutThrowing()
    {
        var (client, handler) = CreateClient();
        handler.ResponseHeaders["x-restate-error-source"] = "ingress";
        handler.ResponseBody = """{"message":"hello Ada"}""";

        var response = await client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal("hello Ada", response.Message);
    }

    [Fact]
    public async Task ErrorResponse_EveryClientEntryPoint_ThrowsTypedException()
    {
        var (client, handler) = CreateAdminCapableClient();
        handler.ResponseStatusCode = HttpStatusCode.ServiceUnavailable;
        handler.ResponseHeaders["x-restate-error-source"] = "ingress";
        handler.ResponseBody = """{"message":"unavailable","code":"RT0002"}""";

        var calls = new Func<Task>[]
        {
            () => client.Attach<GreetResponse>("inv-1"),
            () => client.Attach("inv-1", ClientTestJsonContext.Default.GreetResponse),
            () => client.GetOutput<GreetResponse>("inv-1"),
            () => client.GetOutput("inv-1", ClientTestJsonContext.Default.GreetResponse),
            // Cancel addresses the admin API rather than the ingress, and reports a failure
            // from it the same way.
            () => client.Cancel("inv-1"),
            () => client.Service("Greeter").Call<GreetResponse>("Greet", new GreetRequest("Ada")),
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse),
            () => client.Service("Greeter").Call("Greet", new GreetRequest("Ada"),
                ClientTestJsonContext.Default.GreetRequest, ClientTestJsonContext.Default.GreetResponse),
            () => client.VirtualObject("Counter", "k").Call<GreetResponse>("Add", new GreetRequest("Ada")),
            () => client.Service("Greeter").Send("Greet", new GreetRequest("Ada")),
            () => client.Service("Greeter").Send("Greet", new GreetRequest("Ada"),
                ClientTestJsonContext.Default.GreetRequest)
        };

        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<RestateIngressException>(call);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
            Assert.Equal("ingress", ex.ErrorSource);
            Assert.Equal("RT0002", ex.ErrorCode);
            Assert.Equal("unavailable", ex.Message);
        }
    }

    // ── Scope and limit key ──

    [Fact]
    public async Task Call_WithScope_UsesVersionedIngressPath()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        await client.Service("Greeter").Call(
            "Greet", ClientTestJsonContext.Default.GreetResponse, CallOptions.WithScope("tenant-a"));

        // Matches sdk-go's makeIngressUrl and the Java client: a scope moves the request to the
        // versioned API, which carries the scope and the verb in the path.
        Assert.Equal("/restate/scope/tenant-a/call/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Call_WithScopeOnVirtualObject_KeepsTheKeyInThePath()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        await client.VirtualObject("Counter", "my-key").Call(
            "Add", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest,
            ClientTestJsonContext.Default.GreetResponse, CallOptions.WithScope("tenant-a", "customer-7"));

        Assert.Equal("/restate/scope/tenant-a/call/Counter/my-key/Add",
            handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("customer-7", handler.LastRequest.Headers.GetValues("x-restate-limit-key").Single());
    }

    [Fact]
    public async Task Call_WithIdempotencyKeyOption_SendsTheHeader()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        await client.Service("Greeter").Call<GreetResponse>(
            "Greet", new GreetRequest("Ada"), CallOptions.WithIdempotencyKey("order-1"));

        Assert.Equal("/restate/call/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("order-1", handler.LastRequest.Headers.GetValues("idempotency-key").Single());
        Assert.False(handler.LastRequest.Headers.Contains("x-restate-limit-key"));
    }

    [Fact]
    public async Task Send_WithScope_UsesVersionedIngressPathWithSendVerb()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-1"}""";

        var invocationId = await client.Service("Greeter").Send(
            "Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest,
            SendOptions.WithScope("tenant-a", "customer-7"));

        // The scoped form carries the verb in the prefix — there is no "/send" suffix.
        Assert.Equal("/restate/scope/tenant-a/send/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("customer-7", handler.LastRequest.Headers.GetValues("x-restate-limit-key").Single());
        Assert.Equal("inv-1", invocationId);
    }

    [Fact]
    public async Task Send_WithoutScope_UsesTheCanonicalSendRoute()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-1"}""";

        await client.Service("Greeter").Send("Greet", new GreetRequest("Ada"),
            new SendOptions { Delay = TimeSpan.FromSeconds(30), IdempotencyKey = "order-1" });

        Assert.Equal("/restate/send/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("?delay=30000ms", handler.LastRequest.RequestUri.Query);
        Assert.Equal("order-1", handler.LastRequest.Headers.GetValues("idempotency-key").Single());
        Assert.False(handler.LastRequest.Headers.Contains("x-restate-limit-key"));
    }

    [Fact]
    public async Task LimitKeyWithoutScope_ThrowsWithoutSending()
    {
        var (client, handler) = CreateClient();

        var call = await Assert.ThrowsAsync<ArgumentException>(() => client.Service("Greeter").Call(
            "Greet", ClientTestJsonContext.Default.GreetResponse, new CallOptions { LimitKey = "customer-7" }));
        Assert.Equal("options", call.ParamName);

        var send = await Assert.ThrowsAsync<ArgumentException>(() => client.Service("Greeter").Send(
            "Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest,
            new SendOptions { LimitKey = "customer-7" }));
        Assert.Equal("options", send.ParamName);

        Assert.Null(handler.LastRequest);
    }

    // ── Target lookup ──

    [Fact]
    public async Task LookupWorkflow_PostsTheWorkflowTargetToTheLookupRoute()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv_1workflow"}""";

        var found = await client.LookupWorkflow("SignupWorkflow", "user-1");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("http://localhost:8080/restate/lookup", handler.LastRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("""{"workflowName":"SignupWorkflow","workflowKey":"user-1","target":"workflow"}""",
            handler.LastRequestBody);
        Assert.Equal(new InvocationHandle("inv_1workflow"), found);
    }

    [Fact]
    public async Task LookupWorkflow_WithScope_CarriesTheScopeInTheBody()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv_1scoped"}""";

        var found = await client.LookupWorkflow("SignupWorkflow", "user-1", "tenant-a");

        // The lookup route takes the scope in the body, not as a path prefix.
        Assert.Equal("/restate/lookup", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(
            """{"workflowName":"SignupWorkflow","workflowKey":"user-1","scope":"tenant-a","target":"workflow"}""",
            handler.LastRequestBody);
        Assert.Equal("inv_1scoped", found.InvocationId);
    }

    [Fact]
    public async Task LookupIdempotentInvocation_PostsTheIdempotentTargetToTheLookupRoute()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv_1idem"}""";

        var found = await client.LookupIdempotentInvocation("Greeter", "Greet", "key-42");

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/restate/lookup", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal(
            """{"service":"Greeter","handler":"Greet","idempotencyKey":"key-42","target":"idempotentInvocation"}""",
            handler.LastRequestBody);
        Assert.Equal("inv_1idem", found.InvocationId);
    }

    [Fact]
    public async Task LookupIdempotentInvocation_WithKeyAndScope_CarriesBothInTheBody()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv_1keyed"}""";

        var found = await client.LookupIdempotentInvocation("Counter", "Add", "key-42", "ada", "tenant-a");

        Assert.Equal(
            """
            {"service":"Counter","handler":"Add","idempotencyKey":"key-42","key":"ada","scope":"tenant-a","target":"idempotentInvocation"}
            """,
            handler.LastRequestBody);
        Assert.Equal("inv_1keyed", found.InvocationId);
    }

    [Fact]
    public async Task Lookup_ErrorStatus_ThrowsTypedIngressException()
    {
        var (client, handler) = CreateClient();

        // The ingress derives the invocation id from the target instead of reading storage, so a
        // target that has never been invoked still answers 200. A failure here means the request
        // itself was rejected — a malformed scope, or a server before 1.7 with no lookup route.
        handler.ResponseStatusCode = HttpStatusCode.NotFound;
        handler.ResponseBody = """{"code":404,"message":"not found"}""";

        var workflow = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.LookupWorkflow("SignupWorkflow", "user-1"));
        Assert.Equal(HttpStatusCode.NotFound, workflow.StatusCode);

        var idempotent = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.LookupIdempotentInvocation("Greeter", "Greet", "key-42"));
        Assert.Equal(HttpStatusCode.NotFound, idempotent.StatusCode);
    }

    [Fact]
    public async Task Lookup_OfATargetWithNoInvocation_ReportsItOnAttach()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv_1never-ran"}""";

        var found = await client.LookupWorkflow("SignupWorkflow", "never-ran");
        Assert.Equal("inv_1never-ran", found.InvocationId);

        handler.ResponseStatusCode = HttpStatusCode.NotFound;
        handler.ResponseBody = """{"code":404,"message":"invocation not found"}""";

        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.Attach(found.InvocationId, ClientTestJsonContext.Default.GreetResponse));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("invocation not found", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"invocationId":""}""")]
    public async Task Lookup_MissingInvocationId_ThrowsJsonException(string responseBody)
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = responseBody;

        await Assert.ThrowsAsync<JsonException>(() => client.LookupWorkflow("SignupWorkflow", "user-1"));
    }

    [Fact]
    public async Task Lookup_NullOrEmptyArguments_ThrowWithoutSending()
    {
        var (client, handler) = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.LookupWorkflow(null!, "user-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.LookupWorkflow("SignupWorkflow", ""));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.LookupIdempotentInvocation("Greeter", null!, "key-42"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.LookupIdempotentInvocation("Greeter", "Greet", ""));

        Assert.Null(handler.LastRequest);
    }

    // ── Scope-bound client ──

    [Fact]
    public async Task WithScope_CallAndSend_UseTheScopedRoute()
    {
        var (client, handler) = CreateClient();
        var scoped = client.WithScope("tenant-a");

        handler.ResponseBody = """{"message":"hello"}""";
        await scoped.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal("/restate/scope/tenant-a/call/Greeter/Greet",
            handler.LastRequest!.RequestUri!.AbsolutePath);

        handler.ResponseBody = """{"invocationId":"inv-1"}""";
        await scoped.VirtualObject("Counter", "ada").Send("Add", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest);
        Assert.Equal("/restate/scope/tenant-a/send/Counter/ada/Add",
            handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task WithScope_PerCallScope_OverridesTheBoundScope()
    {
        var (client, handler) = CreateClient();
        var scoped = client.WithScope("tenant-a");

        handler.ResponseBody = """{"message":"hello"}""";
        await scoped.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse,
            CallOptions.WithScope("tenant-b"));
        Assert.Equal("/restate/scope/tenant-b/call/Greeter/Greet",
            handler.LastRequest!.RequestUri!.AbsolutePath);

        handler.ResponseBody = """{"invocationId":"inv-1"}""";
        await scoped.Service("Greeter").Send("Greet", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, SendOptions.WithScope("tenant-b"));
        Assert.Equal("/restate/scope/tenant-b/send/Greeter/Greet",
            handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task WithScope_Lookups_CarryTheBoundScope_UnlessOneIsPassed()
    {
        var (client, handler) = CreateClient();
        var scoped = client.WithScope("tenant-a");
        handler.ResponseBody = """{"invocationId":"inv_1scoped"}""";

        await scoped.LookupWorkflow("SignupWorkflow", "user-1");
        Assert.Equal(
            """{"workflowName":"SignupWorkflow","workflowKey":"user-1","scope":"tenant-a","target":"workflow"}""",
            handler.LastRequestBody);

        await scoped.LookupIdempotentInvocation("Greeter", "Greet", "key-42");
        Assert.Equal(
            """
            {"service":"Greeter","handler":"Greet","idempotencyKey":"key-42","scope":"tenant-a","target":"idempotentInvocation"}
            """,
            handler.LastRequestBody);

        await scoped.LookupWorkflow("SignupWorkflow", "user-1", "tenant-b");
        Assert.Equal(
            """{"workflowName":"SignupWorkflow","workflowKey":"user-1","scope":"tenant-b","target":"workflow"}""",
            handler.LastRequestBody);
    }

    [Fact]
    public async Task WithScope_LimitKeyWithoutAPerCallScope_NarrowsTheBoundScope()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        // The bound scope is the scope the limit key needs, so the rule that rejects a limit key
        // without one is satisfied without repeating the scope on every call.
        await client.WithScope("tenant-a").Service("Greeter").Call(
            "Greet", ClientTestJsonContext.Default.GreetResponse, new CallOptions { LimitKey = "customer-7" });

        Assert.Equal("/restate/scope/tenant-a/call/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("customer-7", handler.LastRequest.Headers.GetValues("x-restate-limit-key").Single());
    }

    [Fact]
    public async Task WithScope_LeavesTheClientItCameFromUnscoped()
    {
        var (client, handler) = CreateClient();
        _ = client.WithScope("tenant-a");

        handler.ResponseBody = """{"message":"hello"}""";
        await client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal("/restate/call/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);

        handler.ResponseBody = """{"invocationId":"inv_1unscoped"}""";
        await client.LookupWorkflow("SignupWorkflow", "user-1");
        Assert.Equal("""{"workflowName":"SignupWorkflow","workflowKey":"user-1","target":"workflow"}""",
            handler.LastRequestBody);
    }

    [Fact]
    public async Task WithScope_DisposingTheDerivedClient_LeavesTheOriginalUsable()
    {
        var handler = new StubHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new RestateClient(http);

        client.WithScope("tenant-a").Dispose();

        // The derived client shares the HttpClient without owning it, so both are still live.
        handler.ResponseBody = """{"message":"hello"}""";
        await client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal("/restate/call/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);

        using var direct = await http.GetAsync(new Uri("http://localhost:8080/restate/health"));
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
    }

    [Fact]
    public async Task WithScope_CarriesTheAdminAddress_SoTheDerivedClientCanCancel()
    {
        var (client, handler) = CreateAdminCapableClient();

        // Binding a scope keeps the rest of the client configuration, so cancellation — which
        // addresses the admin API rather than the ingress, and knows no scope — still reaches it.
        await client.WithScope("tenant-a").Cancel("inv-7");

        Assert.Equal(HttpMethod.Patch, handler.LastRequest!.Method);
        Assert.Equal("http://localhost:9070/invocations/inv-7/cancel", handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public void WithScope_NullOrEmptyScope_Throws()
    {
        var (client, _) = CreateClient();

        Assert.Throws<ArgumentNullException>(() => client.WithScope(null!));
        Assert.Throws<ArgumentException>(() => client.WithScope(""));
    }

    // ── Signals ──

    [Fact]
    public async Task ResolveSignal_TypeInfo_PostsTheValueToTheSignalRoute()
    {
        var (client, handler) = CreateClient();

        await client.ResolveSignal("sign_1abc", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest);

        // The ingress completes a signal by id under /restate/awakeables, which serves both
        // signal ids and legacy awakeable ids.
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/restate/awakeables/sign_1abc/resolve", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("""{"name":"Ada"}""", handler.LastRequestBody);
    }

    [Fact]
    public async Task ResolveSignal_Reflection_PostsCamelCasedValue()
    {
        var (client, handler) = CreateClient();

        await client.ResolveSignal("sign_1abc", new GreetRequest("Ada"));

        Assert.Equal("/restate/awakeables/sign_1abc/resolve", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("""{"name":"Ada"}""", handler.LastRequestBody);
    }

    [Fact]
    public async Task RejectSignal_PostsTheReasonAsTheRawBody()
    {
        var (client, handler) = CreateClient();

        await client.RejectSignal("sign_1abc", "not approved");

        Assert.Equal("/restate/awakeables/sign_1abc/reject", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("not approved", handler.LastRequestBody);
        Assert.Equal("text/plain", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SignalCompletion_ErrorStatus_Throws()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.NotFound;

        var ex = await Assert.ThrowsAsync<RestateIngressException>(
            () => client.ResolveSignal("sign_1abc", "value", ClientTestJsonContext.Default.String));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task SignalCompletion_NullOrEmptyArguments_ThrowWithoutSending()
    {
        var (client, handler) = CreateClient();

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ResolveSignal(null!, "v"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ResolveSignal("", "v"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.RejectSignal("", "reason"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.RejectSignal("sign_1abc", null!));

        Assert.Null(handler.LastRequest);
    }

    // ── Canonical ingress routes and path escaping ──

    // A segment carrying every character that is special in a URL path or query. Escaped once,
    // it must stay inside a single segment instead of opening a new one or starting a query.
    private const string Reserved = "a/b c?d#e%f";

    private const string ReservedEscaped = "a%2Fb%20c%3Fd%23e%25f";

    [Fact]
    public async Task Call_Unscoped_UsesTheCanonicalCallRoute()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        await client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal("http://localhost:8080/restate/call/Greeter/Greet",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.VirtualObject("Counter", "my-key").Call("Add", ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal("http://localhost:8080/restate/call/Counter/my-key/Add",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Send_Unscoped_UsesTheCanonicalSendRoute_WithAndWithoutDelay()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-1"}""";

        await client.Service("Greeter").Send("Greet", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest);
        Assert.Equal("http://localhost:8080/restate/send/Greeter/Greet",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.VirtualObject("Counter", "my-key").Send("Add", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest);
        Assert.Equal("http://localhost:8080/restate/send/Counter/my-key/Add",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.Service("Greeter").Send("Greet", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, delay: TimeSpan.FromMilliseconds(1500));
        Assert.Equal("http://localhost:8080/restate/send/Greeter/Greet?delay=1500ms",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.VirtualObject("Counter", "my-key").Send("Add", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, delay: TimeSpan.FromSeconds(30));
        Assert.Equal("http://localhost:8080/restate/send/Counter/my-key/Add?delay=30000ms",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Scoped_CallAndSend_KeepTheScopedRouteShape()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        await client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse,
            CallOptions.WithScope("tenant-a"));
        Assert.Equal("http://localhost:8080/restate/scope/tenant-a/call/Greeter/Greet",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.VirtualObject("Counter", "my-key").Call("Add", ClientTestJsonContext.Default.GreetResponse,
            CallOptions.WithScope("tenant-a"));
        Assert.Equal("http://localhost:8080/restate/scope/tenant-a/call/Counter/my-key/Add",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        handler.ResponseBody = """{"invocationId":"inv-1"}""";

        await client.Service("Greeter").Send("Greet", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, SendOptions.WithScope("tenant-a"));
        Assert.Equal("http://localhost:8080/restate/scope/tenant-a/send/Greeter/Greet",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.VirtualObject("Counter", "my-key").Send("Add", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest,
            new SendOptions { Scope = "tenant-a", Delay = TimeSpan.FromSeconds(5) });
        Assert.Equal("http://localhost:8080/restate/scope/tenant-a/send/Counter/my-key/Add?delay=5000ms",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task InvocationRoutes_AttachAndOutputAreCanonical_CancelGoesToTheAdminApi()
    {
        var (client, handler) = CreateAdminCapableClient();
        handler.ResponseBody = """{"message":"done"}""";

        await client.Attach("inv-9", ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal("http://localhost:8080/restate/attach/inv-9", handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.Attach<GreetResponse>("inv-9");
        Assert.Equal("http://localhost:8080/restate/attach/inv-9", handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.GetOutput("inv-9", ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal("http://localhost:8080/restate/output/inv-9", handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.GetOutput<GreetResponse>("inv-9");
        Assert.Equal("http://localhost:8080/restate/output/inv-9", handler.LastRequest!.RequestUri!.AbsoluteUri);

        // The ingress serves no cancel verb, so cancel addresses the admin API instead — on its
        // own base address, leaving the ingress one untouched.
        await client.Cancel("inv-9");
        Assert.Equal("http://localhost:9070/invocations/inv-9/cancel", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ReservedCharactersInCallAndSendSegments_StayInsideTheirSegment()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        await client.VirtualObject("Counter", Reserved).Call("Add", ClientTestJsonContext.Default.GreetResponse);

        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal($"http://localhost:8080/restate/call/Counter/{ReservedEscaped}/Add", uri.AbsoluteUri);
        // "/restate/call/Counter/{key}/Add": no segment added by the key, and no query started.
        Assert.Equal(6, uri.Segments.Length);
        Assert.Equal("", uri.Query);
        Assert.Contains("%25", uri.AbsoluteUri);

        await client.Service(Reserved).Call(Reserved, ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal($"http://localhost:8080/restate/call/{ReservedEscaped}/{ReservedEscaped}",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal(5, handler.LastRequest.RequestUri.Segments.Length);

        handler.ResponseBody = """{"invocationId":"inv-1"}""";

        await client.VirtualObject("Counter", Reserved).Send("Add", new GreetRequest("Ada"),
            ClientTestJsonContext.Default.GreetRequest, delay: TimeSpan.FromSeconds(2));
        Assert.Equal($"http://localhost:8080/restate/send/Counter/{ReservedEscaped}/Add?delay=2000ms",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal("?delay=2000ms", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task ReservedCharactersInTheScope_StayInsideTheScopeSegment()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        await client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse,
            CallOptions.WithScope(Reserved));

        var uri = handler.LastRequest!.RequestUri!;
        Assert.Equal($"http://localhost:8080/restate/scope/{ReservedEscaped}/call/Greeter/Greet", uri.AbsoluteUri);
        Assert.Equal(7, uri.Segments.Length);
        Assert.Equal("", uri.Query);
    }

    [Fact]
    public async Task ReservedCharactersInInvocationIds_StayInsideTheirSegment()
    {
        var (client, handler) = CreateAdminCapableClient();
        handler.ResponseBody = """{"message":"done"}""";

        await client.Attach(Reserved, ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal($"http://localhost:8080/restate/attach/{ReservedEscaped}",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        Assert.Equal(4, handler.LastRequest.RequestUri.Segments.Length);

        await client.GetOutput(Reserved, ClientTestJsonContext.Default.GreetResponse);
        Assert.Equal($"http://localhost:8080/restate/output/{ReservedEscaped}",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.Cancel(Reserved);
        Assert.Equal($"http://localhost:9070/invocations/{ReservedEscaped}/cancel",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
        // "/invocations/{id}/cancel": the id opened no segment of its own and started no query.
        Assert.Equal(4, handler.LastRequest.RequestUri.Segments.Length);
        Assert.Equal("", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task SignalIds_ArePutInThePathVerbatim()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = "{}";

        // The ingress takes the awakeable segment as-is and never url-decodes it, and ids minted
        // by other SDKs end in base64 padding, so a "=" must reach the server unescaped: "%3D"
        // would address an id that does not exist.
        const string signalId = "sign_1PePOqp8nZM3KtGH8kSJqwJzpGn2B1pQ4=";

        await client.ResolveSignal(signalId, "value", ClientTestJsonContext.Default.String);
        Assert.Equal($"http://localhost:8080/restate/awakeables/{signalId}/resolve",
            handler.LastRequest!.RequestUri!.AbsoluteUri);

        await client.RejectSignal(signalId, "nope");
        Assert.Equal($"http://localhost:8080/restate/awakeables/{signalId}/reject",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task OrdinaryNames_AreNotRewrittenByEscaping()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"hello"}""";

        await client.VirtualObject("Counter_Object.v2", "key-1.2_3~4").Call(
            "Add-Item.v2", ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal("http://localhost:8080/restate/call/Counter_Object.v2/key-1.2_3~4/Add-Item.v2",
            handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    internal sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public TrackingStringContent? LastResponseContent { get; private set; }
        public string ResponseBody { get; set; } = "{}";
        public string ResponseContentType { get; set; } = "application/json";
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;
        public Dictionary<string, string> ResponseHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastResponseContent = new TrackingStringContent(ResponseBody, ResponseContentType);
            var response = new HttpResponseMessage(ResponseStatusCode) { Content = LastResponseContent };
            foreach (var (name, value) in ResponseHeaders)
                response.Headers.Add(name, value);
            return response;
        }
    }

    internal sealed class TrackingStringContent(string content, string mediaType = "application/json")
        : StringContent(content, Encoding.UTF8, mediaType)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
