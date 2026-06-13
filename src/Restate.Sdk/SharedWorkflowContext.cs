namespace Restate.Sdk;

/// <summary>
///     Context for shared handlers on workflows. Provides read-only state access
///     and the ability to signal the workflow via durable promises.
/// </summary>
public abstract class SharedWorkflowContext : SharedObjectContext, ISharedWorkflowContext
{
    /// <summary>Peeks at a workflow promise without blocking. Returns default if not yet resolved.</summary>
    public abstract ValueTask<T?> PeekPromise<T>(string name);

    /// <summary>Resolves a workflow promise with a payload, awaiting the runtime acknowledgement.</summary>
    public abstract ValueTask ResolvePromise<T>(string name, T payload);

    /// <summary>
    ///     Resolves a workflow promise with per-op <see cref="PayloadOptions" /> (G19). The default body
    ///     drops the options and delegates to <see cref="ResolvePromise{T}(string, T)" /> so existing
    ///     implementations keep working; the durable runtime context overrides this to honor the per-op
    ///     unstable opt-out under global <see cref="Hosting.PayloadReplayChecks.Strict" /> mode.
    /// </summary>
    public virtual ValueTask ResolvePromise<T>(string name, T value, PayloadOptions payload) =>
        ResolvePromise(name, value);

    /// <summary>Rejects a workflow promise with a reason, awaiting the runtime acknowledgement.</summary>
    public abstract ValueTask RejectPromise(string name, string reason);

    /// <summary>
    ///     Rejects a workflow promise with a reason and a custom Restate/HTTP error
    ///     <paramref name="code" /> (G30). The default body drops the code and delegates to
    ///     <see cref="RejectPromise(string, string)" /> so existing implementations keep working; the
    ///     durable runtime context overrides this to emit the chosen code.
    /// </summary>
    public virtual ValueTask RejectPromise(string name, string reason, ushort code) =>
        RejectPromise(name, reason);
}