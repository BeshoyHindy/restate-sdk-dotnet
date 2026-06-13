using System.Buffers;
using Microsoft.Extensions.Logging;
using Restate.Sdk.Internal.Journal;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal.Context;

internal sealed class DefaultContext : Restate.Sdk.Context
{
    private readonly ILogger _logger;
    private readonly InvocationStateMachine _stateMachine;
    private DurableConsole? _console;
    private DurableRandom? _random;

    public DefaultContext(InvocationStateMachine stateMachine, ILogger logger, CancellationToken ct)
    {
        _stateMachine = stateMachine;
        _logger = logger;
        Aborted = ct;
    }

    public override string InvocationId => _stateMachine.InvocationId;
    public override DurableRandom Random => _random ??= new DurableRandom(_stateMachine.RandomSeed);

    public override DurableConsole Console => _console ??= new DurableConsole(() => _stateMachine.IsReplaying);
    public override IReadOnlyDictionary<string, string> Headers => _stateMachine.Headers;
    public override CancellationToken Aborted { get; }

    public override ValueTask<DateTimeOffset> Now()
    {
        return _stateMachine.RunAsync("__restate_now", static () => Task.FromResult(DateTimeOffset.UtcNow), Aborted);
    }

    public override ValueTask<T> Run<T>(string name, Func<Task<T>> action)
    {
        return _stateMachine.RunAsync(name, action, Aborted);
    }

    public override ValueTask Run(string name, Func<Task> action)
    {
        return _stateMachine.RunAsync(name, action, Aborted);
    }

    public override ValueTask<T> Run<T>(string name, Func<T> action)
    {
        return _stateMachine.RunSync(name, action, Aborted);
    }

