namespace Restate.Sdk;

/// <summary>
///     Options for <see cref="IContext.Call{TResponse}(string, string, object?, CallOptions)" /> operations.
///     Provides idempotency key support for call deduplication, and the flow-control scope and
///     limit key the server uses to bound concurrency.
/// </summary>
public readonly record struct CallOptions
{
    /// <summary>
    ///     Idempotency key to deduplicate call operations.
    ///     When set, Restate ensures at-most-once execution for calls with the same key.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    ///     Name of the server-side concurrency scope the invocation runs in.
    ///     Null (the default) invokes outside any scope.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    ///     Narrows the concurrency limit within <see cref="Scope" /> to invocations sharing this key.
    ///     Only valid together with a scope: setting it without one throws when the call is made.
    /// </summary>
    public string? LimitKey { get; init; }

    /// <inheritdoc cref="IdempotencyKey" />
    public static CallOptions WithIdempotencyKey(string key) =>
        new() { IdempotencyKey = key };

    /// <inheritdoc cref="Scope" />
    public static CallOptions WithScope(string scope) =>
        new() { Scope = scope };

    /// <inheritdoc cref="LimitKey" />
    public static CallOptions WithScope(string scope, string limitKey) =>
        new() { Scope = scope, LimitKey = limitKey };
}
