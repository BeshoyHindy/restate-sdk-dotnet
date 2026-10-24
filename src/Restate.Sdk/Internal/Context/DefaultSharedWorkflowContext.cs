using Microsoft.Extensions.Logging;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal.Context;

internal sealed class DefaultSharedWorkflowContext : SharedWorkflowContext
{
    private readonly CancellationToken _ct;
    private readonly InvocationStateMachine _sm;

    public DefaultSharedWorkflowContext(InvocationStateMachine stateMachine, ILogger logger, CancellationToken ct)
    {
        BaseContext = new DefaultContext(stateMachine, logger, ct);
        _sm = stateMachine;
        _ct = ct;
    }

    public override string Key => _sm.Key;

    public override ValueTask<T?> Get<T>(StateKey<T> key) where T : default
    {
        return _sm.GetStateAsync<T>(key.Name, null, _ct);
    }

    public override ValueTask<T?> Get<T>(StateKey<T> key, string? entryName) where T : default
    {
        return _sm.GetStateAsync<T>(key.Name, entryName, _ct);
    }

    public override ValueTask<string[]> StateKeys()
    {
        return _sm.GetStateKeysAsync(null, _ct);
    }

    public override ValueTask<string[]> StateKeys(string? entryName)
    {
        return _sm.GetStateKeysAsync(entryName, _ct);
    }

    public override ValueTask<T?> PeekPromise<T>(string name) where T : default
    {
        return _sm.PeekPromiseAsync<T>(name, _ct);
    }

    public override void ResolvePromise<T>(string name, T payload)
    {
        _sm.ResolvePromise(name, payload);
    }

    public override void RejectPromise(string name, string reason)
    {
        _sm.RejectPromise(name, reason);
    }
}