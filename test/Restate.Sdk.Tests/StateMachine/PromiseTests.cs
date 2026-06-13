using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Mirrors <c>third_party/sdk-shared-core/src/tests/promise.rs</c> (blueprint §4.10, lane 3e)
///     plus the Template A/B replay smoke matrix of §4.1.16. Promise ops are 4 of the 26 rewritten
///     replay sites and ResolvePromise/RejectPromise carry the novel "Template B + id" allocation —
///     a sync void API that BURNS a completion id (proto field 11). That is exactly the B1-class
///     accounting where an off-by-one silently bricks workflow replay, so every case here asserts
///     the wire <c>result_completion_id</c> directly and the §4.10.6 counter-burn case proves the
///     allocated id flows through to the NEXT completable op identically on fresh and replayed runs.
///
///     Driving model (shared with the other StateMachine lanes):
///       * Processing path — <c>Initialize(…, knownEntries: 1)</c>, start the pump, run the op,
///         deliver the wire notification, assert the result and the emitted command frame.
///       * Replay path — buffer the FULL inbound stream (Start + Input + journaled command(s) +
///         buffered notification(s)) BEFORE calling <c>StartAsync</c>, exactly as promise.rs feeds
///         the VM; the op then dequeues its journaled command and resolves against the
///         early-completion slot the preflight parked, with NO additional wire command emitted.
///
///     Every wait flows through <see cref="ProtocolTestHarness.AwaitBounded{T}(Task{T})" /> and the
///     per-test <c>[Fact(Timeout = 10_000)]</c> backstop — the pre-fix completion-id/replay bugs
///     manifest as hangs and JSON-decode-of-protobuf failures, both of which must FAIL (not freeze)
///     the run. No wait is a sync block (xunit v2 only aborts, never fails, sync blockage).
/// </summary>
public class PromiseTests
{
    private const int Timeout = 10_000;

    // JSON encodings reused across cases — promise values round-trip through the SDK's JSON serde,
    // so "my value" on the wire is the JSON string literal, matching promise.rs's b"\"my value\"".
    private static readonly byte[] MyValueJson = "\"my value\""u8.ToArray();

    // ---- §4.10.1 GetPromise processing -------------------------------------------------------

    [Fact(Timeout = Timeout)]
    public async Task GetPromise_Processing_EmitsCommandAndResolvesFromNotification()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var getTask = sm.GetPromiseAsync<string>("my-prom", CancellationToken.None);

        // First completable op → completion id 1 (FirstCompletionId). Resolve it from the wire.
        await rig.DeliverAsync(MessageType.GetPromiseCompletion,
            new Gen.GetPromiseCompletionNotificationMessage
            {
                CompletionId = 1,
                Value = new Gen.Value { Content = ByteString.CopyFrom(MyValueJson) }
            });
        var value = await AwaitBounded(getTask);
        Assert.Equal("my value", value);

