namespace Restate.Sdk;

/// <summary>
///     Interface exposing durable execution primitives available to all handler types.
///     Use this for utility methods, type constraints, and testing with mocking frameworks.
///     Handlers receive the abstract <see cref="Context" /> class; use Mock*Context classes for handler testing.
/// </summary>
public interface IContext
{
    /// <summary>Unique identifier of the current invocation.</summary>
    string InvocationId { get; }

    /// <summary>Request headers from the original invocation.</summary>
    IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Cancellation token that fires when the invocation is aborted.</summary>
    CancellationToken Aborted { get; }

    /// <summary>Executes an async side effect durably. The result is journaled and replayed on retries.</summary>
    ValueTask<T> Run<T>(string name, Func<Task<T>> action);

    /// <summary>Executes an async void side effect durably.</summary>
    ValueTask Run(string name, Func<Task> action);

    /// <summary>Executes a synchronous side effect durably. The result is journaled and replayed on retries.</summary>
    ValueTask<T> Run<T>(string name, Func<T> action);

    /// <summary>Executes an async side effect with a restricted run context. Prevents nested Restate operations.</summary>
    ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action, RunOptions? options = null);

    /// <summary>Executes an async void side effect with a restricted run context.</summary>
    ValueTask Run(string name, Func<IRunContext, Task> action, RunOptions? options = null);

    /// <summary>Calls a handler on a stateless service and awaits its response.</summary>
    ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null);

    /// <summary>Calls a handler on a keyed virtual object or workflow and awaits its response.</summary>
    ValueTask<TResponse> Call<TResponse>(string service, string key, string handler, object? request = null);

    /// <summary>Sends a one-way invocation to a stateless service. Returns a handle to track the invocation.</summary>
    ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>Sends a one-way invocation to a keyed virtual object or workflow. Returns a handle to track the invocation.</summary>
    ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>Suspends execution for the specified duration. Durable — survives process restarts.</summary>
    ValueTask Sleep(TimeSpan duration);

    /// <summary>Creates a durable promise that can be resolved from outside the current invocation.</summary>
    Awakeable<T> Awakeable<T>(ISerde<T>? serde = null);

    /// <summary>Resolves a previously created awakeable with a payload.</summary>
    void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null);

    /// <summary>Rejects a previously created awakeable with a failure reason.</summary>
    void RejectAwakeable(string id, string reason);

    /// <summary>Returns the current time, durably journaled for replay safety.</summary>
    ValueTask<DateTimeOffset> Now();

    /// <summary>Attaches to a running invocation and awaits its result.</summary>
    ValueTask<T> Attach<T>(string invocationId);

    /// <summary>Gets the output of a completed invocation, or default if not yet completed.</summary>
    ValueTask<T?> GetOutput<T>(string invocationId);

    /// <summary>
    ///     Calls a handler by service and handler name with typed request/response serialization.
    /// </summary>
    ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler,
        TRequest request, string? key = null);

    /// <summary>
    ///     Sends a one-way invocation by service and handler name with typed request serialization.
    /// </summary>
    ValueTask<InvocationHandle> Send<TRequest>(string service, string handler,
        TRequest request, string? key = null, SendOptions? options = null);

    /// <summary>
    ///     Executes an async side effect and returns a non-blocking future.
    /// </summary>
    IDurableFuture<T> RunAsync<T>(string name, Func<Task<T>> action);

    /// <summary>
    ///     Creates a non-blocking durable timer that completes after the specified duration.
    /// </summary>
    IDurableFuture Timer(TimeSpan duration);

    /// <summary>
    ///     Calls a handler and returns a non-blocking future instead of awaiting the result.
    /// </summary>
    IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
        object? request = null);

    /// <summary>
    ///     Calls a handler on a keyed virtual object or workflow and returns a non-blocking future.
    /// </summary>
    IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
        object? request = null);
}
