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

    /// <summary>
    ///     Gets the value for the given state key, or default if not set, labelling the journal
    ///     entry with <paramref name="entryName" /> so it reads as that name in the Restate UI.
    ///     The name is observability only: it does not change what is read, and omitting it
    ///     records the same entry as <see cref="Get{T}(StateKey{T})" />.
    /// </summary>
    ValueTask<T?> Get<T>(StateKey<T> key, string? entryName)
    {
        return Get(key);
    }

    /// <summary>Lists all state keys for this virtual object instance.</summary>
    ValueTask<string[]> StateKeys();

    /// <summary>
    ///     Lists all state keys for this virtual object instance, labelling the journal entry with
    ///     <paramref name="entryName" />. The name is observability only.
    /// </summary>
    ValueTask<string[]> StateKeys(string? entryName)
    {
        return StateKeys();
    }
}
