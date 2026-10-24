using Restate.Sdk.Internal;

namespace Restate.Sdk;

/// <summary>
///     A handle to an unnamed signal a handler awaits: the <see cref="Index" /> that addresses it
///     on this invocation, and the durable future that settles when the signal is resolved or
///     rejected. Pass <see cref="Index" /> together with the invocation id — the one an
///     <see cref="InvocationHandle" /> carries — to
///     <see cref="Context.ResolveSignal{T}(string, int, T)" /> or
///     <see cref="Context.RejectSignal(string, int, string)" />.
///     <para>
///         The handle is itself an <see cref="IDurableFuture{T}" />, so it composes with
///         <see cref="Context.All{T}" />, <see cref="Context.Race{T}" />,
///         <see cref="Context.Any{T}" />, <see cref="Context.AllSettled{T}" /> and
///         <see cref="Context.WaitAll" /> without unwrapping <see cref="Future" />.
///     </para>
/// </summary>
/// <param name="Index">
///     The signal index this invocation allocated. It addresses the signal on this invocation and
///     is unique within it; indices below the first user index are reserved for built-in signals.
/// </param>
/// <param name="Future">The durable future that settles when the signal is resolved or rejected.</param>
public readonly record struct SignalHandle<T>(int Index, IDurableFuture<T> Future)
    : IDurableFuture<T>, IArrivalOrderedFuture
{
    /// <inheritdoc />
    public string? InvocationId => Future.InvocationId;

    /// <summary>
    ///     Awaits the signal's value, forwarding to <see cref="Future" />.
    ///     Safe to await more than once: every await observes the same outcome.
    /// </summary>
    public ValueTask<T> GetResult()
    {
        return Future.GetResult();
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        return await GetResult().ConfigureAwait(false);
    }

    // Forwarded so that a handle handed straight to a combinator is ranked by the position the
    // runtime delivered its result at, exactly as the future inside it would be.
    long IArrivalOrderedFuture.ArrivalOrder => (Future as IArrivalOrderedFuture)?.ArrivalOrder ?? 0;
}
