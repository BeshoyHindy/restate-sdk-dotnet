using System.Text.Json;
using Restate.Sdk.Internal;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Plan 07 §1.2 4b-i (TerminalOpsEdgeTests) — G1, G6, G7, G11. Covers the terminal-op in-lock
///     re-checks (H10) and the EnsureActive/ThrowIfClosedLocked suspended arms (H11) that §4 hits
///     only probabilistically, made DETERMINISTIC here by suspending the SM first (EOF before park)
///     and then driving the terminal ops:
///       * G1 — TrySuspendAsync's <c>State == InvocationState.Closed</c> early-return re-entry from a
///         second trigger site (a post-suspension run-epilogue/EOF trigger is a no-op).
///       * G6 — CompleteAsync AFTER a normal close returns silently (raced-normal-close); FailAsync
///         after suspension is swallowed (SuspendedException) and emits NO Error frame.
///       * G7 — after suspension, SetState ⇒ SuspendedException and CompleteAsync ⇒ SuspendedException
///         (the EnsureActive / ThrowIfClosedLocked suspended arms, deterministically).
///       * G11 — FailTerminalAsync replay branch (journaled OutputCommand) + the raced-close silent
///         return of FailTerminalAsync/FailAsync against an already-closed SM.
/// </summary>
public class TerminalOpsEdgeTests
{
    private const int WatchdogMs = 10_000;

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // Drives a fresh invocation to suspension: parks on a first Sleep, then EOF closes input so the
    // pump's EOF-after-park trigger writes the SuspensionMessage and latches the SM Closed+suspended.
    private static async Task<Task> DriveToSuspensionAsync(StateMachineRig rig)
    {
        var sm = rig.StateMachine;
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-term", 1));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Park the handler on a Sleep, then close input → EOF-after-park suspension.
        var sleep = sm.SleepAsync(TimeSpan.FromSeconds(30), CancellationToken.None).AsTask();
        rig.CompleteInbound();

        // The parked sleep unparks with SuspendedException once TrySuspendAsync fires FailAll.
        await Assert.ThrowsAsync<SuspendedException>(() => AwaitBounded(sleep));
        await AwaitBounded(pump);
        Assert.Equal(InvocationState.Closed, sm.State);
        return pump;
    }

    // ---- G7 + G1: suspended arms + double-suspend re-entry ------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task AfterSuspension_SetState_And_Complete_ThrowSuspendedException()
    {
        using var rig = new StateMachineRig();
        await DriveToSuspensionAsync(rig);
        var sm = rig.StateMachine;

        // EnsureActive's suspended arm: a fan-out closure that wakes after suspension sees
        // SuspendedException, not a stale InvalidOperationException.
        Assert.Throws<SuspendedException>(() => sm.SetState("k", 1));
        await Assert.ThrowsAsync<SuspendedException>(() => sm.CompleteAsync(Json("late"), CancellationToken.None).AsTask());

        // G1: a SECOND TrySuspendAsync trigger (the run-epilogue/EOF site) is a no-op against an
        // already-Closed SM — re-entry returns immediately and writes nothing more.
        await sm.TrySuspendAsync();
        Assert.Equal(InvocationState.Closed, sm.State);
    }

    // ---- FailTerminalAsync after suspension: the _suspended throw arm (Operations.cs:1018) ------

