using Restate.Sdk;

namespace ReplayLab;

/// <summary>The request/response shape for <see cref="SlowEchoService" />, carrying the probe id round trip.</summary>
public sealed record SlowEchoRequest(string ProbeId);

/// <summary>
///     A deliberately slow echo target shared by E6 (<see cref="LazySendService" />) and E7
///     (<see cref="CallAcrossSuspensionService" />). It durably sleeps 6s — longer than the 5s
///     inactivity timeout — so the CALLER suspends while waiting, which is the whole point: a
///     fast target would never force the caller to park.
/// </summary>
[Service]
public sealed class SlowEchoService
{
    /// <summary>Sleeps 6s durably, then echoes the caller's probe id.</summary>
    [Handler]
    public async Task<string> Echo(Context ctx, SlowEchoRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");
        await ctx.Sleep(TimeSpan.FromSeconds(6));
        return req.ProbeId;
    }
}

/// <summary>
///     E6 — a fire-and-forget Send whose invocation id is resolved lazily AFTER a suspend/resume
///     (B6). Pre-fix, replaying the send UTF-8-decoded the replayed command's protobuf bytes as
///     the invocation id, so the returned id was garbage; the send also blocked waiting for the
///     id at send time. Post-fix the send is non-blocking and the id arrives via a replayed
///     <c>CallInvocationIdCompletionNotification</c> in the resume batch. The 8s sleep forces the
///     suspend so the <c>OneWayCallCommand</c> is journaled and replayed.
/// </summary>
[Service]
public sealed class LazySendService
{
    /// <summary>
    ///     Sends to <see cref="SlowEchoService" />, suspends, then reports the invocation id
    ///     resolved from the replayed notification.
    /// </summary>
    [Handler]
    public async Task<string> SendAndReport(Context ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        // Fire-and-forget: returns a handle whose id resolves lazily, never blocking the send.
        var handle = await ctx.Send<SlowEchoRequest>("SlowEchoService", "Echo", new SlowEchoRequest(req.ProbeId));

        // Suspend; the resume replays the OneWayCallCommand and its invocation-id notification.
        await ctx.Sleep(TimeSpan.FromSeconds(8));

        // Post-fix: from the replayed CallInvocationIdCompletionNotification, not the command bytes.
        var id = await handle.GetInvocationIdAsync();
        return id;
    }
}
