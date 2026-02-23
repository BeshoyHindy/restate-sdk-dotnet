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

    /// <inheritdoc cref="IdempotencyKey" />
    public static CallOptions WithIdempotencyKey(string key) =>
        new() { IdempotencyKey = key };
}
