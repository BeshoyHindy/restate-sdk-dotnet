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
    ///     Sets the value for the given state key with per-op <see cref="PayloadOptions" /> (G19/G31).
    ///     Under global <see cref="Hosting.PayloadReplayChecks.Strict" /> the journaled value bytes are
    ///     byte-compared on replay against the live re-serialized bytes, unless
    ///     <paramref name="payload" />.<see cref="PayloadOptions.UnstableSerialization" /> opts this op out
    ///     (use it for values that serialize an unordered Dictionary/HashSet via System.Text.Json). The
    ///     default forwards to <see cref="Set{T}(StateKey{T}, T)" /> (Stable) so existing implementors need
    ///     no change; the SDK's concrete context overrides it to honor the opt-out.
    /// </summary>
    void Set<T>(StateKey<T> key, T value, PayloadOptions payload) => Set(key, value);

    /// <summary>Clears a single state key.</summary>
    void Clear(string key);

    /// <summary>
    ///     Clears a single state key identified by a typed <see cref="StateKey{T}" /> (G32 — API
    ///     symmetry with the typed <see cref="Set{T}(StateKey{T}, T)" />/<c>Get</c> ops; <c>sys_state_clear</c> takes
    ///     only the key name, so this just forwards to <see cref="Clear(string)" /> with
    ///     <see cref="StateKey{T}.Name" />). Default-implemented so existing implementors need no change.
    /// </summary>
    void Clear<T>(StateKey<T> key) => Clear(key.Name);

    /// <summary>Clears all state keys for this virtual object instance.</summary>
    void ClearAll();
}
