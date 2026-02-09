namespace Restate.Sdk;

/// <summary>
///     Interface for shared handlers on virtual objects. Provides read-only state access
///     and can run concurrently with other shared handlers for the same key.
/// </summary>
public interface ISharedObjectContext : IContext
{
    /// <summary>The key of the virtual object instance.</summary>
    string Key { get; }

    /// <summary>Gets the value for the given state key, or default if not set.</summary>
    ValueTask<T?> Get<T>(StateKey<T> key);

    /// <summary>Lists all state keys for this virtual object instance.</summary>
    ValueTask<string[]> StateKeys();
}
