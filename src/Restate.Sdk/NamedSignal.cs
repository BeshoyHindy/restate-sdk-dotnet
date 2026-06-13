namespace Restate.Sdk;

/// <summary>
///     A durable promise keyed by a string NAME that is fulfilled when another invocation sends a
///     matching named signal (the cross-SDK "signals" / durable-promise-by-name primitive: TS
///     <c>ctx.promise</c>/signal, Python <c>add_signal_handler</c>, Rust <c>create_signal_handle</c>).
///     Unlike <see cref="Awakeable{T}" />, which is delivered by a numeric signal id, the awaited
///     value arrives by name — the sender supplies the same name to
///     <see cref="IContext.SendSignal{T}" />.
/// </summary>
public readonly record struct NamedSignal<T>
{
    /// <summary>
    ///     The signal name this promise waits on. A sender resolves it by sending a signal with the
    ///     same name to this invocation.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     The value that will be available once a matching named signal is delivered.
    /// </summary>
    /// <remarks>This value must only be awaited once.</remarks>
    public required ValueTask<T> Value { get; init; }
}
