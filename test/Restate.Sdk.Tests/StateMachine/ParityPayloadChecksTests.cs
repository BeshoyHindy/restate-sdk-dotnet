using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     G13 — replay payload byte-equality checks (NonDeterministicChecksOption parity, lib.rs:237-244)
///     and the per-op PayloadOptions.unstable_serialization opt-out (lib.rs:25-47). Covers, for each
///     payload-bearing command (SetState / Call / Send / CompletePromise / CompleteAwakeable / Output):
///       (a) strict OFF (the DEFAULT) — a byte-DIFFERING payload on replay PASSES (the SDK's historical
///           behavior is preserved; this is the safe-default guarantee),
///       (b) strict ON + SAME bytes — PASSES with no false positive, including a value re-serialized via
///           the SDK serializer path (the core no-false-positive guarantee),
///       (c) strict ON + DIFFERENT bytes — throws JOURNAL_MISMATCH (570),
///       (d) strict ON + per-op PayloadOptions.Unstable — a byte-differing payload is ACCEPTED,
///       (e) the dictionary-ordering caveat as an EXECUTABLE test: a Dictionary whose enumeration order
///           differs across runs serializes to different bytes and would spuriously trip 570 under strict
///           mode, but PASSES once the op is marked Unstable — documenting exactly why strict is opt-in.
///     Effective gate = StrictGlobal AND NOT op.Unstable AND command.HasPayloadValue (the De Morgan
///     inversion of Rust's should_ignore_payload_equality = global_ignore || unstable, journal.rs:32-34).
/// </summary>
public class ParityPayloadChecksTests
{
    private const int WatchdogMs = 10_000;
    private const string Service = "Svc";
    private const string Handler = "Handler";
    private const string Key = "key";

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    /// <summary>
    ///     Buffers the journaled commands (preceded by Input) and runs StartAsync, optionally enabling
    ///     global strict payload checks BEFORE the SM initializes (the proven settable-property pattern,
    ///     mirroring how the host fills NegotiatedProtocolVersion).
    /// </summary>
    private static async Task StartReplayAsync(StateMachineRig rig, uint knownEntries, bool strict,
        params (MessageType Type, byte[] Payload)[] journaled)
    {
        rig.StateMachine.StrictPayloadChecks = strict;
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-payload", knownEntries));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        foreach (var (type, payload) in journaled)
            await rig.DeliverAsync(type, payload);
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    // ---- SetState -----------------------------------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task SetStateReplay_StrictOff_DifferentBytes_Passes()
    {
        using var rig = new StateMachineRig();
        // Journal value 1; live value 99 — DIFFERENT bytes. Strict OFF (default) ⇒ never compared.
        await StartReplayAsync(rig, knownEntries: 2, strict: false,
            (MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand("count", Json(1)).ToByteArray()));

        // No throw: the historical behavior (no payload compare) is preserved by the default.
        rig.StateMachine.SetState("count", 99);
        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SetStateReplay_StrictOn_SameBytes_Passes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand("count", Json(42)).ToByteArray()));

