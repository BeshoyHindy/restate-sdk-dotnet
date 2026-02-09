namespace Restate.Sdk;

/// <summary>
///     Interface for the workflow run handler. Provides read-write state access
///     and durable promises for coordinating with shared handlers.
/// </summary>
public interface IWorkflowContext : IObjectContext, ISharedWorkflowContext
{
    /// <summary>Waits for a workflow promise to be resolved.</summary>
    ValueTask<T> Promise<T>(string name);
}
