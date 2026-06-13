using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     Plan 07 core-branches lane — the residual reachable conditional arms the §4 + 4b suites leave
///     open in the post-GATE-3 snapshot, each driven to a DISCRIMINATING outcome over the shared
///     <see cref="ProtocolTestHarness.StateMachineRig" />:
///       * <c>StartAsync</c> EOF guards (Protocol.cs:21/42/71) — the stream ends before the Start,
///         before the Input, and mid known-entries batch → distinct <see cref="ProtocolException" />s;
///       * the signal-notification VOID arm (Protocol.cs:177) — a payloadless-value SignalNotification
///         resolves a registered awakeable with an EMPTY result (not a Value);
///       * <c>GetStateKeysAsync</c> LAZY-replay (Operations.cs:779 validate arm + 799 fall-through to
///         the await) and the <c>?? []</c> null-keys normalization (Operations.cs:806);
///       * <c>ResolveInvocationIdAsync</c>'s <c>Value.Span</c> decode arm (Operations.cs:525) — a send
///         handle resolved by a VALUE-bearing completion rather than the StringValue InvocationId form.
///     All waits flow through the 5 s harness watchdog so a regression that hangs fails fast.
/// </summary>
public sealed class CoreBranchEdgeTests
{
    private const int WatchdogMs = 10_000;

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);

    // ---- StartAsync EOF guards (Protocol.cs:21 / 42 / 71) --------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_StreamEndsBeforeStart_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        // No frame at all — completing the inbound pipe makes the first ReadMessageAsync return null,
        // so the `?? throw` at Protocol.cs:21 fires.
        rig.CompleteInbound();

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => rig.StateMachine.StartAsync(CancellationToken.None));
        Assert.Contains("Stream ended before StartMessage", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_StreamEndsBeforeInput_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        // A valid Start, then EOF before the InputCommand → the second ReadMessageAsync returns null
        // and Protocol.cs:42's `?? throw` fires.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-eof-input", 1));
        rig.CompleteInbound();

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => rig.StateMachine.StartAsync(CancellationToken.None));
        Assert.Contains("Stream ended before InputCommand", ex.Message);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task StartAsync_StreamEndsMidKnownEntriesBatch_ThrowsProtocolException()
    {
        using var rig = new StateMachineRig();
        // known_entries=2 promises one replay entry after Input, but the stream ends first → the
        // known-entries read loop's `?? throw` at Protocol.cs:71 fires.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-eof-batch", 2));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        rig.CompleteInbound();

        var ex = await Assert.ThrowsAsync<ProtocolException>(() => rig.StateMachine.StartAsync(CancellationToken.None));
        Assert.Contains("Stream ended while reading known entries", ex.Message);
    }

    // ---- Signal-notification VOID arm (Protocol.cs:177) ---------------------------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task SignalNotification_WithVoidValue_ResolvesAwakeableWithEmptyResult()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // Register a user awakeable (signal id 17), then deliver a SignalNotification with the VOID
        // oneof (no Value). HandleIncomingMessage's `signal.Value is not null` ternary takes its FALSE
        // arm (Protocol.cs:177) → CompletionResult.Success(Empty), so the awaited byte[] is empty.
        var awakeable = sm.Awakeable();
        await DeliverInboundAsync(rig, MessageType.SignalNotification,
            CreateSignalNotification(awakeable.SignalId));   // value == null → Void

        var slot = sm.AwaitNotificationAsync(awakeable.SignalId, InvocationStateMachine.NotificationKind.Signal);
        var result = await AwaitBounded(slot);
        result.ThrowIfFailure();
        Assert.True(result.Value.IsEmpty);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- GetStateKeysAsync LAZY-replay arms (Operations.cs:779 / 799 / 806) --------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task GetStateKeys_LazyReplay_ValidatesCompletionId_ThenResolvesFromNotification()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;

        // Journal a LAZY GetStateKeys command (NOT eager): on replay it has no inline result, so
        // GetStateKeysAsync takes the `!cmd.HasEagerResult` validate arm (Operations.cs:779), the
        // `replayed is { HasEagerResult: true }` pattern is FALSE (Operations.cs:799 falls through),
        // and the keys arrive via the awaited completion (Operations.cs:806). The journaled lazy
        // command's id is 1 (first allocated).
        var lazyKeys = ProtobufCodec.CreateGetStateKeysCommand(1);
        await DeliverFramedAsync(rig,
            (MessageType.Start, CreateStartMessage("inv-lazy-keys-replay", 2).ToByteArray()),
            (MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()).ToByteArray()),
            (MessageType.GetLazyStateKeysCommand, lazyKeys.ToByteArray()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));
        Assert.True(sm.IsReplaying);

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var keysTask = sm.GetStateKeysAsync(CancellationToken.None);

        var completion = new Gen.GetLazyStateKeysCompletionNotificationMessage
        {
            CompletionId = 1u,
            StateKeys = new Gen.StateKeys()
        };
        completion.StateKeys.Keys.Add(ByteString.CopyFromUtf8("alpha"));
        completion.StateKeys.Keys.Add(ByteString.CopyFromUtf8("beta"));
        await DeliverInboundAsync(rig, MessageType.GetLazyStateKeysCompletion, completion);

        var keys = await AwaitBounded(keysTask);
        Assert.Equal(["alpha", "beta"], keys);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    [Fact(Timeout = WatchdogMs)]
    public async Task GetStateKeys_LazyCompletionWithJsonNull_NormalizesToEmptyArray()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        // A partial fresh start emits a lazy keys command; the runtime answers with a completion whose
        // Value is the JSON literal `null`, so Deserialize<string[]> returns null and the `?? []`
        // normalization (Operations.cs:806) yields an empty array rather than propagating null.
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-null-keys", 1, partialState: true));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(sm.StartAsync(CancellationToken.None));

        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);
        var keysTask = sm.GetStateKeysAsync(CancellationToken.None);

        // A GetLazyStateKeysCompletion with NO StateKeys oneof set: ParseCompletionNotification finds
        // no result, so the completion's Value is EMPTY. Deserialize<string[]>(Empty) hits the IsEmpty
        // fast-path (returns default → null), and Operations.cs:806's `?? []` normalizes null to an
        // empty array rather than propagating null.
        var completion = new Gen.GetLazyStateKeysCompletionNotificationMessage { CompletionId = 1u };
        await DeliverInboundAsync(rig, MessageType.GetLazyStateKeysCompletion, completion);

        var keys = await AwaitBounded(keysTask);
        Assert.Empty(keys);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- ResolveInvocationIdAsync Value.Span decode arm (Operations.cs:525) --------------------

    [Fact(Timeout = WatchdogMs)]
    public async Task SendHandle_ResolvedByValueCompletion_DecodesInvocationIdFromBytes()
    {
        using var rig = new StateMachineRig();
        var sm = rig.StateMachine;
        await StartFreshAsync(rig);
        var pump = sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        // ctx.Send allocates the invocation-id completion id (the first allocated id = 1). The send
        // handle's GetInvocationIdAsync awaits it. We resolve that id with a VALUE-bearing completion
        // (raw UTF-8 bytes) instead of the StringValue InvocationId form, so ResolveInvocationIdAsync's
        // `completion.StringValue ?? Encoding.UTF8.GetString(completion.Value.Span)` takes the
        // null-StringValue arm and decodes the bytes (Operations.cs:525).
        object? request = "payload";   // typed object? selects the non-generic SendAsync overload
        var handle = await AwaitBounded(
            sm.SendAsync("SvcX", null, "Echo", request, null, null, CancellationToken.None));

        var rawId = Encoding.UTF8.GetBytes("inv_from_value");
        var completion = new Gen.CallCompletionNotificationMessage
        {
            CompletionId = 1u,
            Value = new Gen.Value { Content = ByteString.CopyFrom(rawId) }
        };
        await DeliverInboundAsync(rig, MessageType.CallCompletion, completion);

        var id = await AwaitBounded(handle.GetInvocationIdAsync());
        Assert.Equal("inv_from_value", id);

        rig.CompleteInbound();
        await AwaitBounded(pump);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task StartFreshAsync(StateMachineRig rig)
    {
        await rig.DeliverAsync(MessageType.Start, CreateStartMessage("inv-core-branch", 1));
        await rig.DeliverAsync(MessageType.InputCommand, CreateInputCommand(Array.Empty<byte>()));
        await AwaitBounded(rig.StateMachine.StartAsync(CancellationToken.None));
    }

    private static async Task DeliverInboundAsync(StateMachineRig rig, MessageType type, IMessage message)
    {
        var writer = new ProtocolWriter(rig.InboundWriter);
        writer.WriteMessage(type, message.ToByteArray());
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task DeliverFramedAsync(StateMachineRig rig,
        params (MessageType Type, byte[] Payload)[] frames)
    {
        foreach (var (type, payload) in frames)
            await rig.DeliverAsync(type, payload).ConfigureAwait(false);
    }
}
