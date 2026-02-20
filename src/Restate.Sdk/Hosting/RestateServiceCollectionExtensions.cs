using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Restate.Sdk.Endpoint;
using Restate.Sdk.Internal;

namespace Restate.Sdk.Hosting;

/// <summary>
///     Extension methods for registering Restate services with dependency injection.
/// </summary>
public static class RestateServiceCollectionExtensions
{
    /// <summary>
    ///     Adds Restate services to the dependency injection container.
    /// </summary>
    /// <example>
    ///     <code>
    /// builder.Services.AddRestate(opts =&gt; opts.AddService&lt;GreeterService&gt;());
    /// </code>
    /// </example>
    [RequiresUnreferencedCode("AddRestate uses reflection-based DI registration.")]
    public static IServiceCollection AddRestate(
        this IServiceCollection services,
        Action<RestateOptions> configure
    )
    {
        var options = new RestateOptions();
        configure(options);

        var registry = ServiceRegistry.FromTypes(options.ServiceTypes);
        services.AddSingleton(registry);
        services.TryAddSingleton<InvocationHandler>();

        foreach (var type in options.ServiceTypes)
            services.TryAddScoped(type);

        return services;
    }

    /// <summary>
    ///     Registers Restate services using pre-built <see cref="ServiceDefinition" /> instances.
    ///     This method is AOT-safe and is called by the source-generated <c>AddRestateGenerated()</c> extension.
    ///     Unlike <see cref="AddRestate" />, this overload does not use reflection and is compatible with NativeAOT publishing.
    ///     Service type DI registration is handled by the generated code using generic <c>TryAddScoped&lt;T&gt;()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="definitions">Service definitions resolved from the source-generated registry.</param>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection AddRestateAot(
        this IServiceCollection services,
        ServiceDefinition[] definitions
    )
    {
        var registry = new ServiceRegistry();
        foreach (var def in definitions)
            registry.Register(def);
        registry.Freeze();

        services.AddSingleton(registry);
        services.TryAddSingleton<InvocationHandler>();

        return services;
    }
}
