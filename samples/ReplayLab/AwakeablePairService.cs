using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E2 — two awakeables awaited in the OPPOSITE order they were created, the signal-id model's
///     sharpest edge (B4/B8/B9). Signal ids start at 17; the second awakeable created here is
///     signal id 18, the first is 17. Awaiting the SECOND one first means the handler parks on
///     signal 18, and resolving id2 through ingress must wake exactly that awakeable.
///     <list type="bullet">
///         <item>(B4) pre-fix, signal index 0/1 aliased the CANCEL signal, so resolving the second awakeable cancelled/corrupted the invocation instead of completing it;</item>
///         <item>(B8) each <c>await ak.Value</c> with input closed and no signal yet is a suspension point — pre-fix it hung;</item>
///         <item>(B9) the resume batch carries a signal-completion notification, the replayed-awakeable race window where the CompletionManager TOCTOU dropped the only delivery.</item>
///     </list>
///     The handler publishes both ids to <see cref="AwakeableMailbox" /> from a journaled Run so
///     the driving test can read them and resolve through ingress.
/// </summary>
[Service]
public sealed class AwakeablePairService
{
    /// <summary>
    ///     Creates two awakeables, publishes their ids, then awaits the second (signal 18) before
    ///     the first (signal 17). Completes only when both are resolved out of band.
    /// </summary>
    [Handler]
    public async Task<string> AwaitTwo(Context ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        // Awakeable<T> { Id, Value }: Id is the externally-resolvable handle, Value the durable promise.
        var ak1 = ctx.Awakeable<string>(); // signal id 17
        var ak2 = ctx.Awakeable<string>(); // signal id 18

        // Publish the ids out of band so the test can resolve them through ingress while the
        // handler is still parked. Journaled so it runs exactly once across replays.
        await ctx.RunAsync("publish", () =>
        {
            ExecutionProbe.Increment(req.ProbeId, "run:publish");
            AwakeableMailbox.Publish(req.ProbeId, ak1.Id, ak2.Id);
            return Task.FromResult(true);
        }).GetResult();

        // Await out of creation order: park on signal 18 first, then 17.
        var second = await ak2.Value;
        var first = await ak1.Value;

        return $"{first}+{second}";
    }
}
