using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Restate.Sdk.Hosting;
using Restate.Sdk.Identity;

namespace Restate.Sdk.Tests.Identity;

/// <summary>
///     A trivial service so the endpoint has something to discover.
/// </summary>
[Service(Name = "IdentityE2EGreeter")]
public class IdentityE2EGreeter
{
    [Handler]
    public Task<string> Greet(Context ctx, string name) => Task.FromResult($"Hello {name}");
}

/// <summary>
///     End-to-end HTTP tests for request-identity verification: a real ASP.NET Core pipeline with
///     <c>MapRestate</c> behind an in-memory <see cref="TestServer" />, exercising the actual
///     <c>/discover</c> and <c>/invoke</c> routes with and without valid Restate signatures.
/// </summary>
public class RequestIdentityE2ETests
{
    private const string DiscoverPath = "/discover";

    private static async Task<IHost> StartHostAsync(string? trustedPublicKey)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
#pragma warning disable IL2026 // AddRestate uses reflection; acceptable in tests.
                        services.AddRestate(options => options.AddService<IdentityE2EGreeter>());
#pragma warning restore IL2026
                        if (trustedPublicKey is not null)
                            services.AddRestateRequestIdentity(trustedPublicKey);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapRestate());
                    });
            })
            .StartAsync();

        return host;
    }

    [Fact]
    public async Task Discover_WithoutIdentityConfigured_AllowsUnsignedRequest()
    {
        using var host = await StartHostAsync(trustedPublicKey: null);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(DiscoverPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Discover_WithValidSignature_IsAccepted()
    {
        var (publicKey, privateKey) = RestateIdentityTestTokens.GenerateKey();
        using var host = await StartHostAsync(publicKey);
        using var client = host.GetTestClient();
        var jwt = RestateIdentityTestTokens.MintJwt(privateKey, DiscoverPath);

        var request = new HttpRequestMessage(HttpMethod.Get, DiscoverPath);
        request.Headers.Add("x-restate-signature-scheme", "v1");
        request.Headers.Add("x-restate-jwt-v1", jwt);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Discover_WithoutSignature_IsRejected()
    {
        var (publicKey, _) = RestateIdentityTestTokens.GenerateKey();
        using var host = await StartHostAsync(publicKey);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(DiscoverPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Discover_WithUnsignedScheme_IsRejected()
    {
        var (publicKey, _) = RestateIdentityTestTokens.GenerateKey();
        using var host = await StartHostAsync(publicKey);
        using var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, DiscoverPath);
        request.Headers.Add("x-restate-signature-scheme", "unsigned");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Discover_SignedForDifferentAudience_IsRejected()
    {
        var (publicKey, privateKey) = RestateIdentityTestTokens.GenerateKey();
        using var host = await StartHostAsync(publicKey);
        using var client = host.GetTestClient();
        // Signed for a different path than /discover.
        var jwt = RestateIdentityTestTokens.MintJwt(privateKey, "/invoke/Other/Handler");

        var request = new HttpRequestMessage(HttpMethod.Get, DiscoverPath);
        request.Headers.Add("x-restate-signature-scheme", "v1");
        request.Headers.Add("x-restate-jwt-v1", jwt);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_WithoutSignature_IsRejectedBeforeProcessing()
    {
        var (publicKey, _) = RestateIdentityTestTokens.GenerateKey();
        using var host = await StartHostAsync(publicKey);
        using var client = host.GetTestClient();

        var response = await client.PostAsync("/invoke/IdentityE2EGreeter/Greet",
            new ByteArrayContent([]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
