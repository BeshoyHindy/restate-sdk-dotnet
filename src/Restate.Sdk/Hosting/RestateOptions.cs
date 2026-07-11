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

    internal List<string> IdentityKeys { get; } = [];

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

    /// <summary>
    ///     Configures Restate identity public keys (<c>publickeyv1_&lt;base58&gt;</c>) used to verify
    ///     that incoming requests originate from a trusted Restate instance. When at least one key is
    ///     configured, every request to the Restate endpoints must carry a valid
    ///     <c>x-restate-signature-scheme: v1</c> signature; requests that fail verification are
    ///     rejected with <c>401 Unauthorized</c>. When no keys are configured, requests are not verified.
    /// </summary>
    /// <param name="keys">The serialized identity keys, as printed by <c>restate-server</c> on startup.</param>
    /// <returns>This options instance for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keys" /> is <see langword="null" />.</exception>
    public RestateOptions WithIdentityKeys(params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        IdentityKeys.AddRange(keys);
        return this;
    }
}