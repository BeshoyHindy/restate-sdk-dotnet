using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Discovery;

namespace Restate.Sdk.Hosting;

/// <summary>
///     Extension methods for mapping Restate endpoints on an <see cref="IEndpointRouteBuilder" />.
/// </summary>
public static class RestateEndpointRouteBuilderExtensions
{
    private static readonly string ServerVersion =
        $"restate-sdk-dotnet/{typeof(RestateEndpointRouteBuilderExtensions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0"}";

    // Supported manifest versions, highest priority first.
    // v4 adds lambda compression fields we don't support yet, so we cap at v3.
    private static readonly string[] SupportedContentTypes =
    [
        "application/vnd.restate.endpointmanifest.v3+json",
        "application/vnd.restate.endpointmanifest.v2+json",
        "application/vnd.restate.endpointmanifest.v1+json",
    ];

    internal static string? NegotiateVersion(string? acceptHeader)
    {
        if (string.IsNullOrEmpty(acceptHeader))
            return SupportedContentTypes[^1]; // Default to v1 when no Accept header (like Java)

        // Accept: */* or no specific manifest type → default to v1
        if (acceptHeader.Contains("*/*", StringComparison.Ordinal))
            return SupportedContentTypes[^1];

        // Find the highest version we support that the client also supports
        foreach (var supported in SupportedContentTypes)
        {
            if (acceptHeader.Contains(supported, StringComparison.OrdinalIgnoreCase))
                return supported;
        }

        return null; // No mutually supported version → 415
    }

    /// <summary>
    ///     Maps Restate discovery and invocation endpoints.
    ///     Requires prior registration via <see cref="RestateServiceCollectionExtensions.AddRestate" />.
    /// </summary>
    /// <example>
    ///     <code>
    /// app.MapRestate();
    /// </code>
    /// </example>
    public static IEndpointRouteBuilder MapRestate(this IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<ServiceRegistry>();
        var handler = endpoints.ServiceProvider.GetRequiredService<InvocationHandler>();

        // Cache the discovery manifest as a byte[] — it never changes after startup.
        // Uses source-generated DiscoveryJsonContext for AOT compatibility.
        var cachedManifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            EndpointManifest.FromRegistry(registry), DiscoveryJsonContext.Default.EndpointManifest);

        endpoints.MapGet("/discover", async context =>
        {
            var selectedContentType = NegotiateVersion(context.Request.Headers.Accept.ToString());

            if (selectedContentType is null)
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }

            context.Response.ContentType = selectedContentType;
            context.Response.Headers.Append("x-restate-server", ServerVersion);
            context.Response.ContentLength = cachedManifestBytes.Length;
            await context.Response.Body.WriteAsync(cachedManifestBytes, context.RequestAborted);
        });

        endpoints.MapPost("/invoke/{service}/{handlerName}", async context =>
        {
            var serviceName = context.Request.RouteValues["service"]?.ToString();
            var handlerName = context.Request.RouteValues["handlerName"]?.ToString();

            if (string.IsNullOrEmpty(serviceName) || string.IsNullOrEmpty(handlerName))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (!registry.TryGetService(serviceName, out var service) || service is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync($"Service '{serviceName}' not found");
                return;
            }

            if (!registry.TryGetHandler(serviceName, handlerName, out var handlerDef) || handlerDef is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync($"Handler '{handlerName}' not found on service '{serviceName}'");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/vnd.restate.invocation.v6";
            context.Response.Headers.Append("x-restate-server", ServerVersion);

            // Disable response buffering so protocol frames are written directly to the HTTP/2 stream.
            // Without this, middleware or server features may buffer the response body,
            // preventing bidirectional streaming from functioning correctly.
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await context.Response.StartAsync(context.RequestAborted);

            try
            {
                await handler.HandleAsync(
                    context.Request.BodyReader,
                    context.Response.BodyWriter,
                    service,
                    handlerDef,
                    context.RequestServices,
                    context.RequestAborted);
            }
            catch
            {
                // HandleAsync sends End/Error on the protocol stream before throwing.
                // If we're here, something catastrophic happened (e.g. FailAsync itself threw
                // because the pipe is broken). Don't try to write more protocol messages —
                // just let Kestrel close the HTTP/2 stream gracefully.
            }

            await context.Response.CompleteAsync();
        });

        return endpoints;
    }
}