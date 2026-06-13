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

    /// <summary>Rejects a workflow promise with a reason, awaiting the runtime acknowledgement.</summary>
    public abstract ValueTask RejectPromise(string name, string reason);
}