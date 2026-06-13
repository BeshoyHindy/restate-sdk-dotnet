namespace Restate.Sdk;

/// <summary>
///     Options for <see cref="IContext.Call{TResponse}(string, string, object?, CallOptions)" /> operations.
///     Provides idempotency key support for call deduplication.
/// </summary>
public readonly record struct CallOptions
{
    /// <summary>
    ///     Idempotency key to deduplicate call operations.
    ///     When set, Restate ensures at-most-once execution for calls with the same key.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    ///     Custom request headers attached to the call (shared-core <c>Target.headers</c>). Populated
    ///     onto <c>CallCommandMessage.headers</c> (field 4) in declaration order; the runtime forwards
    ///     them to the callee. Null/empty emits no headers.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    ///     Optional custom command/entry name (shared-core <c>sys_call</c> <c>name</c> →
    ///     <c>CallCommandMessage.name</c>, proto field 12). Surfaced for tooling/observability; it is
    ///     part of the journaled command's replay equality (Rust <c>header_eq</c> compares
    ///     <c>name</c>), so the same name MUST be supplied across replays. Null/empty emits no name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    ///     Per-op payload serialization options for the call request parameter (shared-core
    ///     <c>sys_call</c> PayloadOptions, mapped onto <c>CallCommandMessage.parameter</c>; the replay
    ///     byte-compare arm is messages.rs:190 <c>ignore_payload_equality || self.parameter ==
    ///     other.parameter</c>). Only consulted under global <see cref="Hosting.PayloadReplayChecks.Strict" />;
    ///     set <see cref="PayloadOptions.Unstable" /> to skip the request-bytes compare for this call.
    /// </summary>
    public PayloadOptions Payload { get; init; }

    /// <inheritdoc cref="IdempotencyKey" />
    public static CallOptions WithIdempotencyKey(string key) =>
        new() { IdempotencyKey = key };

    /// <inheritdoc cref="Headers" />
    public static CallOptions WithHeaders(IReadOnlyDictionary<string, string> headers) =>
        new() { Headers = headers };

    /// <inheritdoc cref="Name" />
    public static CallOptions WithName(string name) =>
        new() { Name = name };

    /// <inheritdoc cref="Payload" />
    public static CallOptions WithPayloadOptions(PayloadOptions payload) =>
        new() { Payload = payload };
}
