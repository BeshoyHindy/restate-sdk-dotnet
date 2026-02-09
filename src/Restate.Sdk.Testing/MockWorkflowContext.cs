namespace Restate.Sdk.Testing;

/// <summary>
///     A mock <see cref="WorkflowContext" /> for unit testing workflow run handlers.
///     State is stored in-memory. Durable promises are stored in-memory for get/resolve/reject.
///     Side effects execute inline. Calls and sends are recorded.
/// </summary>
public sealed class MockWorkflowContext : WorkflowContext
{
    private readonly MockContextHelper _helper;
    private readonly MockPromiseStore _promiseStore = new();
    private readonly MockStateStore _stateStore = new();

    /// <summary>Creates a new mock workflow context with the given key.</summary>
    public MockWorkflowContext(string key = "test-key", string? invocationId = null)
    {
        _helper = new MockContextHelper(key, invocationId);
        BaseContext = _helper.InnerMock;
    }

    /// <inheritdoc />
    public override string Key => _helper.Key;

    /// <summary>All recorded Call invocations.</summary>
    public IReadOnlyList<RecordedCall> Calls => _helper.Calls;

    /// <summary>All recorded Send invocations.</summary>
    public IReadOnlyList<RecordedSend> Sends => _helper.Sends;

    /// <summary>All recorded Sleep invocations with their requested durations.</summary>
    public IReadOnlyList<RecordedSleep> Sleeps => _helper.Sleeps;

    /// <summary>Configures the return value for a Call to the given service/handler.</summary>
    public void SetupCall<T>(string service, string handler, T result) => _helper.SetupCall(service, handler, result);

    /// <summary>Configures the return value for a Call to the given service/key/handler.</summary>
    public void SetupCall<T>(string service, string key, string handler, T result) => _helper.SetupCall(service, key, handler, result);

    /// <summary>Configures a Call to the given service/handler to throw a <see cref="TerminalException" />.</summary>
    public void SetupCallFailure(string service, string handler, TerminalException exception) => _helper.SetupCallFailure(service, handler, exception);

    /// <summary>Configures a Call to the given service/key/handler to throw a <see cref="TerminalException" />.</summary>
    public void SetupCallFailure(string service, string key, string handler, TerminalException exception) => _helper.SetupCallFailure(service, key, handler, exception);

    /// <summary>Enqueues a value to be returned by the next <see cref="Context.Awakeable{T}" /> call.</summary>
    public void SetupAwakeable<T>(T result) => _helper.SetupAwakeable(result);

    /// <summary>Registers a typed client instance to be returned by typed client methods.</summary>
    public void RegisterClient<TClient>(TClient client) where TClient : class => _helper.RegisterClient(client);

    /// <summary>Pre-populates a state key for testing.</summary>
    public void SetupState<T>(StateKey<T> key, T value) => _stateStore.SetupState(key, value);

    /// <summary>Pre-populates a state key by name.</summary>
    public void SetupState<T>(string key, T value) => _stateStore.SetupState(key, value);

    /// <summary>Gets the current value of a state key for verification.</summary>
    public T? GetStateValue<T>(string key) => _stateStore.GetStateValue<T>(key);

    /// <summary>Verifies whether a state key exists.</summary>
    public bool HasState(string key) => _stateStore.HasState(key);

    /// <summary>Pre-resolves a promise so that <see cref="Promise{T}" /> returns immediately.</summary>
    public void SetupPromise<T>(string name, T value) => _promiseStore.SetupPromise(name, value);

    /// <summary>Checks whether a promise has been resolved.</summary>
    public bool IsPromiseResolved(string name) => _promiseStore.IsPromiseResolved(name);

    /// <summary>Checks whether a promise has been rejected.</summary>
    public bool IsPromiseRejected(string name) => _promiseStore.IsPromiseRejected(name);

    /// <summary>Gets the resolved value of a promise for verification.</summary>
    public T? GetPromiseValue<T>(string name) => _promiseStore.GetPromiseValue<T>(name);

    /// <summary>Gets the rejection reason of a promise for verification.</summary>
    public string? GetPromiseRejection(string name) => _promiseStore.GetPromiseRejection(name);

    /// <inheritdoc />
    public override ValueTask<T> Promise<T>(string name) => _promiseStore.Promise<T>(name);

    /// <inheritdoc />
    public override ValueTask<T?> PeekPromise<T>(string name) where T : default => _promiseStore.PeekPromise<T>(name);

    /// <inheritdoc />
    public override void ResolvePromise<T>(string name, T payload) => _promiseStore.ResolvePromise(name, payload);

    /// <inheritdoc />
    public override void RejectPromise(string name, string reason) => _promiseStore.RejectPromise(name, reason);

    /// <inheritdoc />
    public override void Set<T>(StateKey<T> key, T value) => _stateStore.Set(key, value);

    /// <inheritdoc />
    public override void Clear(string key) => _stateStore.Clear(key);

    /// <inheritdoc />
    public override void ClearAll() => _stateStore.ClearAll();

    /// <inheritdoc />
    public override ValueTask<T?> Get<T>(StateKey<T> key) where T : default => _stateStore.Get<T>(key);

    /// <inheritdoc />
    public override ValueTask<string[]> StateKeys() => _stateStore.StateKeys();
}