        // The emitted GetPromiseCommand must carry key + result_completion_id == 1.
        var command = await FirstCommandAsync(rig, sm, pump, MessageType.GetPromiseCommand);
        var parsed = Gen.GetPromiseCommandMessage.Parser.ParseFrom(command);
        Assert.Equal("my-prom", parsed.Key);
        Assert.Equal(1u, parsed.ResultCompletionId);
    }

    [Fact(Timeout = Timeout)]
    public async Task GetPromise_Processing_FailureNotification_ThrowsTerminalException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var getTask = sm.GetPromiseAsync<string>("my-prom", CancellationToken.None);

        await rig.DeliverAsync(MessageType.GetPromiseCompletion,
            new Gen.GetPromiseCompletionNotificationMessage
            {
                CompletionId = 1,
                Failure = new Gen.Failure { Code = 500, Message = "myerror" }
            });

        var ex = await AwaitBounded(Assert.ThrowsAsync<TerminalException>(async () => await getTask));
        Assert.Equal(500, ex.Code);
        Assert.Equal("myerror", ex.Message);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- §4.10.2 GetPromise replay -----------------------------------------------------------

    [Fact(Timeout = Timeout)]
    public async Task GetPromise_Replay_ResolvesFromBufferedNotification_NoNewCommand()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal: [Input, GetPromiseCommand{key="my-prom", id=1}] + buffered notification{1}.
        // known_entries counts commands AND notifications → Input + command + notification = 3.
        await DeliverReplayBatchAsync(rig, knownEntries: 3,
            (MessageType.GetPromiseCommand, ProtobufCodec.CreateGetPromiseCommand("my-prom", 1)),
            (MessageType.GetPromiseCompletion, new Gen.GetPromiseCompletionNotificationMessage
            {
                CompletionId = 1,
                Value = new Gen.Value { Content = ByteString.CopyFrom(MyValueJson) }
            }));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var value = await AwaitBounded(sm.GetPromiseAsync<string>("my-prom", CancellationToken.None));
        Assert.Equal("my value", value);

        // Replay consumes the journaled command — no GetPromiseCommand is re-emitted on the wire.
        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.GetPromiseCommand));
    }

    [Fact(Timeout = Timeout)]
    public async Task GetPromise_Replay_KeyMismatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 3,
            (MessageType.GetPromiseCommand, ProtobufCodec.CreateGetPromiseCommand("journaled-prom", 1)),
            (MessageType.GetPromiseCompletion, new Gen.GetPromiseCompletionNotificationMessage
            {
                CompletionId = 1,
                Value = new Gen.Value { Content = ByteString.CopyFrom(MyValueJson) }
            }));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Code asks for a DIFFERENT key than the journal recorded → command-name mismatch.
        await AwaitBounded(Assert.ThrowsAsync<ProtocolException>(async () =>
            await sm.GetPromiseAsync<string>("live-prom", CancellationToken.None)));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    // ---- §4.10.3 PeekPromise processing + replay ---------------------------------------------

    [Fact(Timeout = Timeout)]
    public async Task PeekPromise_Processing_VoidNotification_ReturnsDefault()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var peekTask = sm.PeekPromiseAsync<string>("my-prom", CancellationToken.None);

        // Void result = promise not yet completed (peek_promise completed_with_null) → default.
        await rig.DeliverAsync(MessageType.PeekPromiseCompletion,
            new Gen.PeekPromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() });

        var value = await AwaitBounded(peekTask);
        Assert.Null(value);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = Timeout)]
    public async Task PeekPromise_Processing_ValueNotification_ReturnsDeserialized()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var peekTask = sm.PeekPromiseAsync<string>("my-prom", CancellationToken.None);

        await rig.DeliverAsync(MessageType.PeekPromiseCompletion,
            new Gen.PeekPromiseCompletionNotificationMessage
            {
                CompletionId = 1,
                Value = new Gen.Value { Content = ByteString.CopyFrom(MyValueJson) }
            });

        var value = await AwaitBounded(peekTask);
        Assert.Equal("my value", value);

        var command = await FirstCommandAsync(rig, sm, pump, MessageType.PeekPromiseCommand);
        var parsed = Gen.PeekPromiseCommandMessage.Parser.ParseFrom(command);
        Assert.Equal("my-prom", parsed.Key);
        Assert.Equal(1u, parsed.ResultCompletionId);
    }

    [Fact(Timeout = Timeout)]
    public async Task PeekPromise_Replay_ResolvesFromBufferedNotification()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 3,
            (MessageType.PeekPromiseCommand, ProtobufCodec.CreatePeekPromiseCommand("my-prom", 1)),
            (MessageType.PeekPromiseCompletion, new Gen.PeekPromiseCompletionNotificationMessage
            {
                CompletionId = 1,
                Value = new Gen.Value { Content = ByteString.CopyFrom(MyValueJson) }
            }));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var value = await AwaitBounded(sm.PeekPromiseAsync<string>("my-prom", CancellationToken.None));
        Assert.Equal("my value", value);

        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.PeekPromiseCommand));
    }

    // ---- §4.10.4 ResolvePromise / RejectPromise processing -----------------------------------
    // CompletePromiseCommand carries the allocated result_completion_id and is COMPLETABLE (proto
    // field 11): the op AWAITS the CompletePromiseCompletion ack. The happy path delivers a Void ack
    // (success) and asserts both the command shape and that the await resolves. Mirrors promise.rs
    // resolve_promise_succeeds / reject_promise_succeeds, now including the ack barrier.

    [Fact(Timeout = Timeout)]
    public async Task ResolvePromise_Processing_EmitsCompletePromiseCommandWithValueAndId()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var resolve = sm.ResolvePromise("my-prom", "my val", CancellationToken.None);

        // First completable op → completion id 1. Deliver the Void ack so the await resolves.
        await rig.DeliverAsync(MessageType.CompletePromiseCompletion,
            new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() });
        await AwaitBounded(resolve);

        var command = await FirstCommandAsync(rig, sm, pump, MessageType.CompletePromiseCommand);
        var parsed = Gen.CompletePromiseCommandMessage.Parser.ParseFrom(command);
        Assert.Equal("my-prom", parsed.Key);
        Assert.Equal(1u, parsed.ResultCompletionId);
        Assert.Equal(Gen.CompletePromiseCommandMessage.CompletionOneofCase.CompletionValue, parsed.CompletionCase);
        Assert.Equal("\"my val\"", parsed.CompletionValue.Content.ToStringUtf8());
    }

    [Fact(Timeout = Timeout)]
    public async Task RejectPromise_Processing_EmitsCompletePromiseCommandWithFailureAndId()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var reject = sm.RejectPromise("my-prom", "my failure", CancellationToken.None);

        await rig.DeliverAsync(MessageType.CompletePromiseCompletion,
            new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() });
        await AwaitBounded(reject);

        var command = await FirstCommandAsync(rig, sm, pump, MessageType.CompletePromiseCommand);
        var parsed = Gen.CompletePromiseCommandMessage.Parser.ParseFrom(command);
        Assert.Equal("my-prom", parsed.Key);
        Assert.Equal(1u, parsed.ResultCompletionId);
        Assert.Equal(Gen.CompletePromiseCommandMessage.CompletionOneofCase.CompletionFailure, parsed.CompletionCase);
        Assert.Equal(500u, parsed.CompletionFailure.Code);
        Assert.Equal("my failure", parsed.CompletionFailure.Message);
    }

    [Fact(Timeout = Timeout)]
    public async Task ResolvePromise_Processing_AckResolves()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        // The ack is now AWAITED: a Void completion (success) unparks the resolve without throwing.
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var resolve = sm.ResolvePromise("my-prom", "my val", CancellationToken.None);
        await rig.DeliverAsync(MessageType.CompletePromiseCompletion,
            new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() });
        await AwaitBounded(resolve);

        await AwaitBounded(sm.CompleteAsync(MyValueJson, CancellationToken.None));
        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = Timeout)]
    public async Task ResolvePromise_Processing_FailureAck_ThrowsTerminalException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        // Resolving an ALREADY-completed promise returns a Failure (async_results parity, proto
        // Failure=6). The awaited ack surfaces it as a TerminalException — the NEW failure branch.
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var resolve = sm.ResolvePromise("my-prom", "my val", CancellationToken.None);
        await rig.DeliverAsync(MessageType.CompletePromiseCompletion,
            new Gen.CompletePromiseCompletionNotificationMessage
            {
                CompletionId = 1,
                Failure = new Gen.Failure { Code = 409, Message = "promise already completed" }
            });

        var ex = await AwaitBounded(Assert.ThrowsAsync<TerminalException>(async () => await resolve));
        Assert.Equal("promise already completed", ex.Message);
        Assert.Equal(409, ex.Code);

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    [Fact(Timeout = Timeout)]
    public async Task RejectPromise_Processing_FailureAck_ThrowsTerminalException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        // The Failure arm is shared by both variants: rejecting an already-completed promise also
        // surfaces a TerminalException via the awaited ack.
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var reject = sm.RejectPromise("my-prom", "denied", CancellationToken.None);
        await rig.DeliverAsync(MessageType.CompletePromiseCompletion,
            new Gen.CompletePromiseCompletionNotificationMessage
            {
                CompletionId = 1,
                Failure = new Gen.Failure { Code = 409, Message = "promise already completed" }
            });

        var ex = await AwaitBounded(Assert.ThrowsAsync<TerminalException>(async () => await reject));
        Assert.Equal("promise already completed", ex.Message);
        Assert.Equal(409, ex.Code);

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    // ---- §4.10.5 ResolvePromise / RejectPromise replay (id validation) -----------------------

    [Fact(Timeout = Timeout)]
    public async Task ResolvePromise_Replay_DequeuesAndValidatesId_NoNewCommand()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal: [Input, CompletePromiseCommand{key="my-prom", id=1}] + buffered ack — the op is
        // COMPLETABLE, so the unified await consumes the parked CompletePromiseCompletion slot on
        // replay. known_entries = Input + command + notification = 3.
        await DeliverReplayBatchAsync(rig, knownEntries: 3,
            (MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseSuccess("my-prom", "\"my val\""u8, 1)),
            (MessageType.CompletePromiseCompletion,
                new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() }));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Replay path: dequeue + ValidateReplayCompletionId(1, 1) succeeds, resolves from the buffered
        // ack, emits nothing.
        await AwaitBounded(sm.ResolvePromise("my-prom", "my val", CancellationToken.None));

        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.CompletePromiseCommand));
    }

    [Fact(Timeout = Timeout)]
    public async Task ResolvePromise_Replay_MismatchedId_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // The journaled command carries id=7 but the deterministic re-allocation yields id=1 →
        // ValidateReplayCompletionId rejects the divergence (non-deterministic replay).
        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseSuccess("my-prom", "\"my val\""u8, 7)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // The mismatch fires inside the locked prefix; in the async method it surfaces via the task.
        await AwaitBounded(Assert.ThrowsAsync<ProtocolException>(async () =>
            await sm.ResolvePromise("my-prom", "my val", CancellationToken.None)));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    [Fact(Timeout = Timeout)]
    public async Task RejectPromise_Replay_NameMismatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseFailure("journaled-prom", 500, "x", 1)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await AwaitBounded(Assert.ThrowsAsync<ProtocolException>(async () =>
            await sm.RejectPromise("live-prom", "denied", CancellationToken.None)));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    // ---- §4.10.6 Counter-burn determinism (the §4.6.7 analogue) ------------------------------
    // ResolvePromise burns completion id 1, so the FOLLOWING ctx.Sleep must journal id 2 — on BOTH
    // a fresh run and a replayed run. Awaiting the ack does NOT change id order (the id is allocated
    // under the lock before the await). An off-by-one in the id accounting would shift the Sleep's id
    // and brick replay; this is the load-bearing B1 regression for promises.

    [Fact(Timeout = Timeout)]
    public async Task ResolvePromise_ThenSleep_BurnsCompletionId_Fresh()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        sm.Initialize("abc", "key", 0, 1);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var resolve = sm.ResolvePromise("my-prom", "my val", CancellationToken.None);   // burns id 1
        await rig.DeliverAsync(MessageType.CompletePromiseCompletion,
            new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() });
        await AwaitBounded(resolve);

        var sleep = sm.SleepFutureAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);

        await rig.DeliverAsync(MessageType.SleepCompletion, CreateSleepCompletion(2));
        await AwaitBounded(sleep().AsTask());

        var sleepCommand = await FirstCommandAsync(rig, sm, pump, MessageType.SleepCommand);
        var parsed = Gen.SleepCommandMessage.Parser.ParseFrom(sleepCommand);
        Assert.Equal(2u, parsed.ResultCompletionId);              // promiseCompletionId + 1
    }

    [Fact(Timeout = Timeout)]
    public async Task ResolvePromise_ThenSleep_BurnsCompletionId_Replayed()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal mirrors the fresh run: [Input, CompletePromiseCommand{id=1}, SleepCommand{id=2}]
        // + buffered CompletePromiseCompletion{1} + buffered SleepCompletion{2}.
        // known_entries = Input + 2 commands + 2 notifications = 5.
        await DeliverReplayBatchAsync(rig, knownEntries: 5,
            (MessageType.CompletePromiseCommand,
                ProtobufCodec.CreateCompletePromiseSuccess("my-prom", "\"my val\""u8, 1)),
            (MessageType.SleepCommand, ProtobufCodec.CreateSleepCommand(0, 2)),
            (MessageType.CompletePromiseCompletion,
                new Gen.CompletePromiseCompletionNotificationMessage { CompletionId = 1, Void = new Gen.Void() }),
            (MessageType.SleepCompletion, CreateSleepCompletion(2)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Replay: ResolvePromise dequeues + validates id 1 and resolves from the buffered ack; the
        // Sleep dequeues + validates id 2. Any drift in the id burn would surface as a ProtocolException.
        await AwaitBounded(sm.ResolvePromise("my-prom", "my val", CancellationToken.None));
        var sleep = sm.SleepFutureAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None);
        await AwaitBounded(sleep().AsTask());

        // Pure replay re-emits no commands — both the promise and the sleep came from the journal.
        var frames = await CollectFramesAsync(rig, sm, pump);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.CompletePromiseCommand);
        Assert.DoesNotContain(frames, f => f.Header.Type == MessageType.SleepCommand);
    }

    // ---- §4.1.16 Template A/B replay smoke matrix --------------------------------------------
    // The Template A consumers (AttachInvocation, GetInvocationOutput) and the Template B consumers
    // (ResolveAwakeable, RejectAwakeable, CancelInvocationAsync) that §4.1.1-13 don't otherwise
    // exercise. Each gets a happy replay (value/dequeue from the journal) AND a type-mismatch.

    [Fact(Timeout = Timeout)]
    public async Task AttachInvocation_Replay_ResolvesFromBufferedNotification()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 3,
            (MessageType.AttachInvocationCommand, ProtobufCodec.CreateAttachInvocationCommand("inv_1", 1)),
            (MessageType.AttachInvocationCompletion, new Gen.AttachInvocationCompletionNotificationMessage
            {
                CompletionId = 1,
                Value = new Gen.Value { Content = ByteString.CopyFrom(MyValueJson) }
            }));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var value = await AwaitBounded(sm.AttachInvocationAsync<string>("inv_1", CancellationToken.None));
        Assert.Equal("my value", value);

        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.AttachInvocationCommand));
    }

    [Fact(Timeout = Timeout)]
    public async Task AttachInvocation_Replay_TypeMismatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal recorded a GetInvocationOutputCommand where the code attaches → command type mismatch.
        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.GetInvocationOutputCommand,
                ProtobufCodec.CreateGetInvocationOutputCommand("inv_1", 1)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await AwaitBounded(Assert.ThrowsAsync<ProtocolException>(async () =>
            await sm.AttachInvocationAsync<string>("inv_1", CancellationToken.None)));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    [Fact(Timeout = Timeout)]
    public async Task GetInvocationOutput_Replay_ResolvesFromBufferedNotification()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 3,
            (MessageType.GetInvocationOutputCommand, ProtobufCodec.CreateGetInvocationOutputCommand("inv_1", 1)),
            (MessageType.GetInvocationOutputCompletion, new Gen.GetInvocationOutputCompletionNotificationMessage
            {
                CompletionId = 1,
                Value = new Gen.Value { Content = ByteString.CopyFrom(MyValueJson) }
            }));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var value = await AwaitBounded(sm.GetInvocationOutputAsync<string>("inv_1", CancellationToken.None));
        Assert.Equal("my value", value);

        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.GetInvocationOutputCommand));
    }

    [Fact(Timeout = Timeout)]
    public async Task GetInvocationOutput_Replay_TypeMismatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.AttachInvocationCommand, ProtobufCodec.CreateAttachInvocationCommand("inv_1", 1)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await AwaitBounded(Assert.ThrowsAsync<ProtocolException>(async () =>
            await sm.GetInvocationOutputAsync<string>("inv_1", CancellationToken.None)));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    [Fact(Timeout = Timeout)]
    public async Task ResolveAwakeable_Replay_DequeuesCompleteAwakeable_NoNewCommand()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.CompleteAwakeableCommand,
                ProtobufCodec.CreateCompleteAwakeableSuccess("sign_1xyz", new byte[] { 1, 2, 3 })));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        sm.ResolveAwakeable("sign_1xyz", new byte[] { 1, 2, 3 });

        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.CompleteAwakeableCommand));
    }

    [Fact(Timeout = Timeout)]
    public async Task ResolveAwakeable_Replay_TypeMismatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal recorded a SetStateCommand where the code resolves an awakeable → type mismatch.
        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand("k", "1"u8)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        Assert.Throws<ProtocolException>(() => sm.ResolveAwakeable("sign_1xyz", new byte[] { 1 }));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    [Fact(Timeout = Timeout)]
    public async Task RejectAwakeable_Replay_DequeuesCompleteAwakeable_NoNewCommand()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.CompleteAwakeableCommand,
                ProtobufCodec.CreateCompleteAwakeableFailure("sign_1xyz", 500, "timeout")));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        sm.RejectAwakeable("sign_1xyz", "timeout");

        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.CompleteAwakeableCommand));
    }

    [Fact(Timeout = Timeout)]
    public async Task RejectAwakeable_Replay_TypeMismatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.ClearStateCommand, ProtobufCodec.CreateClearStateCommand("k")));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        Assert.Throws<ProtocolException>(() => sm.RejectAwakeable("sign_1xyz", "timeout"));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    [Fact(Timeout = Timeout)]
    public async Task CancelInvocation_Replay_DequeuesSendSignal_NoNewCommand()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // CancelInvocation replays as a SendSignal command (the CANCEL built-in signal, Idx=1).
        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.SendSignalCommand, ProtobufCodec.CreateCancelInvocationCommand("inv_target")));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        await AwaitBounded(sm.CancelInvocationAsync("inv_target", CancellationToken.None));

        Assert.Empty(await CommandsOfTypeAsync(rig, sm, pump, MessageType.SendSignalCommand));
    }

    [Fact(Timeout = Timeout)]
    public async Task CancelInvocation_Replay_TypeMismatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        await DeliverReplayBatchAsync(rig, knownEntries: 2,
            (MessageType.SetStateCommand, ProtobufCodec.CreateSetStateCommand("k", "1"u8)));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        await AwaitBounded(Assert.ThrowsAsync<ProtocolException>(async () =>
            await sm.CancelInvocationAsync("inv_target", CancellationToken.None)));

        rig.CompleteInbound();
        await SwallowPumpAsync(pump);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>
    ///     Buffers a full replay batch (Start{knownEntries} + Input + the given command/notification
    ///     frames, in order) into the rig's inbound pipe and runs StartAsync over it. Commands are
    ///     queued into the replay buffer; notifications are parked as early-completion slots — exactly
    ///     the preflight shape promise.rs feeds the Rust VM. The pump is NOT started here so the
    ///     single-reader invariant holds during preflight; callers start it afterwards.
    /// </summary>
    private static async Task DeliverReplayBatchAsync(StateMachineRig rig, uint knownEntries,
        params (MessageType Type, IMessage Message)[] frames)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("abc", knownEntries, key: "key"));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        foreach (var (type, message) in frames)
            await rig.DeliverAsync(type, message);

        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    /// <summary>
    ///     Drives the invocation to its terminal frames and returns EVERY outbound frame. The direct-SM
    ///     rig never auto-completes its outbound writer (only InvocationHandler does in the full-stack
    ///     path), so a bare drain would block forever waiting for writer completion. Instead this
    ///     finishes the run deterministically: EOF the inbound, drain the pump, then CompleteAsync —
    ///     which emits Output + End. End is the LAST frame by protocol invariant, so draining can stop
    ///     the instant it reads End without waiting on writer completion. Every per-frame read is
    ///     watchdog-bounded, so a hang anywhere still FAILS (not freezes) the test.
    /// </summary>
    private static async Task<IReadOnlyList<(MessageHeader Header, byte[] Payload)>> CollectFramesAsync(
        StateMachineRig rig, InvocationStateMachine sm, Task pump)
    {
        rig.CompleteInbound();
        await AwaitBounded(pump);
        await AwaitBounded(sm.CompleteAsync(Array.Empty<byte>(), CancellationToken.None));

        var reader = new ProtocolReader(rig.OutboundReader);
        var frames = new List<(MessageHeader, byte[])>();
        while (true)
        {
            var message = await AwaitBounded(reader.ReadMessageAsync(CancellationToken.None).AsTask());
            if (message is not { } frame) break;            // writer completed (defensive)
            var header = frame.Header;
            var payload = frame.Payload.ToArray();
            frame.Dispose();
            frames.Add((header, payload));
            if (header.Type == MessageType.End) break;      // terminal sentinel — stop without blocking
        }

        return frames;
    }

    /// <summary>Collects all frames, then returns the payloads matching <paramref name="type" />.</summary>
    private static async Task<IReadOnlyList<byte[]>> CommandsOfTypeAsync(
        StateMachineRig rig, InvocationStateMachine sm, Task pump, MessageType type)
    {
        var frames = await CollectFramesAsync(rig, sm, pump);
        return frames.Where(f => f.Header.Type == type).Select(f => f.Payload).ToList();
    }

    /// <summary>Collects all frames and returns the first payload of <paramref name="type" /> (asserting one exists).</summary>
    private static async Task<byte[]> FirstCommandAsync(
        StateMachineRig rig, InvocationStateMachine sm, Task pump, MessageType type)
    {
        var matches = await CommandsOfTypeAsync(rig, sm, pump, type);
        Assert.NotEmpty(matches);
        return matches[0];
    }

    /// <summary>
    ///     Awaits a pump that is expected to have faulted (the SM's pump rethrows the ProtocolException
    ///     a mismatched replay raises, after FailAll-ing the completion managers). The watchdog still
    ///     bounds the wait; only the rethrow itself is swallowed so the assertion already made stands.
    /// </summary>
    private static async Task SwallowPumpAsync(Task pump)
    {
        try
        {
            await AwaitBounded(pump);
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            // Expected: the pump observed EOF after a mismatch already faulted the run.
        }
    }
}
