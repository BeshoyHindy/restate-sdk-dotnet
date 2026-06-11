namespace Restate.Sdk;

/// <summary>
///     A handle to an invocation started by a send operation. The invocation id is resolved
///     lazily from the runtime's CallInvocationIdCompletionNotification — mirroring the Rust
///     shared-core SendHandle and the Python SDK send handle. Sends are fire-and-forget; the
///     id round trip happens only if the caller asks for it.
/// </summary>
public sealed class InvocationHandle
{
    private readonly Lazy<Task<string>> _invocationId;

    /// <summary>Eager constructor for call sites that already know the id (ingress clients, tests, mocks).</summary>
    public InvocationHandle(string invocationId) =>
        _invocationId = new Lazy<Task<string>>(() => Task.FromResult(invocationId));

    /// <summary>
    ///     Lazy constructor — the resolve thunk runs on FIRST GetInvocationIdAsync call, not at send
    ///     time, so an unawaited handle never parks, never registers in the awaiting set, and never
    ///     produces an UnobservedTaskException.
    /// </summary>
    internal InvocationHandle(Func<Task<string>> resolveInvocationId) =>
        _invocationId = new Lazy<Task<string>>(resolveInvocationId);

    /// <summary>
    ///     Resolves the invocation id. For a send handle this is a suspension point: awaiting it
    ///     with input closed and no CallInvocationIdCompletionNotification suspends the invocation
    ///     with the send's id in waiting_completions. Repeated awaits return the same value.
    /// </summary>
    public ValueTask<string> GetInvocationIdAsync() => new(_invocationId.Value);
}
