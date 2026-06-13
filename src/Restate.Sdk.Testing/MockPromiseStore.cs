using System.Runtime.ExceptionServices;

namespace Restate.Sdk.Testing;

/// <summary>
///     In-memory durable promise store shared by mock workflow contexts.
/// </summary>
internal sealed class MockPromiseStore
{
    private readonly Dictionary<string, TaskCompletionSource<object?>> _pending = [];
    private readonly Dictionary<string, string> _rejected = [];
    private readonly Dictionary<string, object?> _resolved = [];

    public void SetupPromise<T>(string name, T value)
    {
        _resolved[name] = value;
    }

    public bool IsPromiseResolved(string name)
    {
        return _resolved.ContainsKey(name);
    }

    public bool IsPromiseRejected(string name)
    {
        return _rejected.ContainsKey(name);
    }

    public T? GetPromiseValue<T>(string name)
    {
        return _resolved.TryGetValue(name, out var val) ? (T?)val : default;
    }

    public string? GetPromiseRejection(string name)
    {
        return _rejected.GetValueOrDefault(name);
    }

    public ValueTask<T> Promise<T>(string name)
    {
        if (_resolved.TryGetValue(name, out var value))
            return new ValueTask<T>((T)value!);
        if (_rejected.TryGetValue(name, out var reason))
            return ValueTask.FromException<T>(new TerminalException(reason));

        if (!_pending.TryGetValue(name, out var tcs))
        {
            tcs = new TaskCompletionSource<object?>();
            _pending[name] = tcs;
        }

        return new ValueTask<T>(tcs.Task.ContinueWith(static t =>
        {
            if (t.IsFaulted)
            {
                var inner = t.Exception!.InnerException ?? t.Exception!;
                ExceptionDispatchInfo.Capture(inner).Throw();
            }

            return (T)t.Result!;
        }, TaskScheduler.Default));
    }

    public ValueTask<T?> PeekPromise<T>(string name)
    {
        if (_resolved.TryGetValue(name, out var value))
            return new ValueTask<T?>((T?)value);
        return new ValueTask<T?>(default(T));
    }

    public ValueTask ResolvePromise<T>(string name, T payload)
    {
        _resolved[name] = payload;
        if (_pending.TryGetValue(name, out var tcs))
        {
            tcs.TrySetResult(payload);
            _pending.Remove(name);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RejectPromise(string name, string reason)
    {
        _rejected[name] = reason;
        if (_pending.TryGetValue(name, out var tcs))
        {
            tcs.TrySetException(new TerminalException(reason));
            _pending.Remove(name);
        }

        return ValueTask.CompletedTask;
    }
}