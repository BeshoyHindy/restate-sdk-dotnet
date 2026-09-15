namespace Restate.Sdk;

/// <summary>
///     A handle to a running invocation, returned by send operations.
///     Carries the invocation id, and addresses signals on that invocation.
/// </summary>
public readonly record struct InvocationHandle(string InvocationId)
{
    /// <summary>
    ///     Resolves a named signal on this invocation with a value. The handler being signalled
    ///     receives it from its <see cref="Context.Signal{T}(string)" /> future, whether it is
    ///     already awaiting the signal or reaches it later.
    /// </summary>
    /// <param name="context">The context of the handler sending the signal.</param>
    /// <param name="name">The signal name the target handler awaits.</param>
    /// <param name="value">The value the signal resolves with.</param>
    public ValueTask ResolveSignal<T>(Context context, string name, T value)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ResolveSignal(InvocationId, name, value);
    }

    /// <summary>
    ///     Rejects a named signal on this invocation: the handler awaiting it fails with a
    ///     <see cref="TerminalException" /> carrying <paramref name="reason" />.
    /// </summary>
    /// <param name="context">The context of the handler sending the signal.</param>
    /// <param name="name">The signal name the target handler awaits.</param>
    /// <param name="reason">The rejection reason the awaiting handler sees.</param>
    public ValueTask RejectSignal(Context context, string name, string reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RejectSignal(InvocationId, name, reason);
    }
}
