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

    /// <inheritdoc cref="IdempotencyKey" />
    public static CallOptions WithIdempotencyKey(string key) =>
        new() { IdempotencyKey = key };

    /// <inheritdoc cref="Headers" />
    public static CallOptions WithHeaders(IReadOnlyDictionary<string, string> headers) =>
        new() { Headers = headers };
}
