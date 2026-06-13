using System.ComponentModel;

namespace Restate.Sdk.Endpoint;

/// <summary>
///     Describes a Restate service with its handlers.
///     Built at compile time by the source generator.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class ServiceDefinition
{
    /// <summary>Gets the service name as registered with the Restate runtime.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the type of Restate service (Service, VirtualObject, or Workflow).</summary>
    public required ServiceType Type { get; init; }

    /// <summary>Gets the factory delegate that creates service instances from an <see cref="IServiceProvider" />.</summary>
    public required Func<IServiceProvider, object> Factory { get; init; }

    /// <summary>Gets the list of handler definitions for this service.</summary>
    public required IReadOnlyList<HandlerDefinition> Handlers { get; init; }

    /// <summary>Duration to retain workflow execution data after completion, in milliseconds. Only applicable to workflows.</summary>
    public long? WorkflowRetentionMs { get; init; }

    /// <summary>
    ///     Optional service-level documentation surfaced on the discovery manifest (endpoint manifest
    ///     schema service.documentation, G36). Markdown is the conventional format.
    /// </summary>
    public string? Documentation { get; init; }

    /// <summary>
    ///     Optional service-level custom metadata surfaced on the discovery manifest (endpoint manifest
    ///     schema service.metadata, G36) and shown on the Restate Admin API.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
