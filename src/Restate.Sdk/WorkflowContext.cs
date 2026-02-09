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

    /// <summary>Resolves a workflow promise with a payload.</summary>
    public abstract void ResolvePromise<T>(string name, T payload);

    /// <summary>Rejects a workflow promise with a reason.</summary>
    public abstract void RejectPromise(string name, string reason);
}