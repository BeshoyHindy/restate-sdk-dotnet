using System.Net;
using Amazon.Lambda.APIGatewayEvents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Restate.Sdk.Hosting;

namespace Restate.Sdk.Tests.Endpoint;

/// <summary>
///     The 404 and 415 bodies echo request input (service and handler names from the path, the
///     Accept or Content-Type header). They are declared as plain text with <c>nosniff</c> so a
///     client never interprets that echoed input as markup.
/// </summary>
public class EndpointErrorResponseTests : IAsyncLifetime
{
    private const string PlainText = "text/plain; charset=utf-8";

    private WebApplication? _app;
    private HttpClient? _client;

    private HttpClient Client => _client!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRestate(opts => opts.AddService<GreeterService>());

        _app = builder.Build();
        _app.MapRestate();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("/invoke/<img src=x onerror=alert(1)>/Handler")]
    [InlineData("/invoke/GreeterService/<img src=x onerror=alert(1)>")]
    public async Task Invoke_UnknownTarget_ReturnsPlainTextNotFound(string path)
    {
        using var response = await Client.PostAsync(path, new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertPlainText(response);
    }

    [Fact]
    public async Task Invoke_UnsupportedContentType_ReturnsPlainText()
    {
        using var content = new ByteArrayContent([]);
        content.Headers.TryAddWithoutValidation("Content-Type", "text/html");
        using var response = await Client.PostAsync("/invoke/GreeterService/Greet", content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        AssertPlainText(response);
    }

    [Theory]
    [InlineData("/invoke/Unknown/Handler", null)]
    [InlineData("/invoke/GreeterService/Unknown", null)]
    [InlineData("/invoke", null)]
    [InlineData("/invoke/GreeterService/Greet", "text/html")]
    public async Task Lambda_ErrorResponses_ArePlainText(string path, string? contentType)
    {
        var request = new APIGatewayProxyRequest
        {
            Path = path,
            Headers = contentType is null ? null : new Dictionary<string, string> { ["content-type"] = contentType },
        };

        var response = await new Handler().FunctionHandler(request, null!);

        Assert.InRange(response.StatusCode, 404, 415);
        Assert.Equal(PlainText, response.Headers["content-type"]);
        Assert.Equal("nosniff", response.Headers["x-content-type-options"]);
    }

    [Fact]
    public async Task Lambda_UnsupportedDiscoveryVersion_IsPlainText()
    {
        var request = new APIGatewayProxyRequest
        {
            Path = "/discover",
            Headers = new Dictionary<string, string> { ["accept"] = "text/html" },
        };

        var response = await new Handler().FunctionHandler(request, null!);

        Assert.Equal(415, response.StatusCode);
        Assert.Equal(PlainText, response.Headers["content-type"]);
        Assert.Equal("nosniff", response.Headers["x-content-type-options"]);
    }

    private static void AssertPlainText(HttpResponseMessage response)
    {
        Assert.Equal(PlainText, response.Content.Headers.ContentType?.ToString());
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    private sealed class Handler : RestateLambdaHandler
    {
        public override void Register() => Bind<GreeterService>();
    }
}
