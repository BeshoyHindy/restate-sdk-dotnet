namespace Restate.Sdk;

/// <summary>
///     Interface for shared handlers on workflows. Provides read-only state access
///     and the ability to signal the workflow via durable promises.
/// </summary>
public interface ISharedWorkflowContext : ISharedObjectContext
{
    /// <summary>Peeks at a workflow promise without blocking. Returns default if not yet resolved.</summary>
    ValueTask<T?> PeekPromise<T>(string name);

    /// <summary>Resolves a workflow promise with a payload.</summary>
    void ResolvePromise<T>(string name, T payload);

    /// <summary>Rejects a workflow promise with a reason.</summary>
    void RejectPromise(string name, string reason);
}
