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
    ///     Sets the value for the given state key with per-op <see cref="PayloadOptions" /> (G19/G31). The
    ///     default body drops the options and delegates to <see cref="Set{T}(StateKey{T}, T)" /> so existing
    ///     implementations keep working; the durable runtime context overrides this to honor the per-op
    ///     unstable opt-out under global <see cref="Hosting.PayloadReplayChecks.Strict" /> mode.
    /// </summary>
    public virtual void Set<T>(StateKey<T> key, T value, PayloadOptions payload) => Set(key, value);

    /// <summary>Clears a single state key.</summary>
    public abstract void Clear(string key);

    /// <summary>
    ///     Clears a single state key identified by a typed <see cref="StateKey{T}" /> (G32 — API
    ///     symmetry with the typed <see cref="Set{T}(StateKey{T}, T)" />). <c>sys_state_clear</c> takes only the key
    ///     name, so this forwards to <see cref="Clear(string)" />; virtual so a context could specialize
    ///     it, but the name-based behavior is identical.
    /// </summary>
    public virtual void Clear<T>(StateKey<T> key) => Clear(key.Name);

    /// <summary>Clears all state keys for this virtual object instance.</summary>
    public abstract void ClearAll();
}