using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Restate.Sdk;

/// <summary>
///     Interface exposing durable execution primitives available to all handler types.
///     Use this for utility methods, type constraints, and testing with mocking frameworks.
///     Handlers receive the abstract <see cref="Context" /> class; use Mock*Context classes for handler testing.
/// </summary>
public interface IContext
{
    /// <summary>
    ///     Reported by the default implementations of the options overloads below. They exist so
    ///     that adding an overload does not break external implementations of this interface
    ///     (mirroring <see cref="Logger" />); an implementation that has not overridden them
    ///     cannot honour a scope or limit key, and says so instead of dropping it.
    /// </summary>
    private const string OptionsNotSupported =
        "This IContext implementation does not support call or send options; override the overload that takes them.";

    /// <summary>Reported by the default implementations of the signal overloads below.</summary>
    private const string SignalsNotSupported =
        "This IContext implementation does not support signals; override the Signal overloads.";

    /// <summary>Unique identifier of the current invocation.</summary>
    string InvocationId { get; }

    /// <summary>
    ///     Logger scoped to the current invocation. Output is suppressed while the invocation
    ///     replays journal entries, so re-executed handler code does not log duplicate lines.
    ///     Defaults to <see cref="NullLogger.Instance" /> so external <see cref="IContext" />
    ///     implementations keep compiling — mirroring <see cref="Context.Logger" />.
    /// </summary>
    ILogger Logger => NullLogger.Instance;

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
    ///     Calls a handler with typed request/response serialization and call options
    ///     (idempotency key, scope, limit key). Defaults to the option-less overload, so external
    ///     implementations keep compiling; an implementation that cannot honour the options
    ///     reports that rather than dropping them.
    /// </summary>
    ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler, TRequest request, string? key,
        CallOptions options) =>
        options == default
            ? Call<TRequest, TResponse>(service, handler, request, key)
            : throw new NotSupportedException(OptionsNotSupported);

    /// <summary>
    ///     Cancels a running invocation by sending a cancel signal.
    ///     The target invocation will be aborted with a cancellation error.
    /// </summary>
    ValueTask CancelInvocation(string invocationId);

    /// <summary>Sends a one-way invocation to a stateless service. Returns a handle to track the invocation.</summary>
    ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>Sends a one-way invocation to a keyed virtual object or workflow. Returns a handle to track the invocation.</summary>
    ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>
    ///     Sends a one-way invocation with send options (delay, idempotency key, scope, limit key).
    /// </summary>
    ValueTask<InvocationHandle> Send(string service, string handler, object? request, SendOptions options) =>
        options.Scope is null && options.LimitKey is null
            ? Send(service, handler, request, options.Delay, options.IdempotencyKey)
            : throw new NotSupportedException(OptionsNotSupported);

    /// <summary>
    ///     Sends a one-way invocation to a keyed virtual object or workflow with send options.
    /// </summary>
    ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request,
        SendOptions options) =>
        options.Scope is null && options.LimitKey is null
            ? Send(service, key, handler, request, options.Delay, options.IdempotencyKey)
            : throw new NotSupportedException(OptionsNotSupported);

    /// <summary>Suspends execution for the specified duration. Durable — survives process restarts.</summary>
    ValueTask Sleep(TimeSpan duration);

    /// <summary>Creates a durable promise that can be resolved from outside the current invocation.</summary>
    Awakeable<T> Awakeable<T>(ISerde<T>? serde = null);

    /// <summary>
    ///     Awaits a signal by name, resolved or rejected by whoever holds this invocation handle.
    ///     Defaulted so that adding it does not break external implementations of this interface
    ///     (mirroring <see cref="Logger" />).
    /// </summary>
    IDurableFuture<T> Signal<T>(string name) => throw new NotSupportedException(SignalsNotSupported);

    /// <summary>
    ///     Awaits an unnamed signal, addressed by the index this call allocates.
    ///     Defaulted for the same reason as <see cref="Signal{T}(string)" />.
    /// </summary>
    IDurableFuture<T> Signal<T>() => throw new NotSupportedException(SignalsNotSupported);

    /// <summary>
    ///     Resolves a named signal on another invocation. Defaulted so that adding it does not
    ///     break external implementations of this interface (mirroring <see cref="Logger" />).
    /// </summary>
    ValueTask ResolveSignal<T>(string invocationId, string name, T value) =>
        throw new NotSupportedException(SignalsNotSupported);

    /// <summary>Resolves an unnamed signal on another invocation, addressed by its index.</summary>
    ValueTask ResolveSignal<T>(string invocationId, int signalIndex, T value) =>
        throw new NotSupportedException(SignalsNotSupported);

    /// <summary>Rejects a named signal on another invocation.</summary>
    ValueTask RejectSignal(string invocationId, string name, string reason) =>
        throw new NotSupportedException(SignalsNotSupported);

    /// <summary>Rejects an unnamed signal on another invocation, addressed by its index.</summary>
    ValueTask RejectSignal(string invocationId, int signalIndex, string reason) =>
        throw new NotSupportedException(SignalsNotSupported);

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

    /// <summary>
    ///     Calls a handler with call options (idempotency key, scope, limit key) and returns a
    ///     non-blocking future.
    /// </summary>
    IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler, object? request,
        CallOptions options) =>
        options == default
            ? CallFuture<TResponse>(service, handler, request)
            : throw new NotSupportedException(OptionsNotSupported);

    /// <summary>
    ///     Calls a handler on a keyed virtual object or workflow with call options and returns a
    ///     non-blocking future.
    /// </summary>
    IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler, object? request,
        CallOptions options) =>
        options == default
            ? CallFuture<TResponse>(service, key, handler, request)
            : throw new NotSupportedException(OptionsNotSupported);
}