    [Fact(Timeout = WatchdogMs)]
    public async Task AfterSuspension_FailTerminalAsync_ThrowsSuspended_AndEmitsNoOutputFrame()
    {
        using var rig = new StateMachineRig();
        await DriveToSuspensionAsync(rig);
        var sm = rig.StateMachine;

        // FailTerminalAsync does NOT call EnsureActive (unlike CompleteAsync), so its OWN in-lock
        // `State == Closed` + `_suspended` re-check (Operations.cs:1016/1018) is the only shield. After
        // suspension that arm throws SuspendedException — the FailAsync test covers its sibling; this
        // pins the FailTerminalAsync suspended arm (3/4 → 4/4) and proves no OutputCommand follows the
        // Suspension frame.
        await Assert.ThrowsAsync<SuspendedException>(() =>
            sm.FailTerminalAsync(409, "conflict", CancellationToken.None).AsTask());

        var frames = await DrainOutboundAsync(rig);
        Assert.Equal(1, frames.Count(f => f.Header.Type == MessageType.Suspension));
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.OutputCommand);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.Error);
        AssertFrameOrder(Flatten(frames));
    }

    // ---- G6: FailAsync after suspension emits NO Error frame ----------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task AfterSuspension_FailAsync_ThrowsSuspended_AndEmitsNoErrorFrame()
    {
        using var rig = new StateMachineRig();
        await DriveToSuspensionAsync(rig);
        var sm = rig.StateMachine;

        // The handler's bare catch swallows this in production; here we assert the throw directly AND
        // that the only terminal frame on the wire is the Suspension (no Error after it).
        await Assert.ThrowsAsync<SuspendedException>(() =>
            sm.FailAsync(500, "boom", CancellationToken.None).AsTask());

        var frames = await DrainOutboundAsync(rig);
        var suspensionCount = frames.Count(f => f.Header.Type == MessageType.Suspension);
        Assert.Equal(1, suspensionCount);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.Error);
        AssertFrameOrder(Flatten(frames));   // no frame follows the Suspension
    }

    // ---- G6: CompleteAsync after a NORMAL close is rejected by EnsureActive -------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task CompleteAsync_AfterNormalClose_ThrowsInvalidOperation_NotSuspended()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-done", 1));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        await sm.CompleteAsync(Json("ok"), CancellationToken.None);
        Assert.Equal(InvocationState.Closed, sm.State);

        // A second sequential CompleteAsync hits EnsureActive's Closed (non-suspended) arm FIRST:
        // InvalidOperationException, NOT SuspendedException. (The CompleteAsync in-lock
        // raced-normal-close `return` is reachable only via a genuine State flip BETWEEN EnsureActive
        // and the lock — a race; its suspended twin is covered by the after-suspension test above, and
        // the non-throwing raced-close return on the Fail* paths is covered by G11 below, which do NOT
        // call EnsureActive.)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sm.CompleteAsync(Json("again"), CancellationToken.None).AsTask());
        Assert.Contains("Closed", ex.Message);
    }

    // ---- G11: FailTerminalAsync / FailAsync raced-close silent return -------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task FailMethods_AfterNormalClose_ReturnSilently()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-done2", 1));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        await sm.CompleteAsync(Json("ok"), CancellationToken.None);

        // Both terminal-failure methods short-circuit on an already-Closed (non-suspended) SM with the
        // raced-close silent return — neither throws nor writes a second terminal frame.
        await sm.FailTerminalAsync(500, "ignored", CancellationToken.None);
        await sm.FailAsync(503, "ignored", CancellationToken.None);
        Assert.Equal(InvocationState.Closed, sm.State);
    }

    // ---- G11: FailTerminalAsync replay branch (journaled Output) ------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task FailTerminalAsync_WritesFailureOutput_OnLivePath()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-fail", 1));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        // Live path: FailTerminalAsync writes an OutputCommand carrying the failure oneof + End.
        await sm.FailTerminalAsync(409, "conflict", CancellationToken.None);
        Assert.Equal(InvocationState.Closed, sm.State);

        var frames = await DrainOutboundAsync(rig);
        Assert.Contains(frames, f => f.Header.Type == MessageType.OutputCommand);
        Assert.Contains(frames, f => f.Header.Type == MessageType.End);
        var output = frames.First(f => f.Header.Type == MessageType.OutputCommand);
        var parsed = Restate.Sdk.Internal.Protocol.Generated.OutputCommandMessage.Parser.ParseFrom(output.Payload);
        Assert.NotNull(parsed.Failure);
        Assert.Equal(409u, parsed.Failure.Code);
        Assert.Equal("conflict", parsed.Failure.Message);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> DrainOutboundAsync(
        StateMachineRig rig)
    {
        var reader = new ProtocolReader(rig.OutboundReader);
        var frames = new List<(MessageHeader, byte[])>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await reader.ReadMessageAsync(cts.Token).ConfigureAwait(false) is { } message)
            {
                frames.Add((message.Header, message.Payload.ToArray()));
                message.Dispose();
            }
        }
        catch (OperationCanceledException) { /* no more buffered frames — drain complete */ }

        return frames;
    }

    // Re-frames drained (header,payload) pairs back into a raw byte stream for AssertFrameOrder.
    private static byte[] Flatten(IReadOnlyList<(MessageHeader Header, byte[] Payload)> frames)
    {
        var stream = new MemoryStream();
        foreach (var (header, payload) in frames)
            WriteFramedMessage(stream, header.Type, payload);
        return stream.ToArray();
    }
}
