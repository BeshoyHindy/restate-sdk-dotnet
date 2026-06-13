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
}