namespace Restate.Sdk;

/// <summary>
///     Interface for shared handlers on workflows. Provides read-only state access
///     and the ability to signal the workflow via durable promises.
/// </summary>
public interface ISharedWorkflowContext : ISharedObjectContext
{
    /// <summary>Peeks at a workflow promise without blocking. Returns default if not yet resolved.</summary>
    ValueTask<T?> PeekPromise<T>(string name);

    /// <summary>Resolves a workflow promise with a payload, awaiting the runtime acknowledgement.</summary>
    ValueTask ResolvePromise<T>(string name, T payload);

    /// <summary>Rejects a workflow promise with a reason, awaiting the runtime acknowledgement.</summary>
    ValueTask RejectPromise(string name, string reason);

    /// <summary>
    ///     Rejects a workflow promise with a reason and a custom Restate/HTTP error
    ///     <paramref name="code" /> (G30 — shared-core <c>complete_promise</c> carries any
    ///     <c>Failure{code}</c>). The default forwards to <see cref="RejectPromise(string, string)" />
    ///     (code dropped) so existing implementors need no change.
    /// </summary>
    ValueTask RejectPromise(string name, string reason, ushort code) => RejectPromise(name, reason);
}
