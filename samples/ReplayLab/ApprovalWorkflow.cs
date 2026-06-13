using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E8 — a Workflow whose Run handler parks on a durable promise until a shared handler
///     resolves it (the Template A promise replay path, 4 of the 26 rewritten replay sites). The
///     Run handler awaits <c>ctx.Promise&lt;string&gt;("approval")</c> with no traffic, so it
///     suspends parked on the <c>GetPromiseCommand</c> completion (pre-fix B8 hang). A separate
///     <c>Approve</c> shared handler resolves the promise through ingress; the run's resume batch
///     replays <c>GetPromiseCommand</c> + its completion notification, the path that pre-fix
///     deserialized the replayed command's protobuf bytes as the promise value (B1 shape) or hung
///     on <c>known_entries</c> (B2).
/// </summary>
[Workflow]
public sealed class ApprovalWorkflow
{
    /// <summary>
    ///     The workflow's main handler: blocks on the "approval" promise, then returns the
    ///     approver-supplied decision. Runs exactly once per workflow key.
    /// </summary>
    [Handler]
    public async Task<string> Run(WorkflowContext ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        // Parks > 5s with no traffic → genuine suspension on the promise completion.
        var decision = await ctx.Promise<string>("approval");

        return $"approved:{decision}";
    }

    /// <summary>
    ///     Resolves the "approval" promise from outside the run handler. Shared handler — runs
    ///     concurrently with the parked Run.
    /// </summary>
    [SharedHandler]
    public async Task Approve(SharedWorkflowContext ctx, ProbeRequest req)
    {
        await ctx.ResolvePromise("approval", req.ProbeId);
    }
}
