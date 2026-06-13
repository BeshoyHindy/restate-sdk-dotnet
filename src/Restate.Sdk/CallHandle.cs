namespace Restate.Sdk;

/// <summary>
///     A handle to a blocking <c>Call</c> that exposes BOTH the child's result and its invocation id,
///     mirroring the shared-core <c>CallHandle</c> (lib.rs:141-145) which carries a
///     <c>call_notification_handle</c> and an <c>invocation_id_notification_handle</c>. The plain
///     <see cref="IContext.Call{TResponse}(string, string, object?)" /> overloads still return the
///     result directly; use <see cref="IContext.CallHandle{TResponse}(string, string, object?, CallOptions?)" />
///     when the handler needs the callee's invocation id (e.g. to later cancel or attach to it) in
///     addition to awaiting the response.
/// </summary>
/// <typeparam name="TResponse">The deserialized response type of the call.</typeparam>
public sealed class CallHandle<TResponse>
{
    private readonly Func<ValueTask<TResponse>> _result;
    private readonly Lazy<Task<string>> _invocationId;

    /// <summary>
    ///     Lazy constructor — neither thunk runs at construction. The first
    ///     <see cref="GetResponseAsync" /> parks on the call result completion; the first
    ///     <see cref="GetInvocationIdAsync" /> parks on the (separately allocated) invocation-id
    ///     completion. An unawaited id thunk never registers a waiter, so asking only for the result
    ///     never suspends on the id round trip.
    /// </summary>
    internal CallHandle(Func<ValueTask<TResponse>> result, Func<Task<string>> resolveInvocationId)
    {
        _result = result;
        _invocationId = new Lazy<Task<string>>(resolveInvocationId);
    }

    /// <summary>Eager constructor for test doubles / mocks that already know both values.</summary>
    public CallHandle(TResponse response, string invocationId)
    {
        _result = () => new ValueTask<TResponse>(response);
        _invocationId = new Lazy<Task<string>>(() => Task.FromResult(invocationId));
    }

    /// <summary>
    ///     Awaits the call's result. Re-awaiting re-parks on the same completion id (the SM dedupes a
    ///     resolved completion), so repeated awaits return the same value.
    /// </summary>
    public ValueTask<TResponse> GetResponseAsync() => _result();

    /// <summary>
    ///     Resolves the callee's invocation id. This is a distinct suspension point from the result:
    ///     awaiting it with input closed and no <c>CallInvocationIdCompletionNotification</c> suspends
    ///     the invocation. Repeated awaits return the same value.
    /// </summary>
    public ValueTask<string> GetInvocationIdAsync() => new(_invocationId.Value);
}
