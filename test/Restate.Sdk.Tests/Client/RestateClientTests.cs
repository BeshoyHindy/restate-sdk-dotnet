using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Restate.Sdk.Client;

namespace Restate.Sdk.Tests.Client;

internal sealed record GreetRequest(string Name);

internal sealed record GreetResponse(string Message);

[JsonSerializable(typeof(GreetRequest))]
[JsonSerializable(typeof(GreetResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ClientTestJsonContext : JsonSerializerContext;

public class RestateClientTests
{
    private static (RestateClient Client, StubHandler Handler) CreateClient(RestateClientOptions? options = null)
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = options is null ? new RestateClient(http) : new RestateClient(http, options);
        return (client, handler);
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
        Assert.Equal("/Greeter/Greet", handler.LastRequest.RequestUri!.AbsolutePath);
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

        Assert.Equal("/Counter/my-key/Add", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Call_TypeInfo_NoRequest_PostsEmptyBody()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"empty"}""";

        var response = await client.Workflow("Signup", "wf-1").Call(
            "Status", ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal("/Signup/wf-1/Status", handler.LastRequest!.RequestUri!.AbsolutePath);
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
        Assert.Equal("/Greeter/Greet/send", handler.LastRequest!.RequestUri!.AbsolutePath);
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

        Assert.Equal("/Greeter/Greet/send", handler.LastRequest!.RequestUri!.AbsolutePath);
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

        Assert.Equal("/Greeter/ada/Greet/send", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("key-42", Assert.Single(handler.LastRequest!.Headers.GetValues("idempotency-key")));
    }

    [Fact]
    public async Task Attach_TypeInfo_GetsAttachPath()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"attached"}""";

        var response = await client.Attach("inv-9", ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/restate/invocation/inv-9/attach", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Equal("attached", response.Message);
    }

    [Fact]
    public async Task GetOutput_TypeInfo_GetsOutputPath()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"message":"done"}""";

        var response = await client.GetOutput("inv-9", ClientTestJsonContext.Default.GreetResponse);

        Assert.Equal("/restate/invocation/inv-9/output", handler.LastRequest!.RequestUri!.AbsolutePath);
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
    public async Task Call_TypeInfo_ErrorStatus_ThrowsHttpRequestException()
    {
        var (client, handler) = CreateClient();
        handler.ResponseStatusCode = HttpStatusCode.InternalServerError;
        handler.ResponseBody = """{"message":"boom"}""";

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.Service("Greeter").Call("Greet", ClientTestJsonContext.Default.GreetResponse));
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

        Assert.Equal("/Greeter/Greet", handler.LastRequest!.RequestUri!.AbsolutePath);
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
        Assert.Equal("/Greeter/Greet/send", handler.LastRequest!.RequestUri!.AbsolutePath);
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
    public async Task Cancel_DeletesInvocation()
    {
        var (client, handler) = CreateClient();

        await client.Cancel("inv-5");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("/restate/invocation/inv-5", handler.LastRequest.RequestUri!.AbsolutePath);
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

        await client.Cancel("inv-cancel");
        Assert.True(handler.LastResponseContent!.IsDisposed);
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
    public async Task InvocationOperations_NullOrEmptyInvocationId_ThrowWithoutSending()
    {
        var (client, handler) = CreateClient();

        // A null/empty id would silently build "/restate/invocation//..." and surface as an
        // opaque 404 from the server — it must fail fast at the call site instead.
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.Attach<GreetResponse>(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Attach<GreetResponse>(""));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Attach("", ClientTestJsonContext.Default.GreetResponse));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetOutput<GreetResponse>(""));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetOutput("", ClientTestJsonContext.Default.GreetResponse));
        await Assert.ThrowsAsync<ArgumentException>(() => client.Cancel(""));

        Assert.Null(handler.LastRequest);
    }

    internal sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public TrackingStringContent? LastResponseContent { get; private set; }
        public string ResponseBody { get; set; } = "{}";
        public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastResponseContent = new TrackingStringContent(ResponseBody);
            return new HttpResponseMessage(ResponseStatusCode)
            {
                Content = LastResponseContent
            };
        }
    }

    internal sealed class TrackingStringContent(string content)
        : StringContent(content, Encoding.UTF8, "application/json")
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
