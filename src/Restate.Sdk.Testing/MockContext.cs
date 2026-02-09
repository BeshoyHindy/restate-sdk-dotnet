using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Restate.Sdk.Testing;

/// <summary>
///     A mock <see cref="Context" /> for unit testing stateless service handlers.
///     Side effects execute inline, calls and sends are recorded for verification.
/// </summary>
public sealed class MockContext : Context
{
    private readonly Queue<object?> _awakeableResults = new();
    private readonly Dictionary<string, TerminalException> _callFailures = [];
    private readonly Dictionary<string, object?> _callResults = [];
    private readonly List<RecordedCall> _calls = [];
    private readonly Dictionary<Type, object> _clients = [];
    private readonly List<RecordedSend> _sends = [];
    private readonly List<RecordedSleep> _sleeps = [];
    private int _invocationCounter;

    /// <summary>Creates a new mock context with the given invocation ID.</summary>
    public MockContext(string? invocationId = null)
    {
        InvocationId = invocationId ?? $"mock-invocation-{Guid.NewGuid():N}";
        Random = new DurableRandom(12345);
        Console = new DurableConsole(() => false);
    }

    /// <inheritdoc />
    public override string InvocationId { get; }

    /// <inheritdoc />
    public override DurableRandom Random { get; }

    /// <inheritdoc />
    public override DurableConsole Console { get; }

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    /// <inheritdoc />
    public override CancellationToken Aborted => CancellationToken.None;

    /// <summary>All recorded Call invocations.</summary>
    public IReadOnlyList<RecordedCall> Calls => _calls;

    /// <summary>All recorded Send invocations.</summary>
    public IReadOnlyList<RecordedSend> Sends => _sends;

    /// <summary>All recorded Sleep invocations with their requested durations.</summary>
    public IReadOnlyList<RecordedSleep> Sleeps => _sleeps;

    /// <summary>
    ///     Configures the return value for a Call to the given service/handler.
    /// </summary>
    public void SetupCall<T>(string service, string handler, T result)
    {
        _callResults[$"{service}/{handler}"] = result;
    }

    /// <summary>
    ///     Configures the return value for a Call to the given service/key/handler.
    /// </summary>
    public void SetupCall<T>(string service, string key, string handler, T result)
    {
        _callResults[$"{service}/{key}/{handler}"] = result;
    }

    /// <summary>
    ///     The time returned by <see cref="Now" />. Set this to control the time in tests.
    ///     Defaults to 2024-01-01T00:00:00Z.
    /// </summary>
    public DateTimeOffset CurrentTime { get; set; } = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    ///     Enqueues a value to be returned by the next <see cref="Awakeable{T}" /> call.
    ///     Values are consumed in FIFO order. If no value is enqueued, the awakeable resolves
    ///     with <c>default(T)</c>.
    /// </summary>
    public void SetupAwakeable<T>(T result)
    {
        _awakeableResults.Enqueue(result);
    }

    /// <summary>
    ///     Configures a Call to the given service/handler to throw a <see cref="TerminalException" />.
    /// </summary>
    public void SetupCallFailure(string service, string handler, TerminalException exception)
    {
        _callFailures[$"{service}/{handler}"] = exception;
    }

    /// <summary>
    ///     Configures a Call to the given service/key/handler to throw a <see cref="TerminalException" />.
    /// </summary>
    public void SetupCallFailure(string service, string key, string handler, TerminalException exception)
    {
        _callFailures[$"{service}/{key}/{handler}"] = exception;
    }

    /// <summary>
    ///     Registers a typed client instance to be returned by <see cref="ServiceClient{TClient}" />,
    ///     <see cref="ObjectClient{TClient}" />, or <see cref="WorkflowClient{TClient}" />.
    /// </summary>
    public void RegisterClient<TClient>(TClient client) where TClient : class
    {
        _clients[typeof(TClient)] = client;
    }

    /// <inheritdoc />
    public override ValueTask<T> Run<T>(string name, Func<Task<T>> action)
    {
        return new ValueTask<T>(action());
    }

    /// <inheritdoc />
    public override ValueTask Run(string name, Func<Task> action)
    {
        return new ValueTask(action());
    }

    /// <inheritdoc />
    public override ValueTask<T> Run<T>(string name, Func<T> action)
    {
        return new ValueTask<T>(action());
    }

    /// <inheritdoc />
    public override ValueTask<T> Run<T>(string name, Func<IRunContext, Task<T>> action)
    {
        return new ValueTask<T>(action(new MockRunContext()));
    }

    /// <inheritdoc />
    public override ValueTask Run(string name, Func<IRunContext, Task> action)
    {
        return new ValueTask(action(new MockRunContext()));
    }

