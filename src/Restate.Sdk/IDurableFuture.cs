namespace Restate.Sdk;

/// <summary>
///     A durable future representing the result of a non-blocking Restate operation.
///     Used with combinators like <see cref="Context.All{T}" /> and <see cref="Context.Race{T}" />.
/// </summary>
/// <remarks>
///     A context and the futures it hands out must not be used from more than one thread at a
///     time: concurrent context operations reorder the journal and break replay.
///     <para>
///         <c>Task.WhenAny</c> and <c>Task.WhenAll</c> over durable futures bypass the delivery
///         ordering the combinators provide, so compose futures with
///         <see cref="Context.All{T}" />, <see cref="Context.Race{T}" />,
///         <see cref="Context.Any{T}" />, <see cref="Context.AllSettled{T}" /> or
///         <see cref="Context.WaitAll" />.
///     </para>
/// </remarks>
public interface IDurableFuture
{
    /// <summary>
    ///     Awaits the result of this future, returning it as an untyped object.
    ///     Safe to await more than once: every await observes the same outcome.
    /// </summary>
    ValueTask<object?> GetResult();
}

/// <summary>
///     A typed durable future representing the result of a non-blocking Restate operation.
///     Returned by non-blocking variants like <see cref="Context.RunAsync{T}" />,
///     <see cref="Context.Timer" />, and future-returning typed client methods.
/// </summary>
public interface IDurableFuture<T> : IDurableFuture
{
    /// <summary>
    ///     The invocation id of the invocation this future started, or null.
    ///     <para>
    ///         Non-null only on a future returned by a call operation — <c>CallFuture</c> or a
    ///         future-returning typed client method — and only once the server has delivered the
    ///         id. Reading never blocks, so it is null between starting the call and the
    ///         notification arriving; awaiting the future is enough to make the id available.
    ///     </para>
    ///     <para>
    ///         Always null for futures that start no invocation: timers, side effects
    ///         (<c>RunAsync</c>), awakeables and signals. One-way sends do not return a future;
    ///         they hand back an <see cref="InvocationHandle" /> that always carries the id.
    ///     </para>
    /// </summary>
    string? InvocationId { get; }

    /// <summary>
    ///     Awaits the typed result of this future.
    ///     Safe to await more than once: every await observes the same outcome.
    /// </summary>
    new ValueTask<T> GetResult();
}