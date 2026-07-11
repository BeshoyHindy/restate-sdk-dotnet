using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Org.BouncyCastle.Crypto.Parameters;
using Restate.Sdk.Hosting;
using Restate.Sdk.Internal.Identity;
using Restate.Sdk.Tests.Endpoint;

namespace Restate.Sdk.Tests.Identity;

/// <summary>
///     Endpoint-level identity enforcement: drives the endpoints mapped by
///     <c>MapRestate()</c> through an in-memory TestServer with signed and unsigned requests.
/// </summary>
public class EndpointIdentityTests : IAsyncLifetime
{
    private readonly string _serializedKey;
    private readonly Ed25519PrivateKeyParameters _privateKey;

    private WebApplication? _securedApp;
    private WebApplication? _openApp;
    private HttpClient? _securedClient;
    private HttpClient? _openClient;

    private HttpClient Secured => _securedClient!;
    private HttpClient Open => _openClient!;

    public EndpointIdentityTests()
    {
        (_serializedKey, _privateKey) = IdentityTestHelpers.CreateKeyPair();
    }

    public async Task InitializeAsync()
    {
        (_securedApp, _securedClient) = await StartAppAsync(_serializedKey);
        (_openApp, _openClient) = await StartAppAsync(identityKey: null);
    }

    public async Task DisposeAsync()
    {
        _securedClient?.Dispose();
        _openClient?.Dispose();
        if (_securedApp is not null) await _securedApp.DisposeAsync();
        if (_openApp is not null) await _openApp.DisposeAsync();
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAppAsync(string? identityKey)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRestate(opts =>
        {
            opts.AddService<GreeterService>();
            if (identityKey is not null)
                opts.WithIdentityKeys(identityKey);
        });

        var app = builder.Build();
        app.MapRestate();
        await app.StartAsync();

        return (app, app.GetTestClient());
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, bool signed, string? audience = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (signed)
        {
            string token = IdentityTestHelpers.CreateJwt(
                _privateKey, _serializedKey, audience ?? path,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), lifetimeSeconds: 300);
            request.Headers.Add(RequestIdentityVerifier.SignatureSchemeHeader, "v1");
            request.Headers.Add(RequestIdentityVerifier.JwtHeader, token);
        }

        return request;
    }

    [Fact]
    public async Task Discover_NoKeysConfigured_UnsignedSucceeds()
    {
        var response = await Open.SendAsync(CreateRequest(HttpMethod.Get, "/discover", signed: false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Discover_KeysConfigured_UnsignedRejectedWithEmptyBody()
    {
        var response = await Secured.SendAsync(CreateRequest(HttpMethod.Get, "/discover", signed: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Discover_KeysConfigured_SignedSucceeds()
    {
        var response = await Secured.SendAsync(CreateRequest(HttpMethod.Get, "/discover", signed: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("GreeterService", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_KeysConfigured_UnsignedSchemeRejected()
    {
        var request = CreateRequest(HttpMethod.Get, "/discover", signed: false);
        request.Headers.Add(RequestIdentityVerifier.SignatureSchemeHeader, "unsigned");

        var response = await Secured.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Discover_KeysConfigured_WrongAudienceRejected()
    {
        var request = CreateRequest(HttpMethod.Get, "/discover", signed: true, audience: "/invoke/Greeter/Greet");

        var response = await Secured.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Discover_KeysConfigured_RepeatedSchemeHeaderRejected()
    {
        var request = CreateRequest(HttpMethod.Get, "/discover", signed: true);
        request.Headers.Add(RequestIdentityVerifier.SignatureSchemeHeader, "v1"); // now present twice

        var response = await Secured.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_KeysConfigured_UnsignedRejectedBeforeRouting()
    {
        // Enforcement runs first: even an unknown service yields 401, not 404.
        var response = await Secured.SendAsync(
            CreateRequest(HttpMethod.Post, "/invoke/Unknown/Handler", signed: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Invoke_KeysConfigured_SignedPassesIdentityGate()
    {
        // A signed request reaches routing: the unknown service now yields 404.
        var response = await Secured.SendAsync(
            CreateRequest(HttpMethod.Post, "/invoke/Unknown/Handler", signed: true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_KeysConfigured_WrongAudienceRejected()
    {
        var response = await Secured.SendAsync(
            CreateRequest(HttpMethod.Post, "/invoke/Unknown/Handler", signed: true, audience: "/invoke/Other/Path"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_NoKeysConfigured_UnsignedBehavesAsBefore()
    {
        var response = await Open.SendAsync(
            CreateRequest(HttpMethod.Post, "/invoke/Unknown/Handler", signed: false));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
