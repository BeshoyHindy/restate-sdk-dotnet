using System.Net;
using System.Text;
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
    private static (RestateClient Client, StubHandler Handler) CreateClient()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        return (new RestateClient(http), handler);
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
    public async Task Send_TypeInfo_SetsFireAndForgetHeader_AndReturnsInvocationId()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-123"}""";

        var invocationId = await client.Service("Greeter").Send(
            "Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest);

        Assert.Equal("inv-123", invocationId);
        Assert.Equal("fire-and-forget", Assert.Single(handler.LastRequest!.Headers.GetValues("x-restate-mode")));
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

        Assert.Equal("?delay=2000ms", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task Send_TypeInfo_WithIdempotencyKey_SetsHeader()
    {
        var (client, handler) = CreateClient();
        handler.ResponseBody = """{"invocationId":"inv-1"}""";

        await client.Service("Greeter").Send(
            "Greet", new GreetRequest("Ada"), ClientTestJsonContext.Default.GreetRequest,
            idempotencyKey: "key-42");

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
        Assert.Equal("fire-and-forget", Assert.Single(handler.LastRequest!.Headers.GetValues("x-restate-mode")));
    }

    [Fact]
    public async Task Cancel_DeletesInvocation()
    {
        var (client, handler) = CreateClient();

        await client.Cancel("inv-5");

        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("/restate/invocation/inv-5", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    internal sealed class StubHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public string ResponseBody { get; set; } = "{}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
