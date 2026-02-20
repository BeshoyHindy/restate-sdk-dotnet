using Restate.Sdk;

namespace FanOut;

/// <summary>
///     Demonstrates parallel fan-out using Restate's durable futures and combinators.
///     Multiple items are processed concurrently using <c>RunAsync</c> (non-blocking),
///     then gathered with <c>All</c> (wait for all) or <c>Race</c> (first to finish).
///
///     Key patterns demonstrated:
///     - <c>ctx.RunAsync()</c>: Fire off a side effect without blocking. Returns a durable future.
///     - <c>ctx.All()</c>: Await all futures and collect results. Deterministic replay-safe.
///     - <c>ctx.Race()</c>: Return the first result that completes.
///     - <c>ctx.WaitAll()</c>: Yield results in completion order as an async stream.
///
///     All patterns are durable — if the process crashes mid-execution, Restate replays
///     the journal. Completed futures return their journaled results instantly.
/// </summary>
[Service]
public sealed class FanOutService
{
    /// <summary>
    ///     Processes all items in parallel and waits for all to complete.
    ///     Uses <c>RunAsync</c> to fire off all side effects concurrently,
    ///     then <c>All</c> to gather results.
    /// </summary>
    [Handler]
    public async Task<BatchResult> ProcessAll(Context ctx, BatchRequest request)
    {
        ctx.Console.Log($"Processing batch {request.BatchId} with {request.Items.Length} items (All)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Fan out: fire all side effects concurrently
        var futures = new IDurableFuture<ItemResult>[request.Items.Length];
        for (var i = 0; i < request.Items.Length; i++)
        {
            var item = request.Items[i];
            futures[i] = ctx.RunAsync<ItemResult>($"process-{item}",
                async () => await ProcessItem(item));
        }

        // Gather: wait for all results
        var results = await ctx.All(futures);

        sw.Stop();
        ctx.Console.Log($"Batch {request.BatchId} completed in {sw.ElapsedMilliseconds}ms");
        return new BatchResult(request.BatchId, results, sw.Elapsed);
    }

    /// <summary>
    ///     Processes all items in parallel and returns the first to complete.
    ///     Uses <c>RunAsync</c> to fire off all side effects concurrently,
    ///     then <c>Race</c> to return the winner.
    /// </summary>
    [Handler]
    public async Task<ItemResult> ProcessFirst(Context ctx, BatchRequest request)
    {
        ctx.Console.Log($"Processing batch {request.BatchId} with {request.Items.Length} items (Race)...");

        // Fan out
        var futures = new IDurableFuture<ItemResult>[request.Items.Length];
        for (var i = 0; i < request.Items.Length; i++)
        {
            var item = request.Items[i];
            futures[i] = ctx.RunAsync<ItemResult>($"process-{item}",
                async () => await ProcessItem(item));
        }

        // Race: return the first result
        var winner = await ctx.Race(futures);

        ctx.Console.Log($"First result from batch {request.BatchId}: {winner.Item} in {winner.DurationMs}ms");
        return winner;
    }

    /// <summary>
    ///     Demonstrates combining durable timers with side effects using Race.
    ///     Processes an item but times out if it takes longer than a deadline.
    ///     The timer and the work run concurrently — whichever finishes first wins.
    /// </summary>
    [Handler]
    public async Task<ItemResult> ProcessWithTimeout(Context ctx, BatchRequest request)
    {
        ctx.Console.Log($"Processing first item with 5s timeout...");

        var item = request.Items.Length > 0 ? request.Items[0] : "default";

        // Start both the work and a timer concurrently
        var workFuture = ctx.RunAsync<ItemResult>($"process-{item}",
            async () => await ProcessItem(item));

        var timeoutFuture = ctx.RunAsync<ItemResult>("timeout",
            async () =>
            {
                await Task.Delay(5000);
                return new ItemResult(item, "TIMEOUT", 5000);
            });

        // Race: whichever finishes first
        var result = await ctx.Race(workFuture, timeoutFuture);

        ctx.Console.Log($"Result: {result.Result} in {result.DurationMs}ms");
        return result;
    }

    /// <summary>
    ///     Simulates processing an item. Each item takes a random amount of time
    ///     to simulate real-world work (API calls, computations, etc.).
    /// </summary>
    private static async Task<ItemResult> ProcessItem(string item)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Simulate variable-duration work
        var delayMs = Random.Shared.Next(100, 2000);
        await Task.Delay(delayMs);

        var result = $"processed-{item.ToUpperInvariant()}";
        sw.Stop();

        Console.WriteLine($"  [Worker] Processed '{item}' in {sw.ElapsedMilliseconds}ms");
        return new ItemResult(item, result, sw.ElapsedMilliseconds);
    }
}
