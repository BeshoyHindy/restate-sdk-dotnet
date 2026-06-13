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

    /// <summary>Rejects a workflow promise with a reason, awaiting the runtime acknowledgement.</summary>
    public abstract ValueTask RejectPromise(string name, string reason);
}