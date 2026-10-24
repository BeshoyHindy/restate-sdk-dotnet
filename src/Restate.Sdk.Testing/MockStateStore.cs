namespace Restate.Sdk.Testing;

/// <summary>
///     In-memory state store shared by mock contexts that support virtual object state.
/// </summary>
internal sealed class MockStateStore
{
    private readonly List<string> _entryNames = [];
    private readonly Dictionary<string, object?> _state = [];

    /// <summary>The entry names passed to named state operations, in call order.</summary>
    public IReadOnlyList<string> EntryNames => _entryNames;

    public void SetupState<T>(StateKey<T> key, T value)
    {
        _state[key.Name] = value;
    }

    public void SetupState<T>(string key, T value)
    {
        _state[key] = value;
    }

    public T? GetStateValue<T>(string key)
    {
        return _state.TryGetValue(key, out var val) ? (T?)val : default;
    }

    public bool HasState(string key)
    {
        return _state.ContainsKey(key);
    }

    public ValueTask<T?> Get<T>(StateKey<T> key)
    {
        return _state.TryGetValue(key.Name, out var val) ? new ValueTask<T?>((T?)val) : new ValueTask<T?>(default(T));
    }

    public ValueTask<T?> Get<T>(StateKey<T> key, string? entryName)
    {
        RecordEntryName(entryName);
        return Get(key);
    }

    public ValueTask<string[]> StateKeys()
    {
        return new ValueTask<string[]>(_state.Keys.ToArray());
    }

    public ValueTask<string[]> StateKeys(string? entryName)
    {
        RecordEntryName(entryName);
        return StateKeys();
    }

    public void Set<T>(StateKey<T> key, T value)
    {
        _state[key.Name] = value;
    }

    public void Set<T>(StateKey<T> key, T value, string? entryName)
    {
        RecordEntryName(entryName);
        Set(key, value);
    }

    public void Clear(string key)
    {
        _state.Remove(key);
    }

    public void Clear(string key, string? entryName)
    {
        RecordEntryName(entryName);
        Clear(key);
    }

    public void ClearAll()
    {
        _state.Clear();
    }

    public void ClearAll(string? entryName)
    {
        RecordEntryName(entryName);
        ClearAll();
    }

    private void RecordEntryName(string? entryName)
    {
        if (!string.IsNullOrEmpty(entryName))
            _entryNames.Add(entryName);
    }
}