    public override ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action)
    {
        var runCtx = new RunContext(Aborted, _logger);
        return _stateMachine.RunAsync(name, () => action(runCtx), Aborted);
    }

    public override ValueTask Run(string name, Func<IRunContext, Task> action)
    {
        var runCtx = new RunContext(Aborted, _logger);
        return _stateMachine.RunAsync(name, () => action(runCtx), Aborted);
    }

    public override ValueTask<T> Run<T>(string name, Func<Task<T>> action, RetryPolicy retryPolicy)
    {
        return _stateMachine.RunAsync(name, action, Aborted, retryPolicy);
    }

    public override ValueTask Run(string name, Func<Task> action, RetryPolicy retryPolicy)
    {
        return _stateMachine.RunAsync(name, action, Aborted, retryPolicy);
    }

    public override ValueTask<T> Run<T>(string name, Func<T> action, RetryPolicy retryPolicy)
    {
        return _stateMachine.RunAsync(name, () => Task.FromResult(action()), Aborted, retryPolicy);
    }

    public override IDurableFuture<T> RunAsync<T>(string name, Func<Task<T>> action)
    {
        var resolve = _stateMachine.RunFutureAsync(name, action, Aborted);
        return new LazyRunFuture<T>(resolve, _stateMachine.JsonOptions);
    }

    public override IDurableFuture Timer(TimeSpan duration)
    {
        var resolve = _stateMachine.SleepFutureAsync(duration, Aborted);
        return new LazyTimerFuture(resolve);
    }

    public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
        object? request = null)
    {
        return CallFutureInternal<TResponse>(service, null, handler, request);
    }

    public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
        object? request = null)
    {
        return CallFutureInternal<TResponse>(service, key, handler, request);
    }

    private IDurableFuture<TResponse> CallFutureInternal<TResponse>(string service, string? key, string handler,
        object? request)
    {
        var resolve = _stateMachine.CallFutureAsync(service, key, handler, request, Aborted);
        return new LazyCallFuture<TResponse>(resolve, _stateMachine.JsonOptions);
    }

    public override ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null)
    {
        return _stateMachine.CallAsync<TResponse>(service, null, handler, request, Aborted);
    }

    public override ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
        object? request = null)
    {
        return _stateMachine.CallAsync<TResponse>(service, key, handler, request, Aborted);
    }

    public override ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request,
        CallOptions options)
    {
        return _stateMachine.CallAsync<TResponse>(service, null, handler, request, options.IdempotencyKey,
            options.Headers, options.Name, Aborted);
    }

    public override ValueTask<TResponse> Call<TResponse>(string service, string key, string handler, object? request,
        CallOptions options)
    {
        return _stateMachine.CallAsync<TResponse>(service, key, handler, request, options.IdempotencyKey,
            options.Headers, options.Name, Aborted);
    }

    public override CallHandle<TResponse> CallHandle<TResponse>(string service, string handler, object? request = null,
        CallOptions? options = null)
    {
        return _stateMachine.CallHandleAsync<TResponse>(service, null, handler, request,
            options?.IdempotencyKey, options?.Headers, options?.Name, Aborted);
    }

    public override CallHandle<TResponse> CallHandle<TResponse>(string service, string key, string handler,
        object? request, CallOptions? options = null)
    {
        return _stateMachine.CallHandleAsync<TResponse>(service, key, handler, request,
            options?.IdempotencyKey, options?.Headers, options?.Name, Aborted);
    }

    public override ValueTask CancelInvocation(string invocationId)
    {
        return _stateMachine.CancelInvocationAsync(invocationId, Aborted);
    }

    public override ValueTask SendSignal<T>(string targetInvocationId, string name, T payload,
        ISerde<T>? serde = null)
    {
        // Serialize INSIDE the SM helper (reuses _serializeBuffer), then copy to a stable buffer:
        // SendSignalAsync awaits a flush, so the bytes must outlive the reusable buffer.
        var bytes = _stateMachine.SerializeWithSerde(payload, serde).ToArray();
        return _stateMachine.SendSignalAsync(targetInvocationId, name, bytes, Aborted);
    }

    public override ValueTask SendSignalFailure(string targetInvocationId, string name, string reason)
    {
        // Failure variant: no value bytes; the SM writes the failure result oneof. 500 mirrors the
        // RejectAwakeable failure code (a generic terminal/business failure).
        return _stateMachine.SendSignalAsync(targetInvocationId, name, ReadOnlyMemory<byte>.Empty,
            Aborted, (500, reason));
    }

    public override ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null)
    {
        return _stateMachine.SendAsync(service, null, handler, request, delay, idempotencyKey, Aborted);
    }

    public override ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null)
    {
        return _stateMachine.SendAsync(service, key, handler, request, delay, idempotencyKey, Aborted);
    }

    public override ValueTask<InvocationHandle> Send(string service, string handler, object? request,
        SendOptions options)
    {
        return _stateMachine.SendAsync(service, null, handler, request, options.Delay, options.IdempotencyKey,
            options.Headers, options.Name, Aborted);
    }

    public override ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request,
        SendOptions options)
    {
        return _stateMachine.SendAsync(service, key, handler, request, options.Delay, options.IdempotencyKey,
            options.Headers, options.Name, Aborted);
    }

    public override TClient ServiceClient<TClient>()
    {
        return ClientFactory.Create<TClient>(this);
    }

    public override TClient ObjectClient<TClient>(string key)
    {
        return ClientFactory.Create<TClient>(this, key);
    }

    public override TClient WorkflowClient<TClient>(string key)
    {
        return ClientFactory.Create<TClient>(this, key);
    }

    public override TClient ServiceSendClient<TClient>(SendOptions? options = null)
    {
        return ClientFactory.Create<TClient>(this, options: options);
    }

    public override TClient ObjectSendClient<TClient>(string key, SendOptions? options = null)
    {
        return ClientFactory.Create<TClient>(this, key, options);
    }

    public override TClient WorkflowSendClient<TClient>(string key, SendOptions? options = null)
    {
        return ClientFactory.Create<TClient>(this, key, options);
    }

    public override ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler,
        TRequest request, string? key = null)
    {
        return _stateMachine.CallAsync<TRequest, TResponse>(service, handler, request, key, Aborted);
    }

    public override ValueTask<InvocationHandle> Send<TRequest>(string service, string handler,
        TRequest request, string? key = null, SendOptions? options = null)
    {
        return _stateMachine.SendAsync(service, handler, request, key, options?.Delay, options?.IdempotencyKey,
            options?.Headers, options?.Name, Aborted);
    }

    public override ValueTask<T> Attach<T>(string invocationId)
    {
        return _stateMachine.AttachInvocationAsync<T>(invocationId, Aborted);
    }

    public override ValueTask<T> Attach<T>(AttachTarget target)
    {
        return _stateMachine.AttachInvocationAsync<T>(target, Aborted);
    }

    public override ValueTask<T?> GetOutput<T>(string invocationId) where T : default
    {
        return _stateMachine.GetInvocationOutputAsync<T>(invocationId, Aborted);
    }

    public override ValueTask<T?> GetOutput<T>(AttachTarget target) where T : default
    {
        return _stateMachine.GetInvocationOutputAsync<T>(target, Aborted);
    }

    public override ValueTask Sleep(TimeSpan duration)
    {
        return _stateMachine.SleepAsync(duration, Aborted);
    }

    public override Awakeable<T> Awakeable<T>(ISerde<T>? serde = null)
    {
        var (id, signalId) = _stateMachine.Awakeable();
        // Flush any pending writes (gated, so it can't race a detached Run proposal's flush) so the
        // server processes prior commands before we block.
        var flushTask = _stateMachine.FlushPendingAsync(Aborted);
        return new Awakeable<T>
        {
            Id = id,
            Value = FlushThenAwaitAwakeable(flushTask, signalId, serde)
        };
    }

    private async ValueTask<T> FlushThenAwaitAwakeable<T>(
        ValueTask flushTask, uint signalId, ISerde<T>? serde = null)
    {
        await flushTask.ConfigureAwait(false);
        // Park through the SM's single await point (suspension/replay-guard aware).
        var result = await _stateMachine
            .AwaitNotificationAsync(signalId, InvocationStateMachine.NotificationKind.Signal)
            .ConfigureAwait(false);
        result.ThrowIfFailure();
        if (serde is not null)
            return serde.Deserialize(new ReadOnlySequence<byte>(result.Value));
        return _stateMachine.Deserialize<T>(result.Value);
    }

    public override NamedSignal<T> NamedSignal<T>(string name, ISerde<T>? serde = null)
    {
        // Local registration only — no command, no journal entry (Rust create_signal_handle). Flush
        // pending writes (gated) so the server processes prior commands before we block, identical to
        // the awakeable pre-park flush.
        _stateMachine.RegisterNamedSignal(name);
        var flushTask = _stateMachine.FlushPendingAsync(Aborted);
        return new NamedSignal<T>
        {
            Name = name,
            Value = FlushThenAwaitNamedSignal(flushTask, name, serde)
        };
    }

    private async ValueTask<T> FlushThenAwaitNamedSignal<T>(
        ValueTask flushTask, string name, ISerde<T>? serde = null)
    {
        await flushTask.ConfigureAwait(false);
        var result = await _stateMachine.AwaitNamedSignalAsync(name).ConfigureAwait(false);
        result.ThrowIfFailure();
        if (serde is not null)
            return serde.Deserialize(new ReadOnlySequence<byte>(result.Value));
        return _stateMachine.Deserialize<T>(result.Value);
    }

    public override void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null)
    {
        var bytes = _stateMachine.SerializeWithSerde(payload, serde);
        _stateMachine.ResolveAwakeable(id, bytes);
    }

    public override void RejectAwakeable(string id, string reason)
    {
        _stateMachine.RejectAwakeable(id, reason);
    }

    public override void RejectAwakeable(string id, string reason, ushort code)
    {
        _stateMachine.RejectAwakeable(id, reason, code);
    }
}