    /// <inheritdoc />
    public override ValueTask<TResponse> Call<TResponse>(string service, string handler, object? request = null)
    {
        _calls.Add(new RecordedCall(service, null, handler, request));
        var lookupKey = $"{service}/{handler}";
        if (_callFailures.TryGetValue(lookupKey, out var failure))
            throw failure;
        if (_callResults.TryGetValue(lookupKey, out var result))
            return new ValueTask<TResponse>((TResponse)result!);
        return new ValueTask<TResponse>(default(TResponse)!);
    }

    /// <inheritdoc />
    public override ValueTask<TResponse> Call<TResponse>(string service, string key, string handler,
        object? request = null)
    {
        _calls.Add(new RecordedCall(service, key, handler, request));
        var lookupKey = $"{service}/{key}/{handler}";
        if (_callFailures.TryGetValue(lookupKey, out var failure))
            throw failure;
        if (_callResults.TryGetValue(lookupKey, out var result))
            return new ValueTask<TResponse>((TResponse)result!);
        return new ValueTask<TResponse>(default(TResponse)!);
    }

    /// <inheritdoc />
    public override ValueTask<InvocationHandle> Send(string service, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null)
    {
        _sends.Add(new RecordedSend(service, null, handler, request, delay, idempotencyKey));
        return new ValueTask<InvocationHandle>(
            new InvocationHandle($"mock-inv-{Interlocked.Increment(ref _invocationCounter)}"));
    }

    /// <inheritdoc />
    public override ValueTask<InvocationHandle> Send(string service, string key, string handler, object? request = null,
        TimeSpan? delay = null, string? idempotencyKey = null)
    {
        _sends.Add(new RecordedSend(service, key, handler, request, delay, idempotencyKey));
        return new ValueTask<InvocationHandle>(
            new InvocationHandle($"mock-inv-{Interlocked.Increment(ref _invocationCounter)}"));
    }

    /// <inheritdoc />
    public override TClient ServiceClient<TClient>()
    {
        return _clients.TryGetValue(typeof(TClient), out var c)
            ? (TClient)c
            : throw new NotSupportedException(MockHelpers.TypedClientNotSupported);
    }

    /// <inheritdoc />
    public override TClient ObjectClient<TClient>(string key)
    {
        return _clients.TryGetValue(typeof(TClient), out var c)
            ? (TClient)c
            : throw new NotSupportedException(MockHelpers.TypedClientNotSupported);
    }

    /// <inheritdoc />
    public override TClient WorkflowClient<TClient>(string key)
    {
        return _clients.TryGetValue(typeof(TClient), out var c)
            ? (TClient)c
            : throw new NotSupportedException(MockHelpers.TypedClientNotSupported);
    }

    /// <inheritdoc />
    public override TClient ServiceSendClient<TClient>(SendOptions? options = null)
    {
        return _clients.TryGetValue(typeof(TClient), out var c)
            ? (TClient)c
            : throw new NotSupportedException(MockHelpers.TypedSendClientNotSupported);
    }

    /// <inheritdoc />
    public override TClient ObjectSendClient<TClient>(string key, SendOptions? options = null)
    {
        return _clients.TryGetValue(typeof(TClient), out var c)
            ? (TClient)c
            : throw new NotSupportedException(MockHelpers.TypedSendClientNotSupported);
    }

    /// <inheritdoc />
    public override TClient WorkflowSendClient<TClient>(string key, SendOptions? options = null)
    {
        return _clients.TryGetValue(typeof(TClient), out var c)
            ? (TClient)c
            : throw new NotSupportedException(MockHelpers.TypedSendClientNotSupported);
    }

