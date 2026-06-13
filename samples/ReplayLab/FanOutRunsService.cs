using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E3 — 16-way parallel fan-out of journaled Runs whose completion order is jittered so it
///     differs from creation order on attempt 1 (B5). Pre-fix the journal recorded Runs in
///     COMPLETION order, so replay cross-wired results or threw JsonException; the awaited values
///     no longer matched their creation-order positions. Post-fix each Run owns a stable
///     completion id assigned at creation, so the replayed batch resolves every future to the
///     value its creating index produced. The trailing 8s sleep forces a suspend/resume that
///     replays all 16 <c>RunCommand</c>s plus their notifications.
/// </summary>
[Service]
public sealed class FanOutRunsService
{
    // The fan-out width. Wide enough that jittered completion order reliably diverges from
    // creation order, which is the precondition that exposes B5.
    private const int FanOutWidth = 16;

    /// <summary>
    ///     Scatters <see cref="FanOutWidth" /> jittered side effects, then gathers them in
    ///     creation order. Returns <c>[0, 1, ..., 15]</c> iff no cross-wiring occurred.
    /// </summary>
    [Handler]
    public async Task<int[]> Scatter(Context ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        // Fire all Runs concurrently. Each returns the loop index after a random delay, so the
        // futures resolve from notifications in an order unrelated to creation order.
        var futures = Enumerable.Range(0, FanOutWidth).Select(i =>
            ctx.RunAsync($"part-{i}", async () =>
            {
                await Task.Delay(Random.Shared.Next(5, 120));
                ExecutionProbe.Increment(req.ProbeId, $"run:part-{i}");
                return i;
            })).ToArray();

        // Gather in CREATION order. If replay honors completion ids, parts[i] == i for every i.
        var parts = new int[futures.Length];
        for (var i = 0; i < futures.Length; i++)
            parts[i] = await futures[i].GetResult();

        // Force a suspension AFTER the whole fan-out is journaled, so resume replays all 16.
        await ctx.Sleep(TimeSpan.FromSeconds(8));

        return parts;
    }
}
