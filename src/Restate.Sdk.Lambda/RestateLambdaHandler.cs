using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Discovery;

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

    private readonly ServiceRegistry _registry;

    /// <summary>
    ///     Initializes the Lambda handler, building the service registry from registered services.
    /// </summary>
    protected RestateLambdaHandler()
    {
        Register();
        _registry = ServiceRegistry.FromTypes(_serviceTypes);
        _handler = new InvocationHandler();
    }

    /// <summary>
    ///     Registers a Restate service type. Call from <see cref="Register" />.
    /// </summary>
    protected void Bind<TService>() where TService : class
    {
        _serviceTypes.Add(typeof(TService));
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

        if (path.EndsWith("/discover", StringComparison.OrdinalIgnoreCase)) return HandleDiscovery();

        return await HandleInvocation(request, context).ConfigureAwait(false);
    }

    private APIGatewayProxyResponse HandleDiscovery()
    {
        var manifest = EndpointManifest.FromRegistry(_registry, "REQUEST_RESPONSE");
        var json = JsonSerializer.Serialize(manifest, DiscoveryJsonContext.Default.EndpointManifest);

        // Lambda deployments typically use v1 since the runtime discovers via API Gateway.
        // Version negotiation is not needed here (no Accept header in Lambda proxy events).
        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Headers = new Dictionary<string, string>
            {
                ["content-type"] = "application/vnd.restate.endpointmanifest.v3+json",
                ["x-restate-server"] = ServerVersion
            },
            Body = json,
            IsBase64Encoded = false
        };
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
                cts.Token).ConfigureAwait(false);
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
                ["content-type"] = "application/vnd.restate.invocation.v6",
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