namespace Restate.Sdk;

/// <summary>
///     Options controlling the SDK's OpenTelemetry-compatible instrumentation.
///     The SDK exposes an <see cref="System.Diagnostics.ActivitySource" /> and a
///     <see cref="System.Diagnostics.Metrics.Meter" />, both named <c>Restate.Sdk</c>.
/// </summary>
public sealed class RestateTelemetryOptions
{
    /// <summary>
    ///     Enables per-operation child activities (spans) for durable operations
    ///     (<c>Run</c>, <c>Call</c>, and <c>Sleep</c>) beneath the invocation activity.
    ///     Off by default: each traced operation allocates an <see cref="System.Diagnostics.Activity" />
    ///     on the invocation hot path. Even when enabled, activities are only created while a
    ///     listener is attached to the <c>Restate.Sdk</c> source.
    /// </summary>
    public bool EnableOperationActivities { get; set; }
}
