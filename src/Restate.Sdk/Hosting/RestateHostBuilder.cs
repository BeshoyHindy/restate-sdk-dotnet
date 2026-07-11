using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Restate.Sdk.Internal.Identity;

namespace Restate.Sdk.Hosting;

/// <summary>
///     Quick-start entry point for hosting Restate services without manual DI setup.
/// </summary>
public static class RestateHost
{
    /// <summary>Creates a new <see cref="RestateHostBuilder" />.</summary>
    public static RestateHostBuilder CreateBuilder()
    {
        return new RestateHostBuilder();
    }
}

/// <summary>
///     Builds a self-hosted Restate endpoint with Kestrel.
///     For full ASP.NET Core integration, use <see cref="RestateServiceCollectionExtensions.AddRestate" />
///     and <see cref="RestateEndpointRouteBuilderExtensions.MapRestate" /> instead.
/// </summary>
public sealed class RestateHostBuilder
{
    private readonly List<Type> _serviceTypes = [];
    private readonly List<string> _identityKeys = [];
    private int _port = 9080;

    internal RestateHostBuilder()
    {
    }

    /// <summary>Registers a Restate service type (reads the attribute to determine kind).</summary>
    public RestateHostBuilder Bind<TService>() where TService : class
    {
        _serviceTypes.Add(typeof(TService));
        return this;
    }

    /// <summary>Registers a Restate <see cref="ServiceAttribute">Service</see> type.</summary>
    public RestateHostBuilder AddService<TService>() where TService : class
    {
        return Bind<TService>();
    }

    /// <summary>Registers a Restate <see cref="VirtualObjectAttribute">VirtualObject</see> type.</summary>
    public RestateHostBuilder AddVirtualObject<TService>() where TService : class
    {
        return Bind<TService>();
    }

    /// <summary>Registers a Restate <see cref="WorkflowAttribute">Workflow</see> type.</summary>
    public RestateHostBuilder AddWorkflow<TService>() where TService : class
    {
        return Bind<TService>();
    }

    /// <summary>Sets the port to listen on. Default is 9080.</summary>
    public RestateHostBuilder WithPort(int port)
    {
        _port = port;
        return this;
    }

    /// <summary>
    ///     Configures Restate identity public keys (<c>publickeyv1_&lt;base58&gt;</c>) used to verify
    ///     that incoming requests originate from a trusted Restate instance. When at least one key is
    ///     configured, every request to the Restate endpoints must carry a valid
    ///     <c>x-restate-signature-scheme: v1</c> signature; requests that fail verification are
    ///     rejected with <c>401 Unauthorized</c>. When no keys are configured, requests are not verified.
    /// </summary>
    /// <param name="keys">The serialized identity keys, as printed by <c>restate-server</c> on startup.</param>
    /// <returns>This builder for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">
    ///     <paramref name="keys" /> is empty (which would silently leave verification disabled),
    ///     or a key is malformed (wrong prefix, invalid base58, or wrong decoded length).
    /// </exception>
    public RestateHostBuilder WithIdentityKeys(params string[] keys)
    {
        // Validate eagerly so malformed keys fail here, not later inside Build().
        _ = RequestIdentityVerifier.ParseKeys(keys);
        _identityKeys.AddRange(keys);
        return this;
    }

    /// <summary>
    ///     Builds a <see cref="WebApplication" /> with Restate routes mapped.
    /// </summary>
    [RequiresUnreferencedCode("Build uses reflection-based DI and JSON serialization.")]
    [RequiresDynamicCode("Build uses reflection-based DI and JSON serialization.")]
    public WebApplication Build()
    {
        var builder = WebApplication.CreateBuilder();

        ConfigureKestrel(builder);

        var types = _serviceTypes;
        var identityKeys = _identityKeys;
        builder.Services.AddRestate(opts =>
        {
            foreach (var type in types)
                opts.ServiceTypes.Add(type);

            opts.IdentityKeys.AddRange(identityKeys);
        });

        var app = builder.Build();
        app.MapRestate();

        return app;
    }

    /// <summary>
    ///     Builds a <see cref="WebApplication" /> using source-generated registration for NativeAOT compatibility.
    ///     Use this instead of <see cref="Build" /> when publishing with <c>&lt;PublishAot&gt;true&lt;/PublishAot&gt;</c>.
    ///     Requires the source-generated <c>AddRestateGenerated()</c> extension method (emitted by the Restate source generator).
    /// </summary>
    /// <param name="configureServices">
    ///     Callback to register services using the source-generated <c>AddRestateGenerated()</c> extension.
    ///     Example: <c>services => services.AddRestateGenerated()</c>
    /// </param>
    public WebApplication BuildAot(Action<IServiceCollection> configureServices)
    {
        var builder = WebApplication.CreateSlimBuilder();

        ConfigureKestrel(builder);

        configureServices(builder.Services);

        if (_identityKeys.Count > 0)
            builder.Services.AddSingleton(RequestIdentityVerifier.FromKeys(_identityKeys));

        var app = builder.Build();
        app.MapRestate();

        return app;
    }

    private void ConfigureKestrel(WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(_port, listenOptions => { listenOptions.Protocols = HttpProtocols.Http2; });

            // Restate uses bidirectional streaming: the runtime sends the
            // StartMessage then pauses while the SDK processes and sends commands.
            // Kestrel's default MinRequestBodyDataRate would abort these pauses.
            options.Limits.MinRequestBodyDataRate = null;

            // Replay journals can be large for long-running workflows.
            // Remove the default 30 MB limit to avoid truncation.
            options.Limits.MaxRequestBodySize = null;

            // Increase HTTP/2 flow control windows for faster streaming throughput.
            options.Limits.Http2.InitialConnectionWindowSize = 1024 * 1024; // 1 MB
            options.Limits.Http2.InitialStreamWindowSize = 512 * 1024; // 512 KB
        });
    }
}