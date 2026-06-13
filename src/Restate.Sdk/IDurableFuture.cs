namespace Restate.Sdk;

/// <summary>
///     A durable future representing the result of a non-blocking Restate operation.
///     Used with combinators like <see cref="Context.All{T}" /> and <see cref="Context.Race{T}" />.
/// </summary>
public interface IDurableFuture
{
    /// <summary>Awaits the result of this future, returning it as an untyped object.</summary>
    ValueTask<object?> GetResult();
}

/// <summary>
///     A typed durable future representing the result of a non-blocking Restate operation.
///     Returned by non-blocking variants like <see cref="Context.RunAsync{T}(string, Func{Task{T}})" />,
///     <see cref="Context.Timer" />, and future-returning typed client methods.
/// </summary>
public interface IDurableFuture<T> : IDurableFuture
{
    /// <summary>
    ///     The invocation ID associated with this future, if it originated from a call or send operation.
    ///     Null for non-invocation futures (e.g., timers, side effects).
    /// </summary>
    string? InvocationId { get; }

    /// <summary>Awaits the typed result of this future.</summary>
    new ValueTask<T> GetResult();
}