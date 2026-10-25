using Restate.Sdk.Internal;

namespace Restate.Sdk;

/// <summary>
///     A durable promise that can be completed from outside the current invocation.
///     <para>
///         The awakeable is itself an <see cref="IDurableFuture{T}" />, so it composes with
///         <see cref="Context.All{T}" />, <see cref="Context.Race{T}" />,
///         <see cref="Context.Any{T}" />, <see cref="Context.AllSettled{T}" /> and
///         <see cref="Context.WaitAll" /> without unwrapping <see cref="Future" />.
///     </para>
/// </summary>
public readonly record struct Awakeable<T> : IDurableFuture<T>, IArrivalOrderedFuture
{
    /// <summary>
    ///     The unique identifier for this awakeable. Pass this to an external system
    ///     so it can resolve the awakeable when ready.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     The value that will be available once the awakeable is resolved.
    /// </summary>
    /// <remarks>This value must only be awaited once.</remarks>
    public required ValueTask<T> Value { get; init; }

    /// <summary>
    ///     The durable future that settles when the awakeable is resolved or rejected. The
    ///     payload is deserialized on the first await of it or of <see cref="Value" />, not when
    ///     the runtime delivers it.
    /// </summary>
    public required IDurableFuture<T> Future { get; init; }

    /// <inheritdoc />
    /// <remarks>Always null: an awakeable starts no invocation.</remarks>
    public string? InvocationId => Future.InvocationId;

    /// <summary>
    ///     Awaits the awakeable's value, forwarding to <see cref="Future" />.
    ///     Safe to await more than once: every await observes the same outcome and the same
    ///     decoded value.
    /// </summary>
    public ValueTask<T> GetResult()
    {
        return Future.GetResult();
    }

    async ValueTask<object?> IDurableFuture.GetResult()
    {
        return await GetResult().ConfigureAwait(false);
    }

    // Forwarded so that an awakeable handed straight to a combinator is ranked by the position
    // the runtime delivered its result at, exactly as the future inside it would be.
    long IArrivalOrderedFuture.ArrivalOrder => (Future as IArrivalOrderedFuture)?.ArrivalOrder ?? 0;

    // Forwarded for the same reason: the combinators park on the awakeable's own delivery, so the
    // awakeable costs nothing extra and its value is deserialized only if a combinator hands it back.
    RawCompletion IArrivalOrderedFuture.Completion => Future is IArrivalOrderedFuture ordered
        ? ordered.Completion
        : RawCompletion.OfWait(Future.GetResult().AsTask());
}
