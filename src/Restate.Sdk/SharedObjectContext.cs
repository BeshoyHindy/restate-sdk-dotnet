namespace Restate.Sdk;

/// <summary>
///     Context for shared handlers on virtual objects. Provides read-only state access
///     and can run concurrently with other shared handlers for the same key.
/// </summary>
public abstract class SharedObjectContext : Context, ISharedObjectContext
{
    /// <summary>
    ///     The base context to delegate Context operations to.
    ///     Set by subclasses in their constructors.
    /// </summary>
    internal Context BaseContext { get; init; } = null!;

    /// <summary>The key of the virtual object instance.</summary>
    public abstract string Key { get; }

    // --- Context delegation to BaseContext ---

    /// <inheritdoc />
    public override string InvocationId => BaseContext.InvocationId;

    /// <inheritdoc />
    public override DurableRandom Random => BaseContext.Random;

    /// <inheritdoc />
    public override DurableConsole Console => BaseContext.Console;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Headers => BaseContext.Headers;

    /// <inheritdoc />
    public override CancellationToken Aborted => BaseContext.Aborted;

    /// <summary>Gets the value for the given state key, or default if not set.</summary>
    public abstract ValueTask<T?> Get<T>(StateKey<T> key);

    /// <summary>Lists all state keys for this virtual object instance.</summary>
    public abstract ValueTask<string[]> StateKeys();

    /// <inheritdoc />
    public override ValueTask<DateTimeOffset> Now()
    {
        return BaseContext.Now();
    }

    /// <inheritdoc />
    public override ValueTask<T> Run<T>(string name, Func<Task<T>> action)
    {
        return BaseContext.Run(name, action);
    }

    /// <inheritdoc />
    public override ValueTask Run(string name, Func<Task> action)
    {
        return BaseContext.Run(name, action);
    }

    /// <inheritdoc />
    public override ValueTask<T> Run<T>(string name, Func<T> action)
    {
        return BaseContext.Run(name, action);
    }

    /// <inheritdoc />
    public override ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action)
    {
        return BaseContext.Run(name, action);
    }

    /// <inheritdoc />
    public override ValueTask Run(string name, Func<IRunContext, Task> action)
    {
        return BaseContext.Run(name, action);
    }

    /// <inheritdoc />
    public override ValueTask<T> Run<T>(string name, Func<Task<T>> action, RetryPolicy retryPolicy)
    {
        return BaseContext.Run(name, action, retryPolicy);
    }

    /// <inheritdoc />
    public override ValueTask Run(string name, Func<Task> action, RetryPolicy retryPolicy)
    {
        return BaseContext.Run(name, action, retryPolicy);
    }

    /// <inheritdoc />
    public override ValueTask<T> Run<T>(string name, Func<T> action, RetryPolicy retryPolicy)
    {
        return BaseContext.Run(name, action, retryPolicy);
    }

    /// <inheritdoc />
    public override ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null)
    {
        return BaseContext.Call<TResponse>(service, handler, request);
    }

    /// <inheritdoc />
    public override ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
        object? request = null)
    {
        return BaseContext.Call<TResponse>(service, key, handler, request);
    }

    /// <inheritdoc />
    public override ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request,
        CallOptions options)
    {
        return BaseContext.Call<TResponse>(service, handler, request, options);
    }

    /// <inheritdoc />
    public override ValueTask<TResponse> Call<TResponse>(string service, string key, string handler, object? request,
        CallOptions options)
    {
        return BaseContext.Call<TResponse>(service, key, handler, request, options);
    }

    /// <inheritdoc />
    public override ValueTask CancelInvocation(string invocationId)
    {
        return BaseContext.CancelInvocation(invocationId);
    }

    /// <inheritdoc />
    public override ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null)
    {
        return BaseContext.Send(service, handler, request, delay, idempotencyKey);
    }

    /// <inheritdoc />
    public override ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null)
    {
        return BaseContext.Send(service, key, handler, request, delay, idempotencyKey);
    }

    /// <inheritdoc />
    public override TClient ServiceClient<TClient>()
    {
        return ClientFactory.Create<TClient>(this);
    }

    /// <inheritdoc />
    public override TClient ObjectClient<TClient>(string key)
    {
        return ClientFactory.Create<TClient>(this, key);
    }

    /// <inheritdoc />
    public override TClient WorkflowClient<TClient>(string key)
    {
        return ClientFactory.Create<TClient>(this, key);
    }

    /// <inheritdoc />
    public override TClient ServiceSendClient<TClient>(SendOptions? options = null)
    {
        return ClientFactory.Create<TClient>(this, options: options);
    }

    /// <inheritdoc />
    public override TClient ObjectSendClient<TClient>(string key, SendOptions? options = null)
    {
        return ClientFactory.Create<TClient>(this, key, options);
    }

    /// <inheritdoc />
    public override TClient WorkflowSendClient<TClient>(string key, SendOptions? options = null)
    {
        return ClientFactory.Create<TClient>(this, key, options);
    }

    /// <inheritdoc />
    public override ValueTask Sleep(TimeSpan duration)
    {
        return BaseContext.Sleep(duration);
    }

    /// <inheritdoc />
    public override Awakeable<T> Awakeable<T>(ISerde<T>? serde = null)
    {
        return BaseContext.Awakeable(serde);
    }

    /// <inheritdoc />
    public override void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null)
    {
        BaseContext.ResolveAwakeable(id, payload, serde);
    }

    /// <inheritdoc />
    public override void RejectAwakeable(string id, string reason)
    {
        BaseContext.RejectAwakeable(id, reason);
    }

    /// <inheritdoc />
    public override ValueTask<T> Attach<T>(string invocationId)
    {
        return BaseContext.Attach<T>(invocationId);
    }

    /// <inheritdoc />
    public override ValueTask<T?> GetOutput<T>(string invocationId) where T : default
    {
        return BaseContext.GetOutput<T>(invocationId);
    }

    /// <inheritdoc />
    public override ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler, TRequest request,
        string? key = null)
    {
        return BaseContext.Call<TRequest, TResponse>(service, handler, request, key);
    }

    /// <inheritdoc />
    public override ValueTask<InvocationHandle> Send<TRequest>(string service, string handler, TRequest request,
        string? key = null, SendOptions? options = null)
    {
        return BaseContext.Send(service, handler, request, key, options);
    }

    /// <inheritdoc />
    public override IDurableFuture<T> RunAsync<T>(string name, Func<Task<T>> action)
    {
        return BaseContext.RunAsync(name, action);
    }

    /// <inheritdoc />
    public override IDurableFuture Timer(TimeSpan duration)
    {
        return BaseContext.Timer(duration);
    }

    /// <inheritdoc />
    public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
        object? request = null)
    {
        return BaseContext.CallFuture<TResponse>(service, handler, request);
    }

    /// <inheritdoc />
    public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
        object? request = null)
    {
        return BaseContext.CallFuture<TResponse>(service, key, handler, request);
    }
}