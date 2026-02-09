namespace Restate.Sdk;

/// <summary>
///     Interface for exclusive handlers on virtual objects. Provides read-write state access.
///     Only one exclusive handler runs at a time per key.
/// </summary>
public interface IObjectContext : ISharedObjectContext
{
    /// <summary>Sets the value for the given state key.</summary>
    void Set<T>(StateKey<T> key, T value);

    /// <summary>Clears a single state key.</summary>
    void Clear(string key);

    /// <summary>Clears all state keys for this virtual object instance.</summary>
    void ClearAll();
}
