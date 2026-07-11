using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Restate.Sdk.Internal.Identity;
using Restate.Sdk.Tests.Endpoint;

namespace Restate.Sdk.Tests.Identity;

/// <summary>
///     Identity enforcement in <see cref="RestateLambdaHandler.FunctionHandler" />:
///     signed/unsigned API Gateway proxy requests, case-insensitive header lookup,
///     and audience validation against <see cref="APIGatewayProxyRequest.Path" />.
/// </summary>
public class LambdaIdentityTests
{
    private static readonly (string SerializedKey, Ed25519PrivateKeyParameters PrivateKey) KeyPair =
        IdentityTestHelpers.CreateKeyPair();

    private static string CreateToken(string audience)
    {
        return IdentityTestHelpers.CreateJwt(
            KeyPair.PrivateKey, KeyPair.SerializedKey, audience,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), lifetimeSeconds: 300);
    }

    private static APIGatewayProxyRequest CreateSignedRequest(string path, string? audience = null)
    {
        return new APIGatewayProxyRequest
        {
            Path = path,
            Headers = new Dictionary<string, string>
            {
                [RequestIdentityVerifier.SignatureSchemeHeader] = "v1",
                [RequestIdentityVerifier.JwtHeader] = CreateToken(audience ?? path),
            },
        };
    }

    [Fact]
    public async Task Discover_NoKeysConfigured_UnsignedSucceeds()
    {
        var handler = new OpenHandler();

        var response = await handler.FunctionHandler(
            new APIGatewayProxyRequest { Path = "/discover" }, new FakeLambdaContext());

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Discover_KeysConfigured_UnsignedRejectedWithEmptyBody()
    {
        var handler = new SecuredHandler();

        var response = await handler.FunctionHandler(
            new APIGatewayProxyRequest { Path = "/discover" }, new FakeLambdaContext());

        Assert.Equal(401, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(response.Body));
    }

    [Fact]
    public async Task Discover_KeysConfigured_SignedSucceeds()
    {
        var handler = new SecuredHandler();

        var response = await handler.FunctionHandler(
            CreateSignedRequest("/discover"), new FakeLambdaContext());

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("GreeterService", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_KeysConfigured_HeaderLookupIsCaseInsensitive()
    {
        var handler = new SecuredHandler();
        var request = new APIGatewayProxyRequest
        {
            Path = "/discover",
            Headers = new Dictionary<string, string>
            {
                ["X-Restate-Signature-Scheme"] = "v1",
                ["X-Restate-JWT-V1"] = CreateToken("/discover"),
            },
        };

        var response = await handler.FunctionHandler(request, new FakeLambdaContext());

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Discover_KeysConfigured_MultiValueHeadersSucceed()
    {
        var handler = new SecuredHandler();
        var request = new APIGatewayProxyRequest
        {
            Path = "/discover",
            MultiValueHeaders = new Dictionary<string, IList<string>>
            {
                ["X-Restate-Signature-Scheme"] = ["v1"],
                ["X-Restate-JWT-V1"] = [CreateToken("/discover")],
            },
        };

        var response = await handler.FunctionHandler(request, new FakeLambdaContext());

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task Discover_KeysConfigured_RepeatedMultiValueHeaderRejected()
    {
        var handler = new SecuredHandler();
        string token = CreateToken("/discover");
        var request = new APIGatewayProxyRequest
        {
            Path = "/discover",
            MultiValueHeaders = new Dictionary<string, IList<string>>
            {
                ["x-restate-signature-scheme"] = ["v1", "v1"],
                ["x-restate-jwt-v1"] = [token],
            },
        };

        var response = await handler.FunctionHandler(request, new FakeLambdaContext());

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_KeysConfigured_UnsignedRejectedBeforeRouting()
    {
        var handler = new SecuredHandler();

        // Enforcement runs first: even an unknown service yields 401, not 404.
        var response = await handler.FunctionHandler(
            new APIGatewayProxyRequest { Path = "/invoke/Unknown/Handler" }, new FakeLambdaContext());

        Assert.Equal(401, response.StatusCode);
        Assert.True(string.IsNullOrEmpty(response.Body));
    }

    [Fact]
    public async Task Invoke_KeysConfigured_SignedPassesIdentityGate()
    {
        var handler = new SecuredHandler();

        // A signed request reaches routing: the unknown service now yields 404.
        var response = await handler.FunctionHandler(
            CreateSignedRequest("/invoke/Unknown/Handler"), new FakeLambdaContext());

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_KeysConfigured_WrongAudienceRejected()
    {
        var handler = new SecuredHandler();

        var response = await handler.FunctionHandler(
            CreateSignedRequest("/invoke/Unknown/Handler", audience: "/invoke/Other/Path"),
            new FakeLambdaContext());

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_NoKeysConfigured_UnsignedBehavesAsBefore()
    {
        var handler = new OpenHandler();

        var response = await handler.FunctionHandler(
            new APIGatewayProxyRequest { Path = "/invoke/Unknown/Handler" }, new FakeLambdaContext());

        Assert.Equal(404, response.StatusCode);
    }

    private sealed class SecuredHandler : RestateLambdaHandler
    {
        public override void Register()
        {
            Bind<GreeterService>();
            WithIdentityKeys(KeyPair.SerializedKey);
        }
    }

    private sealed class OpenHandler : RestateLambdaHandler
    {
        public override void Register()
        {
            Bind<GreeterService>();
        }
    }

    private sealed class FakeLambdaContext : ILambdaContext
    {
        public string AwsRequestId => "test-request";
        public IClientContext ClientContext => null!;
        public string FunctionName => "test-function";
        public string FunctionVersion => "1";
        public ICognitoIdentity Identity => null!;
        public string InvokedFunctionArn => "arn:aws:lambda:test";
        public ILambdaLogger Logger => null!;
        public string LogGroupName => "test-group";
        public string LogStreamName => "test-stream";
        public int MemoryLimitInMB => 128;
        public TimeSpan RemainingTime => TimeSpan.FromSeconds(30);
    }
}
