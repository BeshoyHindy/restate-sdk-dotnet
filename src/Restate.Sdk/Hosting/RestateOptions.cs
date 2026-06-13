namespace Restate.Sdk.Hosting;

/// <summary>
///     Configuration options for Restate services registered via dependency injection.
/// </summary>
public sealed class RestateOptions
{
    internal List<Type> ServiceTypes { get; } = [];

    /// <summary>
    ///     Global replay payload byte-equality policy (G13). The .NET surface of shared-core's
    ///     <c>NonDeterministicChecksOption</c> (lib.rs:237-244). Defaults to
    ///     <see cref="PayloadReplayChecks.Disabled" /> — payload bytes are NOT byte-compared on replay —
    ///     which preserves the SDK's historical behavior and avoids the System.Text.Json
    ///     unordered-collection false-positive. Set <see cref="PayloadReplayChecks.Strict" /> to opt into
    ///     the byte-compare (only safe for byte-stable payloads or with per-op
    ///     <see cref="Restate.Sdk.PayloadOptions.Unstable" /> exemptions). The default value preserves the
    ///     meaning of every existing registration.
    /// </summary>
    public PayloadReplayChecks PayloadReplayChecks { get; set; } = PayloadReplayChecks.Disabled;

    /// <summary>
    ///     Fluent opt-in to <see cref="PayloadReplayChecks.Strict" /> replay payload byte-equality checks.
    ///     Use only for handlers whose payloads serialize to byte-stable output (scalars, strings, records,
    ///     ordered lists, arrays). See <see cref="PayloadReplayChecks" /> for the dictionary/hashset caveat.
    /// </summary>
    public RestateOptions WithStrictPayloadChecks()
    {
        PayloadReplayChecks = PayloadReplayChecks.Strict;
        return this;
    }

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