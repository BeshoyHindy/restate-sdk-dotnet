namespace Restate.Sdk;

/// <summary>
///     Interface for exclusive handlers on virtual objects. Provides read-write state access.
///     Only one exclusive handler runs at a time per key.
/// </summary>
public interface IObjectContext : ISharedObjectContext
{
    /// <summary>Sets the value for the given state key.</summary>
    void Set<T>(StateKey<T> key, T value);

    /// <summary>
    ///     Sets the value for the given state key, labelling the journal entry with
    ///     <paramref name="entryName" /> so it reads as that name in the Restate UI. The name is
    ///     observability only: it does not change what is written, and omitting it records the
    ///     same entry as <see cref="Set{T}(StateKey{T}, T)" />.
    /// </summary>
    void Set<T>(StateKey<T> key, T value, string? entryName)
    {
        Set(key, value);
    }

    /// <summary>Clears a single state key.</summary>
    void Clear(string key);

    /// <summary>
    ///     Clears a single state key, labelling the journal entry with
    ///     <paramref name="entryName" />. The name is observability only.
    /// </summary>
    void Clear(string key, string? entryName)
    {
        Clear(key);
    }

    /// <summary>Clears all state keys for this virtual object instance.</summary>
    void ClearAll();

    /// <summary>
    ///     Clears all state keys for this virtual object instance, labelling the journal entry
    ///     with <paramref name="entryName" />. The name is observability only.
    /// </summary>
    void ClearAll(string? entryName)
    {
        ClearAll();
    }
}
