using Restate.Sdk;

namespace ReplayLab;

/// <summary>
///     E10 — named (string-keyed) signals: a durable promise-by-name awaited by one invocation and
///     resolved by an externally-sent named signal carrying a value (the cross-SDK "signals"
///     primitive: TS <c>ctx.promise</c>/signal, Python <c>add_signal_handler</c>, Rust
///     <c>create_signal_handle</c>). Unlike an awakeable (delivered by numeric signal id), the
///     awaited value arrives by NAME — the sender supplies the same name to
///     <see cref="IContext.SendSignal{T}" />, which journals a SendSignalCommand carrying the name
///     oneof (proto:482-505) → SignalNotificationMessage delivers it back by name.
///
///     The discriminating post-condition: <see cref="AwaitDecision" /> blocks on the named signal
///     with no traffic and would HANG forever if the feature regressed (the awaited
///     <see cref="NamedSignal{T}.Value" /> never completes); it returns the sender-supplied value
///     ONLY when the matching named signal is delivered. The handler publishes its OWN invocation id
///     out of band so the test can target the signal at it.
/// </summary>
[Service]
public sealed class NamedSignalLabService
{
    /// <summary>The agreed signal name both sides key on.</summary>
    public const string SignalName = "decision";

    /// <summary>
    ///     Parks on the named signal "decision", returning the sender-supplied value once a matching
    ///     named signal arrives. Publishes this invocation's id (from a journaled Run, exactly once)
    ///     so the driving test can send the signal to it while it is still parked.
    /// </summary>
    [Handler]
    public async Task<string> AwaitDecision(Context ctx, ProbeRequest req)
    {
        ExecutionProbe.Increment(req.ProbeId, "attempt");

        // Publish this invocation's id out of band (journaled → exactly once across replays) so the
        // test learns WHERE to send the signal while the handler is still parked.
        var selfId = ctx.InvocationId;
        await ctx.RunAsync("publish-self", () =>
        {
            NamedSignalMailbox.PublishTarget(req.ProbeId, selfId);
            return Task.FromResult(true);
        }).GetResult();

        // Block on the string-keyed durable promise. With no traffic this parks past the inactivity
        // timeout (genuine suspension); it completes ONLY when a matching named signal is delivered.
        var signal = ctx.NamedSignal<string>(SignalName);
        var decision = await signal.Value;

        return $"decided:{decision}";
    }

    /// <summary>
    ///     Sends the "decision" named signal carrying <paramref name="req" />'s value to the target
    ///     invocation, resuming a parked <see cref="AwaitDecision" />. This is the SEND side of the
    ///     named-signal primitive (sys_complete_signal → SendSignalCommand with the name oneof).
    /// </summary>
    [Handler]
    public async Task Resolve(Context ctx, SignalSendRequest req)
    {
        await ctx.SendSignal(req.TargetInvocationId, SignalName, req.Value);
    }
}

/// <summary>The request for the SEND side: which invocation to signal, and the value to deliver.</summary>
public sealed record SignalSendRequest(string TargetInvocationId, string Value);