        // SAME logical value re-serialized via the SDK path ⇒ byte-identical ⇒ no false positive.
        rig.StateMachine.SetState("count", 42);
        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SetStateReplay_StrictOn_DifferentBytes_Throws570()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand("count", Json(1)).ToByteArray()));

        var ex = Assert.Throws<ProtocolException>(() => rig.StateMachine.SetState("count", 99));
        Assert.Equal(570, ex.Code);
        Assert.Contains("payload mismatch", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SetStateReplay_StrictOn_Unstable_DifferentBytes_Passes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand("count", Json(1)).ToByteArray()));

        // Per-op opt-out: the byte-differing live value is accepted (the !unstable arm of the gate).
        rig.StateMachine.SetState("count", 99, PayloadOptions.Unstable);
        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
    }

    /// <summary>
    ///     EXECUTABLE dictionary-ordering caveat. System.Text.Json serializes Dictionary in enumeration
    ///     order, which is NOT byte-stable across runs. We simulate the cross-run drift by journaling the
    ///     dictionary in one key order and re-supplying the SAME logical map in a DIFFERENT order on
    ///     replay. Under strict mode this trips 570 (the unavoidable false-positive); marking the op
    ///     Unstable suppresses it — exactly the documented escape hatch.
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task SetStateReplay_StrictOn_DictionaryReordered_Throws570_UnlessUnstable()
    {
        // Journaled bytes: {"a":1,"b":2} (insertion order a→b).
        var journaledBytes = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 });
        // Live dictionary with the SAME pairs but REVERSED insertion order → STJ emits {"b":2,"a":1}.
        var liveReordered = new Dictionary<string, int> { ["b"] = 2, ["a"] = 1 };
        // Sanity: the two byte sequences genuinely differ (proves the caveat is real, not a no-op test).
        Assert.NotEqual(journaledBytes, JsonSerializer.SerializeToUtf8Bytes(liveReordered));

        using (var rig = new StateMachineRig())
        {
            await StartReplayAsync(rig, knownEntries: 2, strict: true,
                (MessageType.SetStateCommand,
                    ProtobufCodec.CreateSetStateCommand("d", journaledBytes).ToByteArray()));
            // Strict + Stable (default) ⇒ the reordered dictionary spuriously trips 570 — the caveat.
            var ex = Assert.Throws<ProtocolException>(() => rig.StateMachine.SetState("d", liveReordered));
            Assert.Equal(570, ex.Code);
        }

        using (var rig = new StateMachineRig())
        {
            await StartReplayAsync(rig, knownEntries: 2, strict: true,
                (MessageType.SetStateCommand,
                    ProtobufCodec.CreateSetStateCommand("d", journaledBytes).ToByteArray()));
            // The documented fix: mark the op Unstable ⇒ the reordered dictionary is accepted.
            rig.StateMachine.SetState("d", liveReordered, PayloadOptions.Unstable);
            Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
        }
    }

    // ---- Call (request parameter) -------------------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_StrictOff_DifferentParameter_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        var journaled = ProtobufCodec.CreateCallCommand(Service, Handler, Key, Json("journaled"), 2, 1);
        await StartReplayAsync(rig, knownEntries: 2, strict: false,
            (MessageType.CallCommand, journaled.ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // DIFFERENT request param; strict OFF ⇒ accepted (historical behavior preserved).
        var call = sm.CallAsync<string>(Service, Key, Handler, "live-different", CancellationToken.None);
        Assert.Equal(InvocationState.Processing, sm.State);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        Assert.Equal("ok", await AwaitBounded(call.AsTask()));

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_StrictOn_SameParameter_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        var journaled = ProtobufCodec.CreateCallCommand(Service, Handler, Key, Json("same"), 2, 1);
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.CallCommand, journaled.ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var call = sm.CallAsync<string>(Service, Key, Handler, "same", CancellationToken.None);
        Assert.Equal(InvocationState.Processing, sm.State);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        Assert.Equal("ok", await AwaitBounded(call.AsTask()));

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_StrictOn_DifferentParameter_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        var journaled = ProtobufCodec.CreateCallCommand(Service, Handler, Key, Json("journaled"), 2, 1);
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.CallCommand, journaled.ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.CallAsync<string>(Service, Key, Handler, "live-different", CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);
        Assert.Contains("payload mismatch", ex.Message);

        rig.CompleteInbound();
        await SwallowAsync(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task CallReplay_StrictOn_Unstable_DifferentParameter_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        var journaled = ProtobufCodec.CreateCallCommand(Service, Handler, Key, Json("journaled"), 2, 1);
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.CallCommand, journaled.ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // unstablePayload: true ⇒ the differing request parameter is accepted under strict mode.
        var call = sm.CallAsync<string>(Service, Key, Handler, "live-different", null, null, null,
            CancellationToken.None, unstablePayload: true);
        Assert.Equal(InvocationState.Processing, sm.State);
        await rig.DeliverAsync(MessageType.CallCompletion, CreateCallCompletion(2, Json("ok")));
        Assert.Equal("ok", await AwaitBounded(call.AsTask()));

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- Send (OneWayCall parameter) ----------------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task SendReplay_StrictOn_DifferentParameter_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        var journaled = ProtobufCodec.CreateSendCommandWithOptions(
            Service, Handler, Key, Json("journaled"), 0, idempotencyKey: null, notificationIdx: 1);
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.OneWayCallCommand, journaled.ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.SendAsync(Service, Key, Handler, (object?)"live-different", null, null, null, null,
                CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);

        rig.CompleteInbound();
        await SwallowAsync(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task SendReplay_StrictOn_Unstable_DifferentParameter_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        var journaled = ProtobufCodec.CreateSendCommandWithOptions(
            Service, Handler, Key, Json("journaled"), 0, idempotencyKey: null, notificationIdx: 1);
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.OneWayCallCommand, journaled.ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await AwaitBounded(sm.SendAsync(Service, Key, Handler, (object?)"live-different", null, null, null, null,
            CancellationToken.None, unstablePayload: true).AsTask());
        Assert.Equal(InvocationState.Processing, sm.State);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- CompletePromise (CompletionValue) ----------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task ResolvePromiseReplay_StrictOn_SameBytes_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // [Input, CompletePromise{value, id=1}, buffered ack] ⇒ knownEntries = 3.
        await StartReplayAsync(rig, knownEntries: 3, strict: true,
            (MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseSuccess("p", Json("v"), 1).ToByteArray()),
            (MessageType.CompletePromiseCompletion,
                new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() }
                    .ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await AwaitBounded(sm.ResolvePromise("p", "v", CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task ResolvePromiseReplay_StrictOn_DifferentBytes_Throws570()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseSuccess("p", Json("journaled"), 1).ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            sm.ResolvePromise("p", "live-different", CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);

        rig.CompleteInbound();
        await SwallowAsync(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task ResolvePromiseReplay_StrictOn_Unstable_DifferentBytes_Passes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartReplayAsync(rig, knownEntries: 3, strict: true,
            (MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseSuccess("p", Json("journaled"), 1).ToByteArray()),
            (MessageType.CompletePromiseCompletion,
                new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() }
                    .ToByteArray()));
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await AwaitBounded(sm.ResolvePromise("p", "live-different", PayloadOptions.Unstable, CancellationToken.None));

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- CompleteAwakeable (Value) ------------------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task ResolveAwakeableReplay_StrictOn_SameBytes_Passes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.CompleteAwakeableCommand,
                ProtobufCodec.CreateCompleteAwakeableSuccess("awk-1", Json("v")).ToByteArray()));

        rig.StateMachine.ResolveAwakeable("awk-1", Json("v"));
        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task ResolveAwakeableReplay_StrictOn_DifferentBytes_Throws570()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.CompleteAwakeableCommand,
                ProtobufCodec.CreateCompleteAwakeableSuccess("awk-1", Json("journaled")).ToByteArray()));

        var ex = Assert.Throws<ProtocolException>(() =>
            rig.StateMachine.ResolveAwakeable("awk-1", Json("live-different")));
        Assert.Equal(570, ex.Code);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task ResolveAwakeableReplay_StrictOn_Unstable_DifferentBytes_Passes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.CompleteAwakeableCommand,
                ProtobufCodec.CreateCompleteAwakeableSuccess("awk-1", Json("journaled")).ToByteArray()));

        rig.StateMachine.ResolveAwakeable("awk-1", Json("live-different"), PayloadOptions.Unstable);
        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
    }

    /// <summary>
    ///     A journaled CompleteAwakeable FAILURE arm carries no comparable Value (HasPayloadValue=false),
    ///     so even under strict mode it is governed by the existing structural eq, never byte-compared —
    ///     parity with Rust's `match (Some(Value), Some(Value)) => true` short-circuit (only Value/Value).
    /// </summary>
    [Fact(Timeout = WatchdogMs)]
    public async Task ResolveAwakeableReplay_StrictOn_JournaledFailure_NotByteCompared()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.CompleteAwakeableCommand,
                ProtobufCodec.CreateCompleteAwakeableFailure("awk-1", 500, "boom").ToByteArray()));

        // The replayed command is a Failure (no Value bytes); resolving with ANY value bytes does not
        // byte-compare (HasPayloadValue=false), so no 570 — only the structural type/name path governs it.
        rig.StateMachine.ResolveAwakeable("awk-1", Json("anything"));
        Assert.Equal(InvocationState.Processing, rig.StateMachine.State);
    }

    // ---- Output (handler return value) --------------------------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task OutputReplay_StrictOn_SameBytes_Passes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.OutputCommand, ProtobufCodec.CreateOutputCommand(Json("result")).ToByteArray()));

        await AwaitBounded(rig.StateMachine.CompleteAsync("result", CancellationToken.None));
        Assert.Equal(InvocationState.Closed, rig.StateMachine.State);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task OutputReplay_StrictOn_DifferentBytes_Throws570()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.OutputCommand, ProtobufCodec.CreateOutputCommand(Json("journaled")).ToByteArray()));

        var ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            rig.StateMachine.CompleteAsync("live-different", CancellationToken.None).AsTask());
        Assert.Equal(570, ex.Code);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task OutputReplay_StrictOn_NullResult_EmptyBytes_Passes()
    {
        using var rig = new StateMachineRig();
        // Journal an EMPTY Output value; replay a NULL result (⇒ empty live bytes). Empty == empty
        // passes — and this exercises the `result is null` arm of the live-output serialization.
        await StartReplayAsync(rig, knownEntries: 2, strict: true,
            (MessageType.OutputCommand,
                ProtobufCodec.CreateOutputCommand(ReadOnlySpan<byte>.Empty).ToByteArray()));

        await AwaitBounded(rig.StateMachine.CompleteAsync(null, CancellationToken.None));
        Assert.Equal(InvocationState.Closed, rig.StateMachine.State);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task OutputReplay_StrictOff_DifferentBytes_Passes()
    {
        using var rig = new StateMachineRig();
        await StartReplayAsync(rig, knownEntries: 2, strict: false,
            (MessageType.OutputCommand, ProtobufCodec.CreateOutputCommand(Json("journaled")).ToByteArray()));

        // Strict OFF ⇒ a divergent return value is accepted (historical behavior).
        await AwaitBounded(rig.StateMachine.CompleteAsync("live-different", CancellationToken.None));
        Assert.Equal(InvocationState.Closed, rig.StateMachine.State);
    }

    // Drains a pump that is expected to fault/close after a forced ProtocolException, swallowing the fault.
    private static async Task SwallowAsync(Task pump)
    {
        try { await pump.WaitAsync(TimeSpan.FromMilliseconds(WatchdogMs)); }
        catch { /* expected: the forced replay mismatch tears down the pump */ }
    }
}
