using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk;

/// <summary>
///     Base context available to all handler types.
///     Provides durable execution primitives: side effects, calls, timers, and awakeables.
///     Subclass this to create test doubles (see Restate.Sdk.Testing).
/// </summary>
/// <remarks>
///     A context and the durable futures it hands out must not be used from more than one thread
///     at a time: concurrent context operations reorder the journal and break replay.
///     <para>
///         <c>Task.WhenAny</c> and <c>Task.WhenAll</c> over durable futures bypass the delivery
///         ordering the combinators provide, so compose futures with <see cref="All{T}" />,
///         <see cref="Race{T}" />, <see cref="Any{T}" />, <see cref="AllSettled{T}" /> or
///         <see cref="WaitAll" />.
///     </para>
/// </remarks>
public abstract class Context : IContext
{
    /// <summary>Replay-safe random number generator seeded by the invocation.</summary>
    public abstract DurableRandom Random { get; }

    /// <summary>Console that is silent during replay to avoid duplicate output.</summary>
    public abstract DurableConsole Console { get; }

    /// <summary>
    ///     Logger scoped to the current invocation. Output is suppressed while the invocation
    ///     replays journal entries, so re-executed handler code does not log duplicate lines.
    ///     Defaults to <see cref="NullLogger.Instance" /> for custom test doubles;
    ///     the SDK-provided contexts override it with a replay-aware logger.
    /// </summary>
    public virtual ILogger Logger => NullLogger.Instance;

    /// <summary>Request headers from the original invocation.</summary>
    public abstract IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Unique identifier of the current invocation.</summary>
    public abstract string InvocationId { get; }

    /// <summary>Cancellation token that fires when the invocation is aborted.</summary>
    public abstract CancellationToken Aborted { get; }

    /// <summary>Executes an async side effect durably. The result is journaled and replayed on retries.</summary>
    /// <remarks>
    ///     The result is the object the action returned on the attempt that ran it, and a copy
    ///     deserialized from the journal on every later attempt, so <typeparamref name="T" /> must
    ///     round-trip through the configured serializer and callers must not rely on reference
    ///     identity.
    /// </remarks>
    public abstract ValueTask<T> Run<T>(string name, Func<Task<T>> action);

    /// <summary>Executes an async void side effect durably.</summary>
    public abstract ValueTask Run(string name, Func<Task> action);

    /// <summary>Executes a synchronous side effect durably. The result is journaled and replayed on retries.</summary>
    /// <remarks>
    ///     The result is the object the action returned on the attempt that ran it, and a copy
    ///     deserialized from the journal on every later attempt, so <typeparamref name="T" /> must
    ///     round-trip through the configured serializer and callers must not rely on reference
    ///     identity.
    /// </remarks>
    public abstract ValueTask<T> Run<T>(string name, Func<T> action);

