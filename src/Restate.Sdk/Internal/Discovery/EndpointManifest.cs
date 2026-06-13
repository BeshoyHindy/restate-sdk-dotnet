using System.Text.Json.Serialization;
using Restate.Sdk.Endpoint;

namespace Restate.Sdk.Internal.Discovery;

/// <summary>
///     Source-generated JSON serializer context for discovery manifest types.
///     Eliminates reflection-based serialization, making discovery AOT-compatible.
/// </summary>
[JsonSerializable(typeof(EndpointManifest))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class DiscoveryJsonContext : JsonSerializerContext;

internal sealed class EndpointManifest
{
    [JsonPropertyName("protocolMode")] public string ProtocolMode { get; init; } = "BIDI_STREAM";

    [JsonPropertyName("minProtocolVersion")]
    public int MinProtocolVersion { get; init; } = 5;

    [JsonPropertyName("maxProtocolVersion")]
    public int MaxProtocolVersion { get; init; } = 6;

    [JsonPropertyName("services")] public required IReadOnlyList<ServiceManifest> Services { get; init; }

    public static EndpointManifest FromRegistry(ServiceRegistry registry, string protocolMode = "BIDI_STREAM")
    {
        var services = registry.Services
            .Select(static s => ServiceManifest.FromDefinition(s))
            .ToList();

        return new EndpointManifest { ProtocolMode = protocolMode, Services = services };
    }
}

internal sealed class ServiceManifest
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("ty")] public required string Type { get; init; }

    [JsonPropertyName("handlers")] public required IReadOnlyList<HandlerManifest> Handlers { get; init; }

    [JsonPropertyName("workflowCompletionRetention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WorkflowCompletionRetention { get; init; }

    [JsonPropertyName("documentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Documentation { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public static ServiceManifest FromDefinition(ServiceDefinition def)
    {
        var typeName = def.Type switch
        {
            ServiceType.Service => "SERVICE",
            ServiceType.VirtualObject => "VIRTUAL_OBJECT",
            ServiceType.Workflow => "WORKFLOW",
            _ => "SERVICE"
        };

        var handlers = def.Handlers
            .Select(h => HandlerManifest.FromDefinition(h, def.Type))
            .ToList();

        return new ServiceManifest
        {
            Name = def.Name,
            Type = typeName,
            Handlers = handlers,
            WorkflowCompletionRetention = def.WorkflowRetentionMs,
            // G36 — service-level documentation/metadata advertised by the discovery manifest schema.
            Documentation = def.Documentation,
            Metadata = def.Metadata,
        };
    }
}

internal sealed class HandlerManifest
{
    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("ty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("input")]
    public required PayloadDescriptor Input { get; init; }

    [JsonPropertyName("output")]
    public required PayloadDescriptor Output { get; init; }

    [JsonPropertyName("documentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Documentation { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    [JsonPropertyName("inactivityTimeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? InactivityTimeout { get; init; }

    [JsonPropertyName("abortTimeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? AbortTimeout { get; init; }

    [JsonPropertyName("idempotencyRetention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? IdempotencyRetention { get; init; }

    [JsonPropertyName("journalRetention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? JournalRetention { get; init; }

    [JsonPropertyName("ingressPrivate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IngressPrivate { get; init; }

    public static HandlerManifest FromDefinition(HandlerDefinition def, ServiceType serviceType)
    {
        string? handlerType = null;

        if (serviceType is ServiceType.VirtualObject)
            handlerType = def.IsShared ? "SHARED" : "EXCLUSIVE";
        else if (serviceType is ServiceType.Workflow)
            handlerType = def.IsShared ? "SHARED" : "WORKFLOW";

        var input = def.HasInput
            ? new PayloadDescriptor { Required = true, ContentType = def.ContentType }
            : new PayloadDescriptor(); // {} — "only empty body accepted"

        var output = def.HasOutput
            ? new PayloadDescriptor { ContentType = def.ContentType, SetContentTypeIfEmpty = false }
            : new PayloadDescriptor { SetContentTypeIfEmpty = false }; // void output

        return new HandlerManifest
        {
            Name = def.Name,
            Type = handlerType,
            Input = input,
            Output = output,
            Documentation = def.Documentation,
            Metadata = def.Metadata,
            InactivityTimeout = def.InactivityTimeoutMs,
            AbortTimeout = def.AbortTimeoutMs,
            IdempotencyRetention = def.IdempotencyRetentionMs,
            JournalRetention = def.JournalRetentionMs,
            IngressPrivate = def.IngressPrivate,
        };
    }
}

// G35 (accepted divergence) — the discovery-manifest schema marks the input/output `jsonSchema` field
// OPTIONAL, and the runtime never requires it: it is discovery-only metadata used for ingress request
// validation / OpenAPI generation, not for handler dispatch. This SDK does not generate JSON Schema for
// handler input/output payload types, so the field is omitted. The fields the runtime DOES need —
// `contentType`, `required`, and `setContentTypeIfEmpty` — are emitted below. Emitting `jsonSchema` would
// require a payload-type → JSON-Schema generator (out of scope), so its absence is an accepted, runtime-safe
// optional-field divergence rather than a parity defect.
internal sealed class PayloadDescriptor
{
    [JsonPropertyName("contentType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentType { get; init; }

    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Required { get; init; }

    [JsonPropertyName("setContentTypeIfEmpty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? SetContentTypeIfEmpty { get; init; }
}
