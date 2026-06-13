using Microsoft.Extensions.Logging;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Internal.Context;

internal sealed class DefaultObjectContext : ObjectContext
{
    private readonly CancellationToken _ct;
    private readonly InvocationStateMachine _sm;

    public DefaultObjectContext(InvocationStateMachine stateMachine, ILogger logger, CancellationToken ct)
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

    public override void Set<T>(StateKey<T> key, T value, PayloadOptions payload)
    {
        _sm.SetState(key.Name, value, payload);
    }

    public override void Clear(string key)
    {
        _sm.ClearState(key);
    }

    public override void ClearAll()
    {
        _sm.ClearAllState();
    }
}