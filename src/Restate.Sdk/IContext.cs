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
    ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action);

    /// <summary>Executes an async void side effect with a restricted run context.</summary>
    ValueTask Run(string name, Func<IRunContext, Task> action);

    /// <summary>
    ///     Executes an async side effect durably with a custom retry policy.
    ///     The SDK retries locally with exponential backoff before propagating failures.
    /// </summary>
    ValueTask<T> Run<T>(string name, Func<Task<T>> action, RetryPolicy retryPolicy);

    /// <summary>
    ///     Executes an async void side effect durably with a custom retry policy.
    /// </summary>
    ValueTask Run(string name, Func<Task> action, RetryPolicy retryPolicy);

    /// <summary>
    ///     Executes a synchronous side effect durably with a custom retry policy.
    ///     The SDK retries locally with exponential backoff before propagating failures.
    /// </summary>
    ValueTask<T> Run<T>(string name, Func<T> action, RetryPolicy retryPolicy);

    /// <summary>Calls a handler on a stateless service and awaits its response.</summary>
    ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null);

    /// <summary>Calls a handler on a keyed virtual object or workflow and awaits its response.</summary>
    ValueTask<TResponse> Call<TResponse>(string service, string key, string handler, object? request = null);

    /// <summary>
    ///     Calls a handler on a stateless service with call options (e.g., idempotency key).
    /// </summary>
    ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request, CallOptions options);

    /// <summary>
    ///     Calls a handler on a keyed virtual object or workflow with call options.
    /// </summary>
    ValueTask<TResponse> Call<TResponse>(string service, string key, string handler, object? request,
        CallOptions options);

    /// <summary>
    ///     Calls a handler and returns a <c>CallHandle&lt;TResponse&gt;</c> that exposes BOTH the
    ///     response and the callee's invocation id (shared-core get_call_invocation_id). Use this when
    ///     the handler needs the child's invocation id — e.g. to cancel or attach to it — in addition
    ///     to awaiting the result.
    /// </summary>
    CallHandle<TResponse> CallHandle<TResponse>(string service, string handler, object? request = null,
        CallOptions? options = null);

    /// <summary>
    ///     Calls a handler on a keyed virtual object or workflow and returns a
    ///     <c>CallHandle&lt;TResponse&gt;</c> exposing both the response and the callee's invocation id.
    /// </summary>
    CallHandle<TResponse> CallHandle<TResponse>(string service, string key, string handler, object? request,
        CallOptions? options = null);

    /// <summary>
    ///     Cancels a running invocation by sending a cancel signal.
    ///     The target invocation will be aborted with a cancellation error.
    /// </summary>
    ValueTask CancelInvocation(string invocationId);

    /// <summary>
    ///     Sends a named signal carrying a payload to a target invocation. The target resolves a
    ///     matching <see cref="NamedSignal{T}" /> awaiting the same <paramref name="name" />.
    /// </summary>
    ValueTask SendSignal<T>(string targetInvocationId, string name, T payload, ISerde<T>? serde = null);

    /// <summary>
    ///     Sends a named signal carrying a terminal failure to a target invocation. A matching
    ///     <see cref="NamedSignal{T}" /> await on the target throws a <see cref="TerminalException" />.
    /// </summary>
    ValueTask SendSignalFailure(string targetInvocationId, string name, string reason);

    /// <summary>
    ///     Sends a named signal carrying a terminal failure with a custom Restate/HTTP error
    ///     <paramref name="code" /> (G29). Shared-core's <c>sys_complete_signal</c> failure carries an
    ///     arbitrary <c>TerminalFailure.code</c> (lib.rs:191-194); the no-code overload uses 500. The
    ///     default forwards to <see cref="SendSignalFailure(string, string, string)" /> (code dropped) so
    ///     existing <see cref="IContext" /> implementors need no change.
    /// </summary>
    ValueTask SendSignalFailure(string targetInvocationId, string name, string reason, ushort code) =>
        SendSignalFailure(targetInvocationId, name, reason);

    /// <summary>Sends a one-way invocation to a stateless service. Returns a handle to track the invocation.</summary>
    ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>Sends a one-way invocation to a keyed virtual object or workflow. Returns a handle to track the invocation.</summary>
    ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>
    ///     Sends a one-way invocation with full <see cref="SendOptions" /> (delay, idempotency key, and
    ///     custom headers).
    /// </summary>
    ValueTask<InvocationHandle> Send(string service, string handler, object? request, SendOptions options);

    /// <summary>
    ///     Sends a one-way invocation to a keyed virtual object or workflow with full
    ///     <see cref="SendOptions" /> (delay, idempotency key, and custom headers).
    /// </summary>
    ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request, SendOptions options);

    /// <summary>Suspends execution for the specified duration. Durable — survives process restarts.</summary>
    ValueTask Sleep(TimeSpan duration);

    /// <summary>Creates a durable promise that can be resolved from outside the current invocation.</summary>
    Awakeable<T> Awakeable<T>(ISerde<T>? serde = null);

    /// <summary>
    ///     Creates a durable promise keyed by <paramref name="name" />, resolved when another
    ///     invocation sends a matching named signal via <see cref="SendSignal{T}" />.
    /// </summary>
    NamedSignal<T> NamedSignal<T>(string name, ISerde<T>? serde = null);

    /// <summary>Resolves a previously created awakeable with a payload.</summary>
    void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null);

    /// <summary>Rejects a previously created awakeable with a failure reason.</summary>
    void RejectAwakeable(string id, string reason);

    /// <summary>
    ///     Rejects a previously created awakeable with a failure reason and a custom Restate/HTTP error
    ///     <paramref name="code" /> (G28). The default forwards to <see cref="RejectAwakeable(string,
    ///     string)" /> (code dropped) so existing <see cref="IContext" /> implementors need no change.
    /// </summary>
    void RejectAwakeable(string id, string reason, ushort code) => RejectAwakeable(id, reason);

    /// <summary>Returns the current time, durably journaled for replay safety.</summary>
    ValueTask<DateTimeOffset> Now();

    /// <summary>Attaches to a running invocation and awaits its result.</summary>
    ValueTask<T> Attach<T>(string invocationId);

    /// <summary>
    ///     Attaches to a running invocation identified by an <see cref="AttachTarget" /> (invocation
    ///     id, workflow id, or idempotency id) and awaits its result.
    /// </summary>
    ValueTask<T> Attach<T>(AttachTarget target);

    /// <summary>Gets the output of a completed invocation, or default if not yet completed.</summary>
    ValueTask<T?> GetOutput<T>(string invocationId);

    /// <summary>
    ///     Gets the output of a completed invocation identified by an <see cref="AttachTarget" />
    ///     (invocation id, workflow id, or idempotency id), or default if not yet completed.
    /// </summary>
    ValueTask<T?> GetOutput<T>(AttachTarget target);

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
