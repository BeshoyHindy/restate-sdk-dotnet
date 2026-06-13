using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E1 — the canonical Run → Sleep → Run shape that pre-fix broke in four distinct ways
///     (B1/B2/B3/B8). The 8s sleep exceeds the container's 5s inactivity timeout, so the server
///     closes the input stream and the invocation must genuinely suspend, then resume with a
///     batch shaped <c>RunCommand{a}</c> + <c>RunCompletionNotification{a}</c> + <c>SleepCommand</c>
///     + <c>SleepCompletionNotification</c>. That exact shape is what pre-fix:
///     <list type="bullet">
///         <item>(B1) JSON-deserialized the replayed protobuf command bytes as the user result;</item>
///         <item>(B2) hung in Replaying because notifications inflate <c>known_entries</c> beyond the command count;</item>
///         <item>(B3) double-read the pipe with two concurrent <c>PipeReader</c>s;</item>
///         <item>(B8) could never suspend in the first place — the handler parked forever on EOF.</item>
///     </list>
/// </summary>
[Service]
public sealed class RunSleepRunService
{
    /// <summary>
    ///     Journals run "a", suspends across an 8s sleep, then journals run "b". On resume both
    ///     runs replay from the journal, so each closure executes exactly once even though the
    ///     handler body runs at least twice.
    /// </summary>
    [Handler]
    public async Task<string> Execute(Context ctx, ProbeRequest req)
    {
        // First statement: counts re-invocations. Post-suspension resume re-runs this body.
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        var a = await ctx.RunAsync("a", () =>
        {
            ExecutionProbe.Increment(req.ProbeId, "run:a");
            return Task.FromResult(Guid.NewGuid().ToString("N"));
        }).GetResult();

        // > 5s inactivity → the server closes input → genuine suspension + resume.
        await ctx.Sleep(TimeSpan.FromSeconds(8));

        var b = await ctx.RunAsync("b", () =>
        {
            ExecutionProbe.Increment(req.ProbeId, "run:b");
            return Task.FromResult(Guid.NewGuid().ToString("N"));
        }).GetResult();

        // a survived the suspend/resume from its journaled value; b is freshly journaled.
        return $"{a}|{b}";
    }
}
