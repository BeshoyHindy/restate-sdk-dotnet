using Microsoft.Extensions.Logging;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal.Context;

internal sealed class DefaultWorkflowContext : WorkflowContext
{
    private readonly CancellationToken _ct;
    private readonly InvocationStateMachine _sm;

    public DefaultWorkflowContext(InvocationStateMachine stateMachine, ILogger logger, CancellationToken ct)
    {
        BaseContext = new DefaultContext(stateMachine, logger, ct);
        _sm = stateMachine;
        _ct = ct;
    }

    public override string Key => _sm.Key;

    public override ValueTask<T?> Get<T>(StateKey<T> key) where T : default
    {
        return _sm.GetStateAsync<T>(key.Name, _ct);
    }

    public override ValueTask<string[]> StateKeys()
    {
        return _sm.GetStateKeysAsync(_ct);
    }

    public override void Set<T>(StateKey<T> key, T value)
    {
        _sm.SetState(key.Name, value);
    }

    public override void Clear(string key)
    {
        _sm.ClearState(key);
    }

    public override void ClearAll()
    {
        _sm.ClearAllState();
    }

    public override ValueTask<T> Promise<T>(string name)
    {
        return _sm.GetPromiseAsync<T>(name, _ct);
    }

    public override ValueTask<T?> PeekPromise<T>(string name) where T : default
    {
        return _sm.PeekPromiseAsync<T>(name, _ct);
    }

    public override ValueTask ResolvePromise<T>(string name, T payload)
    {
        return _sm.ResolvePromise(name, payload, _ct);
    }

    public override ValueTask RejectPromise(string name, string reason)
    {
        return _sm.RejectPromise(name, reason, _ct);
    }
}