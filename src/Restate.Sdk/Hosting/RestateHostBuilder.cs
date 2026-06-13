using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

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
    private int _port = 9080;
    private PayloadReplayChecks _payloadReplayChecks = PayloadReplayChecks.Disabled;

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
    ///     G13 — opts into <see cref="PayloadReplayChecks.Strict" /> replay payload byte-equality checks
    ///     for this endpoint. Default is <see cref="PayloadReplayChecks.Disabled" />. Only safe for handlers
    ///     whose payloads serialize to byte-stable output; see <see cref="PayloadReplayChecks" /> for the
    ///     dictionary/hashset caveat.
    /// </summary>
    public RestateHostBuilder WithStrictPayloadChecks()
    {
        _payloadReplayChecks = PayloadReplayChecks.Strict;
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
        builder.WebHost.ConfigureRestate(_port);

        var types = _serviceTypes;
        var payloadReplayChecks = _payloadReplayChecks;
        builder.Services.AddRestate(opts =>
        {
            foreach (var type in types)
                opts.ServiceTypes.Add(type);
            opts.PayloadReplayChecks = payloadReplayChecks;
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
        builder.WebHost.ConfigureRestate(_port);

        configureServices(builder.Services);

        var app = builder.Build();
        app.MapRestate();

        return app;
    }
}