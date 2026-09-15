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
    ///     Name of the server-side concurrency scope the invocation runs in.
    ///     Null (the default) invokes outside any scope.
    /// </summary>
    public string? Scope { get; init; }

    /// <summary>
    ///     Narrows the concurrency limit within <see cref="Scope" /> to invocations sharing this key.
    ///     Only valid together with a scope: setting it without one throws when the send is made.
    /// </summary>
    public string? LimitKey { get; init; }

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

    /// <inheritdoc cref="Scope" />
    public static SendOptions WithScope(string scope)
    {
        return new SendOptions { Scope = scope };
    }

    /// <inheritdoc cref="LimitKey" />
    public static SendOptions WithScope(string scope, string limitKey)
    {
        return new SendOptions { Scope = scope, LimitKey = limitKey };
    }
}