    /// <summary>Executes an async side effect with a restricted run context. Prevents nested Restate operations.</summary>
    /// <remarks>
    ///     The result is the object the action returned on the attempt that ran it, and a copy
    ///     deserialized from the journal on every later attempt, so <typeparamref name="T" /> must
    ///     round-trip through the configured serializer and callers must not rely on reference
    ///     identity.
    /// </remarks>
    public abstract ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action);

    /// <summary>Executes an async void side effect with a restricted run context.</summary>
    public abstract ValueTask Run(string name, Func<IRunContext, Task> action);

    /// <summary>
    ///     Executes an async side effect durably with a custom retry policy.
    ///     The SDK retries locally with exponential backoff before propagating failures.
    /// </summary>
    /// <remarks>
    ///     The result is the object the action returned on the attempt that ran it, and a copy
    ///     deserialized from the journal on every later attempt, so <typeparamref name="T" /> must
    ///     round-trip through the configured serializer and callers must not rely on reference
    ///     identity.
    /// </remarks>
    public abstract ValueTask<T> Run<T>(string name, Func<Task<T>> action, RetryPolicy retryPolicy);

    /// <summary>
    ///     Executes an async void side effect durably with a custom retry policy.
    /// </summary>
    public abstract ValueTask Run(string name, Func<Task> action, RetryPolicy retryPolicy);

    /// <summary>
    ///     Executes a synchronous side effect durably with a custom retry policy.
    ///     The SDK retries locally with exponential backoff before propagating failures.
    /// </summary>
    /// <remarks>
    ///     The result is the object the action returned on the attempt that ran it, and a copy
    ///     deserialized from the journal on every later attempt, so <typeparamref name="T" /> must
    ///     round-trip through the configured serializer and callers must not rely on reference
    ///     identity.
    /// </remarks>
    public abstract ValueTask<T> Run<T>(string name, Func<T> action, RetryPolicy retryPolicy);

    /// <summary>Calls a handler on a stateless service and awaits its response.</summary>
    public abstract ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null);

    /// <summary>Calls a handler on a keyed virtual object or workflow and awaits its response.</summary>
    public abstract ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
        object? request = null);

    /// <summary>
    ///     Calls a handler on a stateless service with call options (e.g., idempotency key).
    /// </summary>
    public abstract ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request,
        CallOptions options);

    /// <summary>
    ///     Calls a handler on a keyed virtual object or workflow with call options.
    /// </summary>
    public abstract ValueTask<TResponse> Call<TResponse>(string service, string key, string handler, object? request,
        CallOptions options);

    /// <summary>
    ///     Calls a handler with typed request/response serialization and call options
    ///     (idempotency key, scope, limit key).
    /// </summary>
    public abstract ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler, TRequest request,
        string? key, CallOptions options);

    /// <summary>
    ///     Cancels a running invocation by sending a cancel signal.
    ///     The target invocation will be aborted with a cancellation error.
    /// </summary>
    public abstract ValueTask CancelInvocation(string invocationId);

    /// <summary>Sends a one-way invocation to a stateless service. Returns a handle to track the invocation.</summary>
    public abstract ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>Sends a one-way invocation to a keyed virtual object or workflow. Returns a handle to track the invocation.</summary>
    public abstract ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>
    ///     Sends a one-way invocation to a stateless service with send options
    ///     (delay, idempotency key, scope, limit key).
    /// </summary>
    public abstract ValueTask<InvocationHandle> Send(string service, string handler, object? request,
        SendOptions options);

    /// <summary>
    ///     Sends a one-way invocation to a keyed virtual object or workflow with send options.
    /// </summary>
    public abstract ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request,
        SendOptions options);

    /// <summary>Gets a source-generated typed call client for a stateless service.</summary>
    public abstract TClient ServiceClient<TClient>() where TClient : class;

    /// <summary>Gets a source-generated typed call client for a virtual object, bound to the given key.</summary>
    public abstract TClient ObjectClient<TClient>(string key) where TClient : class;

    /// <summary>Gets a source-generated typed call client for a workflow, bound to the given key.</summary>
    public abstract TClient WorkflowClient<TClient>(string key) where TClient : class;

    /// <summary>Gets a source-generated typed send client for a stateless service.</summary>
    public abstract TClient ServiceSendClient<TClient>(SendOptions? options = null) where TClient : class;

    /// <summary>Gets a source-generated typed send client for a virtual object, bound to the given key.</summary>
    public abstract TClient ObjectSendClient<TClient>(string key, SendOptions? options = null) where TClient : class;

    /// <summary>Gets a source-generated typed send client for a workflow, bound to the given key.</summary>
    public abstract TClient WorkflowSendClient<TClient>(string key, SendOptions? options = null) where TClient : class;

    /// <summary>Suspends execution for the specified duration. Durable — survives process restarts.</summary>
    public abstract ValueTask Sleep(TimeSpan duration);

    /// <summary>Creates a durable promise that can be resolved from outside the current invocation.</summary>
    public abstract Awakeable<T> Awakeable<T>(ISerde<T>? serde = null);

    /// <summary>
    ///     Awaits a signal by name. Another handler holding this invocation handle resolves or
    ///     rejects the signal by that name, and the returned future composes with
    ///     <see cref="All{T}" />, <see cref="Race{T}" />, <see cref="Any{T}" /> and
    ///     <see cref="AllSettled{T}" /> like any other durable future.
    ///     A signal resolved before this attempt started resolves from the journal on replay.
    ///     The ingress addresses signals by awakeable id only, so a named signal has no ingress
    ///     route — use <see cref="Awakeable{T}" /> when an external system must complete it.
    /// </summary>
    /// <param name="name">The signal name the resolver addresses. Must not be empty.</param>
    public abstract IDurableFuture<T> Signal<T>(string name);

    /// <summary>
    ///     Awaits an unnamed signal, addressed by the index this call allocates: the returned
    ///     <see cref="SignalHandle{T}" /> carries that index alongside the future. Indices below
    ///     the first user index are reserved for built-in signals, and they come from the same
    ///     allocator <see cref="Awakeable{T}" /> uses — an awakeable is an unnamed signal plus the
    ///     opaque id that addresses it, so use <see cref="Awakeable{T}" /> when the resolver needs
    ///     an id to carry around, and this when the resolver addresses the invocation handle and
    ///     <see cref="SignalHandle{T}.Index" />.
    /// </summary>
    public abstract SignalHandle<T> Signal<T>();

    /// <summary>
    ///     Resolves a named signal on another invocation. The awaiting handler receives
    ///     <paramref name="value" /> from its <see cref="Signal{T}(string)" /> future.
    /// </summary>
    /// <param name="invocationId">The invocation to signal, from an <see cref="InvocationHandle" />.</param>
    /// <param name="name">The signal name the target handler awaits.</param>
    /// <param name="value">The value the signal resolves with.</param>
    public abstract ValueTask ResolveSignal<T>(string invocationId, string name, T value);

    /// <summary>
    ///     Resolves an unnamed signal on another invocation, addressed by its index
    ///     (see <see cref="Signal{T}()" />).
    /// </summary>
    public abstract ValueTask ResolveSignal<T>(string invocationId, int signalIndex, T value);

    /// <summary>
    ///     Rejects a named signal on another invocation: the awaiting handler's future fails with
    ///     a <see cref="TerminalException" /> carrying <paramref name="reason" />.
    /// </summary>
    public abstract ValueTask RejectSignal(string invocationId, string name, string reason);

    /// <summary>
    ///     Rejects an unnamed signal on another invocation, addressed by its index.
    /// </summary>
    public abstract ValueTask RejectSignal(string invocationId, int signalIndex, string reason);

    /// <summary>Resolves a previously created awakeable with a payload.</summary>
    public abstract void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null);

    /// <summary>Rejects a previously created awakeable with a failure reason.</summary>
    public abstract void RejectAwakeable(string id, string reason);

    /// <summary>Returns the current time, durably journaled for replay safety.</summary>
    public abstract ValueTask<DateTimeOffset> Now();

    /// <summary>Attaches to a running invocation and awaits its result.</summary>
    public abstract ValueTask<T> Attach<T>(string invocationId);

    /// <summary>Gets the output of a completed invocation, or default if not yet completed.</summary>
    public abstract ValueTask<T?> GetOutput<T>(string invocationId);

    /// <summary>
    ///     Calls a handler by service and handler name with typed request/response serialization.
    ///     Use this when you don't have generated typed clients.
    /// </summary>
    public abstract ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler,
        TRequest request, string? key = null);

    /// <summary>
    ///     Sends a one-way invocation by service and handler name with typed request serialization.
    ///     Returns a handle to track the invocation.
    /// </summary>
    public abstract ValueTask<InvocationHandle> Send<TRequest>(string service, string handler,
        TRequest request, string? key = null, SendOptions? options = null);

    /// <summary>
    ///     Executes an async side effect and returns a non-blocking future.
    ///     The action runs immediately but the returned future resolves when the server acknowledges the result.
    ///     Use with <see cref="All{T}" /> or <see cref="Race{T}" /> for concurrent task patterns.
    /// </summary>
    /// <remarks>
    ///     The result is the object the action returned on the attempt that ran it, and a copy
    ///     deserialized from the journal on every later attempt, so <typeparamref name="T" /> must
    ///     round-trip through the configured serializer and callers must not rely on reference
    ///     identity.
    /// </remarks>
    public abstract IDurableFuture<T> RunAsync<T>(string name, Func<Task<T>> action);

    /// <summary>
    ///     Creates a non-blocking durable timer that completes after the specified duration.
    ///     Unlike <see cref="Sleep" /> which blocks, this returns a future for use with combinators.
    /// </summary>
    public abstract IDurableFuture Timer(TimeSpan duration);

    /// <summary>
    ///     Calls a handler and returns a non-blocking future instead of awaiting the result.
    ///     Use with combinators for concurrent fan-out patterns.
    /// </summary>
    public abstract IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
        object? request = null);

    /// <summary>
    ///     Calls a handler on a keyed virtual object or workflow and returns a non-blocking future.
    /// </summary>
    public abstract IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
        object? request = null);

    /// <summary>
    ///     Calls a handler with call options (idempotency key, scope, limit key) and returns a
    ///     non-blocking future.
    /// </summary>
    public abstract IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler, object? request,
        CallOptions options);

    /// <summary>
    ///     Calls a handler on a keyed virtual object or workflow with call options and returns a
    ///     non-blocking future.
    /// </summary>
    public abstract IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
        object? request, CallOptions options);

    /// <summary>
    ///     Awaits all futures and returns their results in order.
    ///     If any future fails, the first failure in delivery order is thrown as soon as it is
    ///     delivered, without waiting for the futures still pending; the operations behind them
    ///     stay journaled and complete there.
    ///     <para>
    ///         Inputs are deserialized lazily: each one is decoded as the runtime's delivery for
    ///         it is taken, in delivery order, so the decoding order is the one the journal
    ///         recorded rather than the one the thread pool happens to produce.
    ///     </para>
    ///     <para>
    ///         A future pending when the invocation suspends is not a failure — the suspension
    ///         propagates instead, unless another future was delivered a failure, which is thrown
    ///         in its place because the journal replays it on the next attempt.
    ///     </para>
    /// </summary>
    public virtual ValueTask<T[]> All<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
    {
        return AwaitAll(futures.ToArray());

        static async ValueTask<T[]> AwaitAll(IDurableFuture<T>[] inputs)
        {
            var completions = CompletionsOf(inputs);
            var taken = new bool[inputs.Length];
            var results = new T[inputs.Length];
            SuspensionException? suspension = null;

            for (var remaining = inputs.Length; remaining > 0; remaining--)
            {
                var next = await TakeNextSettled(inputs, completions, taken).ConfigureAwait(false);
                if (completions[next].Failure is { } delivered)
                {
                    // A poisoned input is the invocation suspending, not a failed operation, and
                    // it must not mask a failure another input was delivered: hold it and keep
                    // taking the inputs that settled, then suspend if none of them failed.
                    if (FindSuspension(delivered) is { } poisoned)
                    {
                        suspension ??= poisoned;
                        continue;
                    }

                    ExceptionDispatchInfo.Capture(delivered).Throw();
                }

                results[next] = await inputs[next].GetResult().ConfigureAwait(false);
            }

            if (suspension is not null)
                throw suspension;

            return results;
        }
    }

    /// <summary>
    ///     Returns the result of the future the runtime delivered a result to first.
    ///     <para>
    ///         Only the winner is deserialized: a losing branch is left as the runtime delivered
    ///         it, so its payload is never decoded and a payload that cannot be decoded cannot
    ///         fail the invocation. The winner is chosen by delivery order, which makes a
    ///         replayed attempt take the branch the original attempt took.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException">No futures were provided.</exception>
    public virtual ValueTask<T> Race<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
    {
        if (futures.Length == 0)
            throw new ArgumentException("No futures provided.", nameof(futures));

        return AwaitRace(futures.ToArray());

        static async ValueTask<T> AwaitRace(IDurableFuture<T>[] inputs)
        {
            var completions = CompletionsOf(inputs);
            var winner = await TakeNextSettled(inputs, completions, new bool[inputs.Length]).ConfigureAwait(false);
            return await inputs[winner].GetResult().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Returns the result of the first future the runtime delivered a <em>successful</em>
    ///     result to. Failed futures are ignored unless every future fails, in which case an
    ///     <see cref="AggregateException" /> containing all failures is thrown
    ///     (matching the TypeScript SDK's <c>RestatePromise.any</c> / JavaScript <c>AggregateError</c> semantics).
    ///     <para>
    ///         Inputs are inspected in delivery order and only the input handed back is
    ///         deserialized: a failed branch is read from the delivery itself, without decoding a
    ///         payload, and a replayed attempt takes the branch the original attempt took.
    ///     </para>
    /// </summary>
    /// <exception cref="AggregateException">
    ///     Every future failed (or none were provided); contains each failure as an inner exception,
    ///     in input order. Futures canceled by an invocation abort count as failures and surface as
    ///     <see cref="TaskCanceledException" />. Futures pending when the invocation suspends are
    ///     never settled as failures — the suspension propagates instead. A payload that cannot be
    ///     deserialized is not a failed branch either: that error propagates so the attempt is retried.
    /// </exception>
    public virtual ValueTask<T> Any<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
    {
        return AwaitAny(futures.ToArray());

        static async ValueTask<T> AwaitAny(IDurableFuture<T>[] inputs)
        {
            var completions = CompletionsOf(inputs);
            var taken = new bool[inputs.Length];

            // Indexed by input, so the aggregate below lists failures in input order, matching
            // JavaScript's AggregateError.errors ordering.
            var errors = new Exception[inputs.Length];

            for (var remaining = inputs.Length; remaining > 0; remaining--)
            {
                var next = await TakeNextSettled(inputs, completions, taken).ConfigureAwait(false);
                if (completions[next].Failure is { } delivered)
                {
                    errors[next] = delivered;
                    continue;
                }

                try
                {
                    return await inputs[next].GetResult().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsBranchFailure(ex))
                {
                    errors[next] = ex;
                }
            }

            // Every input has settled without a success. Suspension is not a failure (see
            // FindSuspension) and must win over the aggregate.
            foreach (var error in errors)
                if (FindSuspension(error) is { } suspension)
                    throw suspension;

            throw new AggregateException("All durable futures failed.", errors);
        }
    }

    /// <summary>
    ///     Awaits all futures and returns a settlement result for each one, in input order.
    ///     Unlike <see cref="All{T}" />, individual failures do not throw — inspect
    ///     <see cref="SettledResult{T}.IsSuccess" /> per item
    ///     (matching the TypeScript SDK's <c>RestatePromise.allSettled</c> semantics).
    ///     Futures canceled by an invocation abort settle as failures carrying a
    ///     <see cref="TaskCanceledException" />. Futures pending when the invocation suspends are
    ///     never settled as failures — the operations are journaled and WILL complete on a later
    ///     attempt, so the suspension propagates instead of fabricating failure entries. A payload
    ///     that cannot be deserialized is not a failure entry either: that error propagates so the
    ///     attempt is retried.
    ///     <para>
    ///         Inputs are deserialized lazily, each one as the runtime's delivery for it is taken,
    ///         in delivery order; a failed branch is read from the delivery itself, without
    ///         decoding a payload.
    ///     </para>
    /// </summary>
    public virtual ValueTask<SettledResult<T>[]> AllSettled<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
    {
        return AwaitAllSettled(futures.ToArray());

        static async ValueTask<SettledResult<T>[]> AwaitAllSettled(IDurableFuture<T>[] inputs)
        {
            var completions = CompletionsOf(inputs);
            var taken = new bool[inputs.Length];
            var results = new SettledResult<T>[inputs.Length];

            for (var remaining = inputs.Length; remaining > 0; remaining--)
            {
                var next = await TakeNextSettled(inputs, completions, taken).ConfigureAwait(false);
                if (completions[next].Failure is { } delivered)
                {
                    if (FindSuspension(delivered) is { } suspension)
                        throw suspension;
                    results[next] = SettledResult<T>.Failure(delivered);
                    continue;
                }

                try
                {
                    results[next] = SettledResult<T>.Success(
                        await inputs[next].GetResult().ConfigureAwait(false));
                }
                catch (Exception ex) when (IsBranchFailure(ex))
                {
                    results[next] = SettledResult<T>.Failure(ex);
                }
            }

            return results;
        }
    }

    /// <summary>
    ///     Yields futures as the runtime delivers their results, in delivery order.
    ///     Each element contains the completed future and an optional error.
    ///     <para>
    ///         Inputs are deserialized lazily, each one as it is yielded; a failed branch is read
    ///         from the delivery itself, without decoding a payload. Delivery order is the order
    ///         the journal recorded, so a replayed attempt yields the futures in the order the
    ///         original attempt saw them.
    ///     </para>
    /// </summary>
    public virtual async IAsyncEnumerable<(IDurableFuture future, Exception? error)> WaitAll(
        params IDurableFuture[] futures)
    {
        var completions = CompletionsOf(futures);
        var taken = new bool[futures.Length];

        for (var remaining = futures.Length; remaining > 0; remaining--)
        {
            var next = await TakeNextSettled(futures, completions, taken).ConfigureAwait(false);
            var error = completions[next].Failure;
            if (error is null)
                try
                {
                    await futures[next].GetResult().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsBranchFailure(ex))
                {
                    error = ex;
                }

            if (FindSuspension(error) is { } suspension)
                throw suspension;

            yield return (futures[next], error);
        }
    }

    /// <summary>
    ///     The runtime's delivery for each input: the wait a combinator parks on and ranks
    ///     without deserializing anything. A future the SDK did not create exposes no delivery,
    ///     so its decoded result stands in for one — such an input is deserialized even when the
    ///     combinator does not hand it back.
    /// </summary>
    private static RawCompletion[] CompletionsOf<TFuture>(TFuture[] futures)
        where TFuture : class, IDurableFuture
    {
        var completions = new RawCompletion[futures.Length];
        for (var i = 0; i < futures.Length; i++)
            completions[i] = futures[i] is IArrivalOrderedFuture ordered
                ? ordered.Completion
                : RawCompletion.OfWait(futures[i].GetResult().AsTask());

        return completions;
    }

    /// <summary>
    ///     Waits until one of the inputs that has not been taken yet has settled, marks it taken
    ///     and returns its index. The settled inputs are ranked afresh at every wake-up, so the
    ///     combinator takes them in the order the runtime delivered their results — the order the
    ///     journal recorded — rather than in the order the thread pool finishes deserializing
    ///     them in, and the same journal replays to the same branch.
    /// </summary>
    private static async ValueTask<int> TakeNextSettled<TFuture>(TFuture[] futures,
        RawCompletion[] completions, bool[] taken)
        where TFuture : class, IDurableFuture
    {
        while (true)
        {
            var next = EarliestSettled(futures, completions, taken);
            if (next >= 0)
            {
                taken[next] = true;
                return next;
            }

            await WhenAnySettles(completions, taken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The index of the input to take next, or -1 while none of the untaken inputs has
    ///     settled. Inputs the runtime delivered a result to are ranked among themselves by the
    ///     position it was delivered at; an input with no delivered result — resolved locally by
    ///     a re-executed Run, or replayed as an already-known value — keeps its argument
    ///     position, exactly where the caller put it.
    /// </summary>
    private static int EarliestSettled<TFuture>(TFuture[] futures, RawCompletion[] completions, bool[] taken)
        where TFuture : class, IDurableFuture
    {
        var first = -1;
        var firstArrival = 0L;
        var earliest = -1;
        var earliestArrival = 0L;

        for (var i = 0; i < futures.Length; i++)
        {
            if (taken[i] || !completions[i].IsSettled)
                continue;

            var arrival = ArrivalOrderOf(futures[i]);
            if (first < 0)
            {
                first = i;
                firstArrival = arrival;
            }

            if (arrival > 0 && (earliest < 0 || arrival < earliestArrival))
            {
                earliest = i;
                earliestArrival = arrival;
            }
        }

        return firstArrival > 0 ? earliest : first;
    }

    /// <summary>
    ///     A wait that completes as soon as one of the untaken inputs settles. The deliveries
    ///     never fault, so nothing is thrown here and no abandoned input is left unobserved.
    /// </summary>
    private static Task WhenAnySettles(RawCompletion[] completions, bool[] taken)
    {
        var pending = new List<Task>(completions.Length);
        for (var i = 0; i < completions.Length; i++)
            if (!taken[i])
                pending.Add(completions[i].Settled);

        return Task.WhenAny(pending);
    }

    private static long ArrivalOrderOf(IDurableFuture future)
    {
        return future is IArrivalOrderedFuture ordered ? ordered.ArrivalOrder : 0;
    }

    /// <summary>
    ///     Whether deserializing the branch a combinator took failed in a way that settles that
    ///     branch rather than the attempt: a <see cref="TerminalException" /> the serializer
    ///     raised is the branch's own failure and a cancellation is the invocation unwinding,
    ///     while any other error — a malformed payload, a broken converter — is a bug the runtime
    ///     must retry, so it propagates and fails the attempt.
    /// </summary>
    private static bool IsBranchFailure(Exception exception)
    {
        return exception is TerminalException or OperationCanceledException;
    }

    /// <summary>Finds a <see cref="SuspensionException" /> in an exception tree, unwrapping aggregates.</summary>
    private static SuspensionException? FindSuspension(Exception? exception)
    {
        switch (exception)
        {
            case SuspensionException suspension:
                return suspension;
            case AggregateException aggregate:
                foreach (var inner in aggregate.InnerExceptions)
                    if (FindSuspension(inner) is { } found)
                        return found;
                return null;
            default:
                return null;
        }
    }
}