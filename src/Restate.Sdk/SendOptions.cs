namespace Restate.Sdk;

/// <summary>
///     Options for fire-and-forget send operations.
/// </summary>
public readonly record struct SendOptions
{
    /// <summary>Delay before the invocation is executed.</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>Idempotency key to deduplicate send operations.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    ///     Custom request headers attached to the send (shared-core <c>Target.headers</c>). Populated
    ///     onto <c>OneWayCallCommandMessage.headers</c> (field 5) in declaration order. Null/empty
    ///     emits no headers.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    ///     Optional custom command/entry name (shared-core <c>sys_send</c> <c>name</c> →
    ///     <c>OneWayCallCommandMessage.name</c>, proto field 12). Part of the journaled command's
    ///     replay equality (Rust <c>header_eq</c> compares <c>name</c>), so the same name MUST be
    ///     supplied across replays. Null/empty emits no name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    ///     Per-op payload serialization options for the send request parameter (shared-core
    ///     <c>sys_send</c> PayloadOptions, mapped onto <c>OneWayCallCommandMessage.parameter</c>; the
    ///     replay byte-compare arm is messages.rs:207 <c>ignore_payload_equality || self.parameter ==
    ///     other.parameter</c>). Only consulted under global <see cref="Hosting.PayloadReplayChecks.Strict" />;
    ///     set <see cref="PayloadOptions.Unstable" /> to skip the request-bytes compare for this send.
    /// </summary>
    public PayloadOptions Payload { get; init; }

    /// <inheritdoc cref="Delay" />
    public static SendOptions AfterDelay(TimeSpan delay)
    {
        return new SendOptions { Delay = delay };
    }

    /// <inheritdoc cref="IdempotencyKey" />
    public static SendOptions WithIdempotencyKey(string key)
    {
        return new SendOptions { IdempotencyKey = key };
    }

    /// <inheritdoc cref="Headers" />
    public static SendOptions WithHeaders(IReadOnlyDictionary<string, string> headers)
    {
        return new SendOptions { Headers = headers };
    }

    /// <inheritdoc cref="Name" />
    public static SendOptions WithName(string name)
    {
        return new SendOptions { Name = name };
    }

    /// <inheritdoc cref="Payload" />
    public static SendOptions WithPayloadOptions(PayloadOptions payload)
    {
        return new SendOptions { Payload = payload };
    }
}