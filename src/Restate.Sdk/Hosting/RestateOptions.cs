namespace Restate.Sdk.Hosting;

/// <summary>
///     Configuration options for Restate services registered via dependency injection.
/// </summary>
public sealed class RestateOptions
{
    internal List<Type> ServiceTypes { get; } = [];

    /// <summary>
    ///     Telemetry options for the SDK's tracing and metrics instrumentation
    ///     (the <c>Restate.Sdk</c> ActivitySource and Meter).
    /// </summary>
    public RestateTelemetryOptions Telemetry { get; } = new();

    /// <summary>Registers a Restate service type (reads the attribute to determine kind).</summary>
    public RestateOptions Bind<TService>() where TService : class
    {
        ServiceTypes.Add(typeof(TService));
        return this;
    }

    /// <summary>Registers a Restate <see cref="ServiceAttribute">Service</see> type.</summary>
    public RestateOptions AddService<TService>() where TService : class
    {
        return Bind<TService>();
    }

    /// <summary>Registers a Restate <see cref="VirtualObjectAttribute">VirtualObject</see> type.</summary>
    public RestateOptions AddVirtualObject<TService>() where TService : class
    {
        return Bind<TService>();
    }

    /// <summary>Registers a Restate <see cref="WorkflowAttribute">Workflow</see> type.</summary>
    public RestateOptions AddWorkflow<TService>() where TService : class
    {
        return Bind<TService>();
    }
}