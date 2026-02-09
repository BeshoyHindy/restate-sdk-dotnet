namespace Restate.Sdk;

/// <summary>
///     Base context available to all handler types.
///     Provides durable execution primitives: side effects, calls, timers, and awakeables.
///     Subclass this to create test doubles (see Restate.Sdk.Testing).
/// </summary>
public abstract class Context : IContext
{
    /// <summary>Replay-safe random number generator seeded by the invocation.</summary>
    public abstract DurableRandom Random { get; }

    /// <summary>Console that is silent during replay to avoid duplicate output.</summary>
    public abstract DurableConsole Console { get; }

    /// <summary>Request headers from the original invocation.</summary>
    public abstract IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Unique identifier of the current invocation.</summary>
    public abstract string InvocationId { get; }

    /// <summary>Cancellation token that fires when the invocation is aborted.</summary>
    public abstract CancellationToken Aborted { get; }

    /// <summary>Executes an async side effect durably. The result is journaled and replayed on retries.</summary>
    public abstract ValueTask<T> Run<T>(string name, Func<Task<T>> action);

    /// <summary>Executes an async void side effect durably.</summary>
    public abstract ValueTask Run(string name, Func<Task> action);

    /// <summary>Executes a synchronous side effect durably. The result is journaled and replayed on retries.</summary>
    public abstract ValueTask<T> Run<T>(string name, Func<T> action);

    /// <summary>Executes an async side effect with a restricted run context. Prevents nested Restate operations.</summary>
    public abstract ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action, RunOptions? options = null);

    /// <summary>Executes an async void side effect with a restricted run context.</summary>
    public abstract ValueTask Run(string name, Func<IRunContext, Task> action, RunOptions? options = null);

    /// <summary>Calls a handler on a stateless service and awaits its response.</summary>
    public abstract ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null);

    /// <summary>Calls a handler on a keyed virtual object or workflow and awaits its response.</summary>
    public abstract ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
        object? request = null);

    /// <summary>Sends a one-way invocation to a stateless service. Returns a handle to track the invocation.</summary>
    public abstract ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

    /// <summary>Sends a one-way invocation to a keyed virtual object or workflow. Returns a handle to track the invocation.</summary>
    public abstract ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null);

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
    ///     Awaits all futures and returns their results in order.
    ///     If any future fails, the first failure is thrown.
    /// </summary>
    public virtual ValueTask<T[]> All<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
    {
        var tasks = new Task<T>[futures.Length];
        for (var i = 0; i < futures.Length; i++)
            tasks[i] = futures[i].GetResult().AsTask();
        return AwaitAll(tasks);

        static async ValueTask<T[]> AwaitAll(Task<T>[] tasks)
        {
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Returns the result of the first future to complete.
    /// </summary>
    public virtual ValueTask<T> Race<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
    {
        var tasks = new Task<T>[futures.Length];
        for (var i = 0; i < futures.Length; i++)
            tasks[i] = futures[i].GetResult().AsTask();
        return AwaitRace(tasks);

        static async ValueTask<T> AwaitRace(Task<T>[] tasks)
        {
            var winner = await Task.WhenAny(tasks).ConfigureAwait(false);
            return await winner.ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Yields futures as they complete, in completion order.
    ///     Each element contains the completed future and an optional error.
    /// </summary>
    public virtual async IAsyncEnumerable<(IDurableFuture future, Exception? error)> WaitAll(
        params IDurableFuture[] futures)
    {
        var taskToFuture = new Dictionary<Task, IDurableFuture>(futures.Length);
        var tasks = new Task[futures.Length];
        for (var i = 0; i < futures.Length; i++)
        {
            var task = futures[i].GetResult().AsTask();
            tasks[i] = task;
            taskToFuture[task] = futures[i];
        }

        await foreach (var completedTask in Task.WhenEach(tasks).ConfigureAwait(false))
        {
            var future = taskToFuture[completedTask];
            var error = completedTask.IsFaulted ? completedTask.Exception?.InnerException : null;
            yield return (future, error);
        }
    }
}