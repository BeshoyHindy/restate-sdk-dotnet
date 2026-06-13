using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Discovery;
using Restate.Sdk.Internal.Protocol;

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

    /// <summary>
    ///     Runs request-identity verification. On rejection, writes a 401 response and returns false;
    ///     the caller must then stop processing the request.
    /// </summary>
    private static async ValueTask<bool> VerifyIdentityAsync(HttpContext context, IRequestIdentityVerifier verifier)
    {
        var result = verifier.Verify(
            name =>
            {
                var values = context.Request.Headers[name];
                return values.Count > 0 ? values.ToString() : null;
            },
            context.Request.Path.ToString());

        if (result.IsVerified)
            return true;

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync(result.RejectionReason ?? "Request identity verification failed");
        return false;
    }

    internal static string? NegotiateVersion(string? acceptHeader)
    {
        if (string.IsNullOrEmpty(acceptHeader))
            return SupportedContentTypes[^1]; // Default to v1 when no Accept header (like Java)

        // Accept: */* or no specific manifest type → default to v1
        if (acceptHeader.Contains("*/*", StringComparison.Ordinal))
            return SupportedContentTypes[^1];

        // Check version-specific substrings directly (highest priority first)
        if (acceptHeader.Contains("endpointmanifest.v3", StringComparison.OrdinalIgnoreCase))
            return SupportedContentTypes[0];
        if (acceptHeader.Contains("endpointmanifest.v2", StringComparison.OrdinalIgnoreCase))
            return SupportedContentTypes[1];
        if (acceptHeader.Contains("endpointmanifest.v1", StringComparison.OrdinalIgnoreCase))
            return SupportedContentTypes[2];

        return null; // No mutually supported version → 415
    }

    /// <summary>
    ///     Validates the inbound /invoke request Content-Type and negotiates the service-protocol
    ///     version, mirroring shared-core <c>VM::new</c> (vm/mod.rs:214-261). restate-server signals the
    ///     negotiated version via the request Content-Type (<c>application/vnd.restate.invocation.v{N}</c>).
    ///     Returns:
    ///       * a positive version number when the content type names a version WITHIN [V5,V6];
    ///       * <c>0</c> when a recognizable invocation content type names a version OUTSIDE [V5,V6]
    ///         (RT0015 — the caller rejects 415);
    ///       * <see cref="Internal.Protocol.ProtocolVersion.MaximumSupported" /> when no recognizable
    ///         invocation content type was supplied (e.g. a hand-crafted request) — we fall back to our
    ///         manifest max, exactly as the prior echo behavior did, rather than reject.
    /// </summary>
    internal static int NegotiateInvocationVersion(string? requestContentType)
    {
        if (string.IsNullOrEmpty(requestContentType))
            return ProtocolVersion.MaximumSupported;

        var version = ProtocolVersion.TryParse(requestContentType);
        if (version is null)
            // Not a restate invocation content type at all → fall back to our max (legacy behavior).
            return ProtocolVersion.MaximumSupported;

        // A well-formed invocation content type whose version is outside [V5,V6] is RT0015 → reject.
        return ProtocolVersion.IsSupported(version.Value) ? version.Value : 0;
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
        var identityVerifier = endpoints.ServiceProvider.GetRequiredService<IRequestIdentityVerifier>();
        // G13: the endpoint-wide payload-check policy captured at AddRestate/AddRestateAot. Optional so a
        // host that wired the handler by hand (without the carrier) keeps the safe default.
        var invocationOptions = endpoints.ServiceProvider.GetService<RestateInvocationOptions>()
            ?? RestateInvocationOptions.Default;

        // Cache the discovery manifest as a byte[] — it never changes after startup.
        // Uses source-generated DiscoveryJsonContext for AOT compatibility.
        var cachedManifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            EndpointManifest.FromRegistry(registry), DiscoveryJsonContext.Default.EndpointManifest);

        endpoints.MapGet("/discover", async context =>
        {
            if (!await VerifyIdentityAsync(context, identityVerifier))
                return;

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
            if (!await VerifyIdentityAsync(context, identityVerifier))
                return;

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

            // G12: negotiate + validate the inbound service-protocol version BEFORE starting the 200
            // stream or invoking the handler. A recognizable invocation content type naming a version
            // outside [V5,V6] is rejected with 415 (RT0015), matching shared-core VM::new
            // (vm/mod.rs:214-261); an absent/unrecognized type falls back to our manifest max.
            var negotiatedVersion = NegotiateInvocationVersion(context.Request.ContentType);
            if (negotiatedVersion == 0)
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                await context.Response.WriteAsync(
                    $"Unsupported protocol version '{context.Request.ContentType}', not within " +
                    $"[v{ProtocolVersion.MinimumSupported} to v{ProtocolVersion.MaximumSupported}]. " +
                    "See https://docs.restate.dev/references/errors/#RT0015");
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            // The response protocol version MUST match the version the server negotiated on the
            // request. restate-server sends Content-Type: application/vnd.restate.invocation.v{N}
            // on /invoke and rejects (RT0012) any response whose version differs — so we echo the
            // negotiated version's content-type back.
            context.Response.ContentType = ProtocolVersion.ContentTypeFor(negotiatedVersion);
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
                    context.RequestAborted,
                    negotiatedVersion,
                    invocationOptions.StrictPayloadChecks);
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