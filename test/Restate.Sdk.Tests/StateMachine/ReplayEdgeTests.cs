using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Plan 07 §1.2 4b-i (ReplayEdgeTests) — G3, G4, G8. Drives <see cref="InvocationStateMachine" />
///     over the shared <see cref="ProtocolTestHarness.StateMachineRig" /> exactly like §4.1, but
///     covers the branches §4 leaves open:
///       * G3 — the Call/OneWayCall target-triple comparison as a PER-OPERAND matrix
///         (service-only, handler-only, key-only mismatch), so each operand of the compound
///         <c>||</c> in <c>InvocationJournal.DequeueReplay(...triple...)</c> is exercised
///         independently (§4.1.17 owns the combined Call/OneWayCall happy path + a single mismatch).
///       * G4 — <see cref="InvocationStateMachine" />'s <c>ValidateReplayCompletionId</c>: a
///         journaled id of 0 ⇒ "corrupt journal", a journaled id ≠ the locally allocated one ⇒
///         "non-deterministic replay" (neither arm is in §4).
///       * G8 — <see cref="ProtobufCodec.ParseReplayCommand" />'s default arm reached THROUGH the
///         StartAsync preflight: a command-flagged frame carrying a type id with no replay decoder
///         surfaces a <see cref="ProtocolException" /> out of StartAsync.
/// </summary>
public class ReplayEdgeTests
{
    private const int WatchdogMs = 10_000;

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // Frames Start + Input + the replay batch onto the inbound pipe, then runs the preflight.
    private static async Task<StartInfo> StartReplayAsync(StateMachineRig rig, uint knownEntries,
        params (MessageType Type, byte[] Payload)[] batch)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-replay-edge", knownEntries));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        foreach (var (type, payload) in batch)
            await rig.DeliverAsync(type, payload);
        return await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    // ---- G3: per-operand target-triple mismatch ----------------------------------------------

    [Theory(Timeout = WatchdogMs)]
    // Journaled CallCommand was for (Svc, Handler, key); the live call differs in exactly ONE operand.
    [InlineData("OtherSvc", "Handler", "key")]   // service operand differs
    [InlineData("Svc", "OtherHandler", "key")]   // handler operand differs
    [InlineData("Svc", "Handler", "otherKey")]   // key operand differs
    public async Task CallReplay_SingleOperandTripleMismatch_Throws(
        string liveService, string liveHandler, string liveKey)
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal one CallCommand for the canonical target (ids 1 = invocation idx, 2 = result).
        var journaled = ProtobufCodec.CreateCallCommand("Svc", "Handler", "key", Json("p"), 2, 1);
        await StartReplayAsync(rig, 2, (MessageType.CallCommand, journaled.ToByteArray()));
        Assert.Equal(InvocationState.Replaying, sm.State);

        // Replaying the call with a single differing operand must fail the triple comparison loudly.
        await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.CallAsync<string>(liveService, liveKey, liveHandler, "p", CancellationToken.None).AsTask());
    }

    [Theory(Timeout = WatchdogMs)]
    [InlineData("OtherSvc", "Handler", "key")]
    [InlineData("Svc", "OtherHandler", "key")]
    [InlineData("Svc", "Handler", "otherKey")]
    public async Task SendReplay_SingleOperandTripleMismatch_Throws(
        string liveService, string liveHandler, string liveKey)
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // OneWayCall journals one command (id 1 = invocation idx; no result id).
        var journaled = ProtobufCodec.CreateSendCommand("Svc", "Handler", "key", Json("p"), 0, null, 1);
        await StartReplayAsync(rig, 2, (MessageType.OneWayCallCommand, journaled.ToByteArray()));
        Assert.Equal(InvocationState.Replaying, sm.State);

        await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.SendAsync(liveService, liveKey, liveHandler, "p", null, null, CancellationToken.None).AsTask());
    }

    // ---- G4: ValidateReplayCompletionId arms -------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task SleepReplay_JournaledZeroCompletionId_ThrowsCorruptJournal()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // A SleepCommand whose result_completion_id is 0 is a corrupt/foreign journal: a conformant
        // SDK never writes 0 (ids start at 1 precisely so 0 means field-unset). The Sleep replay pops
        // it and ValidateReplayCompletionId rejects the zero id.
        var sleepZero = ProtobufCodec.CreateSleepCommand(123, 0);
        await StartReplayAsync(rig, 2, (MessageType.SleepCommand, sleepZero.ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.SleepAsync(TimeSpan.FromSeconds(1), CancellationToken.None).AsTask());
        Assert.Contains("Corrupt journal", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SleepReplay_JournaledMismatchedCompletionId_ThrowsNonDeterministic()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // The first completable op locally allocates id 1, but the journaled SleepCommand carries id
        // 99 — a non-deterministic divergence (counters did not advance identically across attempts).
        var sleepMismatch = ProtobufCodec.CreateSleepCommand(123, 99);
        await StartReplayAsync(rig, 2, (MessageType.SleepCommand, sleepMismatch.ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.SleepAsync(TimeSpan.FromSeconds(1), CancellationToken.None).AsTask());
        Assert.Contains("Non-deterministic replay", ex.Message);
    }

    // ---- G8: ParseReplayCommand default arm via StartAsync ------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_CommandFlaggedFrameWithNoDecoder_ThrowsThroughPreflight()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // InputCommand (0x0400) IS command-flagged (IsCommand() == true) but has NO ParseReplayCommand
        // case — inside the known-entries batch it hits the default "Unknown replayed command type"
        // arm, surfaced as a ProtocolException out of StartAsync. (InputCommand is only valid as the
        // single post-Start input frame, never inside the replay batch.)
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-bad-cmd", 2));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Json("x")));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => sm.StartAsync(CancellationToken.None));
        Assert.Contains("Unknown replayed command type", ex.Message);
    }
}
