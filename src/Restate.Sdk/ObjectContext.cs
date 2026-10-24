namespace Restate.Sdk;

/// <summary>
///     Context for exclusive handlers on virtual objects. Provides read-write state access.
///     Only one exclusive handler runs at a time per key.
/// </summary>
public abstract class ObjectContext : SharedObjectContext, IObjectContext
{
    /// <summary>Sets the value for the given state key.</summary>
    public abstract void Set<T>(StateKey<T> key, T value);

    /// <summary>
    ///     Sets the value for the given state key, labelling the journal entry with
    ///     <paramref name="entryName" /> so it reads as that name in the Restate UI. The name is
    ///     observability only: it does not change what is written. The default implementation
    ///     drops the name and writes through <see cref="Set{T}(StateKey{T}, T)" />, so contexts
    ///     that predate entry names keep working.
    /// </summary>
    public virtual void Set<T>(StateKey<T> key, T value, string? entryName)
    {
        Set(key, value);
    }

    /// <summary>Clears a single state key.</summary>
    public abstract void Clear(string key);

    /// <summary>
    ///     Clears a single state key, labelling the journal entry with
    ///     <paramref name="entryName" />. The name is observability only; the default
    ///     implementation drops it and clears through <see cref="Clear(string)" />.
    /// </summary>
    public virtual void Clear(string key, string? entryName)
    {
        Clear(key);
    }

    /// <summary>Clears all state keys for this virtual object instance.</summary>
    public abstract void ClearAll();

    /// <summary>
    ///     Clears all state keys for this virtual object instance, labelling the journal entry
    ///     with <paramref name="entryName" />. The name is observability only; the default
    ///     implementation drops it and clears through <see cref="ClearAll()" />.
    /// </summary>
    public virtual void ClearAll(string? entryName)
    {
        ClearAll();
    }
}