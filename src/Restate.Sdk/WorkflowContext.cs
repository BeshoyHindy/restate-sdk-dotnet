namespace Restate.Sdk;

/// <summary>
///     Context for the workflow run handler. Provides read-write state access
///     and durable promises for coordinating with shared handlers.
/// </summary>
public abstract class WorkflowContext : ObjectContext, IWorkflowContext
{
    /// <summary>Waits for a workflow promise to be resolved.</summary>
    public abstract ValueTask<T> Promise<T>(string name);

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