    /// <inheritdoc />
    public override ValueTask Sleep(TimeSpan duration)
    {
        _sleeps.Add(new RecordedSleep(duration));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override Awakeable<T> Awakeable<T>(ISerde<T>? serde = null)
    {
        var id = $"mock-awakeable-{Interlocked.Increment(ref _invocationCounter)}";
        var value = _awakeableResults.Count > 0
            ? (T)_awakeableResults.Dequeue()!
            : default!;
        return new Awakeable<T>
        {
            Id = id,
            Value = new ValueTask<T>(value)
        };
    }

    /// <inheritdoc />
    public override void ResolveAwakeable<T>(string id, T payload, ISerde<T>? serde = null)
    {
    }

    /// <inheritdoc />
    public override void RejectAwakeable(string id, string reason)
    {
    }

    /// <inheritdoc />
    public override ValueTask<DateTimeOffset> Now()
    {
        return new ValueTask<DateTimeOffset>(CurrentTime);
    }

    /// <inheritdoc />
    public override ValueTask<T> Attach<T>(string invocationId)
    {
        return new ValueTask<T>(default(T)!);
    }

    /// <inheritdoc />
    public override ValueTask<T?> GetOutput<T>(string invocationId) where T : default
    {
        return new ValueTask<T?>(default(T));
    }

    /// <inheritdoc />
    public override ValueTask<TResponse> Call<TRequest, TResponse>(string service, string handler, TRequest request,
        string? key = null)
    {
        _calls.Add(new RecordedCall(service, key, handler, request));
        var lookupKey = key is not null ? $"{service}/{key}/{handler}" : $"{service}/{handler}";
        if (_callFailures.TryGetValue(lookupKey, out var failure))
            throw failure;
        if (_callResults.TryGetValue(lookupKey, out var result))
            return new ValueTask<TResponse>((TResponse)result!);
        return new ValueTask<TResponse>(default(TResponse)!);
    }

    /// <inheritdoc />
    public override ValueTask<InvocationHandle> Send<TRequest>(string service, string handler, TRequest request,
        string? key = null, SendOptions? options = null)
    {
        _sends.Add(new RecordedSend(service, key, handler, request, options?.Delay, options?.IdempotencyKey));
        return new ValueTask<InvocationHandle>(
            new InvocationHandle($"mock-inv-{Interlocked.Increment(ref _invocationCounter)}"));
    }

    /// <inheritdoc />
    public override IDurableFuture<T> RunAsync<T>(string name, Func<Task<T>> action)
    {
        return new EagerFuture<T>(action());
    }

    /// <inheritdoc />
    public override IDurableFuture Timer(TimeSpan duration)
    {
        return new CompletedVoidFuture();
    }

    /// <inheritdoc />
    public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string handler,
        object? request = null)
    {
        var vt = Call<TResponse>(service, handler, request);
        return new CompletedFuture<TResponse>(vt.IsCompletedSuccessfully ? vt.Result : default!);
    }

    /// <inheritdoc />
    public override IDurableFuture<TResponse> CallFuture<TResponse>(string service, string key, string handler,
        object? request = null)
    {
        var vt = Call<TResponse>(service, key, handler, request);
        return new CompletedFuture<TResponse>(vt.IsCompletedSuccessfully ? vt.Result : default!);
    }

    /// <inheritdoc />
    public override ValueTask<T[]> All<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
    {
        var futuresCopy = new IDurableFuture<T>[futures.Length];
        futures.CopyTo(futuresCopy);
        return AwaitAll(futuresCopy);

        static async ValueTask<T[]> AwaitAll(IDurableFuture<T>[] items)
        {
            var results = new T[items.Length];
            for (var i = 0; i < items.Length; i++)
                results[i] = await items[i].GetResult();
            return results;
        }
    }

    /// <inheritdoc />
    public override ValueTask<T> Race<T>(params ReadOnlySpan<IDurableFuture<T>> futures)
    {
        return futures.Length > 0 ? futures[0].GetResult() : throw new ArgumentException("No futures provided");
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<(IDurableFuture future, Exception? error)> WaitAll(
        params IDurableFuture[] futures)
    {
        foreach (var future in futures)
        {
            Exception? error = null;
            try
            {
                await future.GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            yield return (future, error);
        }
    }

    private sealed class CompletedFuture<T> : IDurableFuture<T>
    {
        private readonly T _value;

        public CompletedFuture(T value)
        {
            _value = value;
        }

        public ValueTask<T> GetResult()
        {
            return new ValueTask<T>(_value);
        }

        ValueTask<object?> IDurableFuture.GetResult()
        {
            return new ValueTask<object?>(_value);
        }

        public string? InvocationId => null;
    }

    private sealed class EagerFuture<T> : IDurableFuture<T>
    {
        private readonly Task<T> _task;

        public EagerFuture(Task<T> task)
        {
            _task = task;
        }

        public ValueTask<T> GetResult()
        {
            return new ValueTask<T>(_task);
        }

        async ValueTask<object?> IDurableFuture.GetResult()
        {
            return await _task.ConfigureAwait(false);
        }

        public string? InvocationId => null;
    }

    private sealed class CompletedVoidFuture : IDurableFuture
    {
        public ValueTask<object?> GetResult()
        {
            return new ValueTask<object?>((object?)null);
        }
    }

    private sealed class MockRunContext : IRunContext
    {
        public CancellationToken CancellationToken => CancellationToken.None;
        public ILogger Logger { get; } = NullLogger.Instance;
    }
}

/// <summary>A recorded Call invocation.</summary>
public sealed record RecordedCall(string Service, string? Key, string Handler, object? Request);

/// <summary>A recorded Send invocation.</summary>
public sealed record RecordedSend(
    string Service,
    string? Key,
    string Handler,
    object? Request,
    TimeSpan? Delay,
    string? IdempotencyKey);

/// <summary>A recorded Sleep invocation.</summary>
public sealed record RecordedSleep(TimeSpan Duration);