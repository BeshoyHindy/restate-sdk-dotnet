using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Microsoft.Extensions.Logging;
using Restate.Sdk.Hosting;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Discovery;
using Restate.Sdk.Internal.Identity;
using Restate.Sdk.Internal.Protocol;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace Restate.Sdk;

/// <summary>
///     Base class for Restate Lambda handlers. Subclass and override <see cref="Register" />
///     to bind your services, then reference this as your Lambda function handler.
/// </summary>
/// <example>
///     <code>
/// public class Handler : RestateLambdaHandler
/// {
///     public override void Register()
///     {
///         Bind&lt;GreeterService&gt;();
///     }
/// }
/// </code>
/// </example>
public abstract class RestateLambdaHandler
{
    private static readonly TimeSpan LambdaTimeoutMargin = TimeSpan.FromMilliseconds(500);

    private static readonly string ServerVersion =
        $"restate-sdk-dotnet/{typeof(RestateLambdaHandler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0"}";

    private readonly InvocationHandler _handler;

    private readonly List<Type> _serviceTypes = [];

    private readonly List<string> _identityKeys = [];

    private readonly ServiceRegistry _registry;

    private ILoggerFactory? _loggerFactory;
    private readonly RequestIdentityVerifier? _identityVerifier;

    /// <summary>
    ///     Initializes the Lambda handler, building the service registry from registered services.
    /// </summary>
    protected RestateLambdaHandler()
    {
        Register();
        _registry = ServiceRegistry.FromTypes(_serviceTypes);
        _handler = new InvocationHandler(_loggerFactory);
        _identityVerifier = _identityKeys.Count > 0 ? RequestIdentityVerifier.FromKeys(_identityKeys) : null;
    }

    /// <summary>
    ///     Registers a Restate service type. Call from <see cref="Register" />.
    /// </summary>
    protected void Bind<TService>() where TService : class
    {
        _serviceTypes.Add(typeof(TService));
    }

    /// <summary>
    ///     Configures the logger factory used for SDK logging and <see cref="Context.Logger" />.
    ///     Call from <see cref="Register" />. When not configured, logging is disabled.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when called after handler construction (e.g. from a derived-class constructor
    ///     body, which runs after <see cref="Register" />) — the invocation pipeline has already
    ///     been built and a late factory would be silently ignored.
    /// </exception>
    protected void UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (_handler is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(UseLoggerFactory)} must be called from {nameof(Register)}(); " +
                "the invocation pipeline has already been built.");
        }

        _loggerFactory = loggerFactory;
    }

    /// <summary>
    ///     Configures Restate identity public keys (<c>publickeyv1_&lt;base58&gt;</c>) used to verify
    ///     that incoming requests originate from a trusted Restate instance. Call from
    ///     <see cref="Register" />. When at least one key is configured, every request must carry a
    ///     valid <c>x-restate-signature-scheme: v1</c> signature; requests that fail verification are
    ///     rejected with <c>401 Unauthorized</c>. When no keys are configured, requests are not verified.
    /// </summary>
    /// <param name="keys">The serialized identity keys, as printed by <c>restate-server</c> on startup.</param>
    /// <exception cref="ArgumentNullException"><paramref name="keys" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="keys" /> is empty (which would silently leave verification disabled),
    ///     or a key is malformed (wrong prefix, invalid base58, or wrong decoded length).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when called after handler construction (e.g. from a derived-class constructor
    ///     body, which runs after <see cref="Register" />) — the identity verifier has already
    ///     been built and late keys would be silently ignored, leaving the endpoint unverified.
    /// </exception>
    protected void WithIdentityKeys(params string[] keys)
    {
        // Validate eagerly so malformed keys fail here, not from the base constructor.
        _ = RequestIdentityVerifier.ParseKeys(keys);
        if (_handler is not null)
        {
            throw new InvalidOperationException(
                $"{nameof(WithIdentityKeys)} must be called from {nameof(Register)}(); " +
                "the identity verifier has already been built and late keys would be ignored.");
        }

        _identityKeys.AddRange(keys);
    }

    /// <summary>
    ///     Override this method to register your Restate service types using <see cref="Bind{TService}" />.
    /// </summary>
    public abstract void Register();

    /// <summary>
    ///     Lambda function handler entry point. Processes Restate discovery and invocation requests.
    /// </summary>
    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var path = request.Path ?? "";

        // Identity verification runs before anything else touches the request.
        if (_identityVerifier is not null && !VerifyRequestIdentity(_identityVerifier, request, path))
            return new APIGatewayProxyResponse { StatusCode = 401 };

        if (path.EndsWith("/discover", StringComparison.OrdinalIgnoreCase)) return HandleDiscovery(request);

        return await HandleInvocation(request, context).ConfigureAwait(false);
    }

    /// <summary>
    ///     Verifies the request identity headers against the configured keys.
    ///     The token audience must equal the request path (for example <c>/invoke/Greeter/Greet</c>).
    /// </summary>
    private static bool VerifyRequestIdentity(
        RequestIdentityVerifier verifier, APIGatewayProxyRequest request, string path)
    {
        var scheme = GetHeader(request, RequestIdentityVerifier.SignatureSchemeHeader);
        var token = GetHeader(request, RequestIdentityVerifier.JwtHeader);
        return verifier.Verify(scheme, token, path);
    }

    /// <summary>
    ///     Looks up a header case-insensitively (API Gateway header casing varies), checking both
    ///     the single-value and multi-value maps. Repeated values are ambiguous and yield <see langword="null" />.
    ///     <see cref="APIGatewayProxyRequest.MultiValueHeaders" /> is consulted first: API Gateway
    ///     REST APIs populate both maps, and <see cref="APIGatewayProxyRequest.Headers" /> keeps only
    ///     the last value of a repeated header, which would mask the ambiguity.
    /// </summary>
    private static string? GetHeader(APIGatewayProxyRequest request, string name)
    {
        if (request.MultiValueHeaders is not null)
            foreach (var header in request.MultiValueHeaders)
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                    return header.Value is [var single] ? single : null;

        if (request.Headers is not null)
            foreach (var header in request.Headers)
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                    return header.Value;

        return null;
    }

    private APIGatewayProxyResponse HandleDiscovery(APIGatewayProxyRequest request)
    {
        // Mirror the ASP.NET endpoint's manifest version negotiation via the Accept header.
        // API Gateway does not normalize header casing, so scan case-insensitively.
        var acceptHeader = GetHeader(request.Headers, "accept");
        var selectedContentType = RestateEndpointRouteBuilderExtensions.NegotiateVersion(acceptHeader);

        if (selectedContentType is null)
            return new APIGatewayProxyResponse
            {
                StatusCode = 415,
                Body = $"Unsupported discovery manifest version. Accept header was '{acceptHeader}'"
            };

        var manifest = EndpointManifest.FromRegistry(_registry, "REQUEST_RESPONSE");
        var json = JsonSerializer.Serialize(manifest, DiscoveryJsonContext.Default.EndpointManifest);

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = selectedContentType,
                ["x-restate-server"] = ServerVersion
            },
            Body = json,
            IsBase64Encoded = false
        };
    }

    /// <summary>
    ///     Case-insensitive header lookup. API Gateway proxy events preserve the original
    ///     header casing from the client, so a plain dictionary lookup is not reliable.
    /// </summary>
    private static string? GetHeader(IDictionary<string, string>? headers, string name)
    {
        if (headers is null)
            return null;

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }

        return null;
    }

    private async Task<APIGatewayProxyResponse> HandleInvocation(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var path = request.Path ?? "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Expected path: /invoke/{service}/{handler}
        var invokeIdx = Array.IndexOf(segments, "invoke");
        if (invokeIdx < 0 || invokeIdx + 2 >= segments.Length)
            return new APIGatewayProxyResponse
            {
                StatusCode = 404,
                Body = "Invalid invocation path. Expected /invoke/{service}/{handler}"
            };

        var serviceName = segments[invokeIdx + 1];
        var handlerName = segments[invokeIdx + 2];

        if (!_registry.TryGetService(serviceName, out var service) || service is null)
            return new APIGatewayProxyResponse
            {
                StatusCode = 404,
                Body = $"Service '{serviceName}' not found"
            };

        if (!_registry.TryGetHandler(serviceName, handlerName, out var handlerDef) || handlerDef is null)
            return new APIGatewayProxyResponse
            {
                StatusCode = 404,
                Body = $"Handler '{handlerName}' not found on service '{serviceName}'"
            };

        // Negotiate the service protocol version from the request content type.
        var contentType = GetHeader(request.Headers, "content-type");
        if (!ServiceProtocolVersionExtensions.TryParse(contentType, out var protocolVersion))
            return new APIGatewayProxyResponse
            {
                StatusCode = 415,
                Body = $"Unsupported invocation content type '{contentType}'. " +
                       $"Supported: {ServiceProtocolVersionExtensions.SupportedContentTypes}"
            };

        // Decode the binary protocol body
        byte[] requestBody;
        if (request.IsBase64Encoded && request.Body is not null)
            requestBody = Convert.FromBase64String(request.Body);
        else if (request.Body is not null)
            requestBody = Encoding.UTF8.GetBytes(request.Body);
        else
            requestBody = [];

        // Create a CancellationToken from the Lambda remaining time so the handler
        // aborts gracefully before Lambda hard-kills the process.
        var remaining = context.RemainingTime;
        using var cts = remaining > LambdaTimeoutMargin
            ? new CancellationTokenSource(remaining - LambdaTimeoutMargin)
            : new CancellationTokenSource();

        // Process via in-memory pipes — use try/finally to ensure pipes are completed.
        var requestPipe = new Pipe();
        var responsePipe = new Pipe();

        try
        {
            // Write request body into the request pipe
            await requestPipe.Writer.WriteAsync(requestBody, cts.Token).ConfigureAwait(false);
            await requestPipe.Writer.CompleteAsync().ConfigureAwait(false);

            // Run the invocation handler
            var services = new MinimalServiceProvider();
            await _handler.HandleAsync(
                requestPipe.Reader,
                responsePipe.Writer,
                service,
                handlerDef,
                services,
                protocolVersion,
                cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defensive: HandleAsync terminates the protocol body itself for every expected
            // failure. Anything escaping is unexpected — surface a retryable function error
            // instead of returning a half-written (non-terminal) protocol body as 200.
            context.Logger.LogError($"Restate invocation processing failed unexpectedly: {ex}");
            return new APIGatewayProxyResponse { StatusCode = 500 };
        }
        finally
        {
            await requestPipe.Reader.CompleteAsync().ConfigureAwait(false);
            await responsePipe.Writer.CompleteAsync().ConfigureAwait(false);
        }

        // Read the response
        var responseBody = await ReadAllAsync(responsePipe.Reader).ConfigureAwait(false);
        await responsePipe.Reader.CompleteAsync().ConfigureAwait(false);

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Headers = new Dictionary<string, string>
            {
                // Echo the negotiated protocol version back to the runtime.
                ["content-type"] = protocolVersion.ToContentType(),
                ["x-restate-server"] = ServerVersion
            },
            Body = Convert.ToBase64String(responseBody),
            IsBase64Encoded = true
        };
    }

    private static async Task<byte[]> ReadAllAsync(PipeReader reader)
    {
        while (true)
        {
            var result = await reader.ReadAsync().ConfigureAwait(false);
            if (result.IsCompleted)
            {
                var bytes = result.Buffer.ToArray();
                reader.AdvanceTo(result.Buffer.End);
                return bytes;
            }

            reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }
    }

    /// <summary>
    ///     Minimal service provider for Lambda invocations (no DI container).
    ///     Creates service instances using parameterless constructors.
    /// </summary>
    [UnconditionalSuppressMessage("AOT", "IL2026:RequiresUnreferencedCode",
        Justification =
            "Lambda handler creates instances of user-registered service types with [DynamicallyAccessedMembers].")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification =
            "Lambda handler creates instances of user-registered service types with [DynamicallyAccessedMembers].")]
    private sealed class MinimalServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return Activator.CreateInstance(serviceType);
        }
    }
}