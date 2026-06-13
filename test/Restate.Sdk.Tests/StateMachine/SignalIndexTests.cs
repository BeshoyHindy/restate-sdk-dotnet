using System.IO.Pipelines;
using Google.Protobuf;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;
using Restate.Sdk.Tests.Testing;
using Gen = Restate.Sdk.Internal.Protocol.Generated;
using static Restate.Sdk.Tests.Testing.ProtocolTestHarness;

namespace Restate.Sdk.Tests.StateMachine;

/// <summary>
///     B4 — signal-index base + reserved-range routing (blueprint §4.3), mirroring the Rust
///     shared-core Journal::default() signal counter (vm/context.rs: signal_index starts at 17,
///     "1 to 16 are reserved", BuiltInSignal::CANCEL = 1).
///
///     Pre-fix the SDK derived awakeable signal ids from a journal-count-based
///     <c>_nextSignalIndex</c> starting at 0, so the first awakeable would have decoded to id 0
///     (a reserved index) and a runtime SignalNotification at idx 17 would never have matched —
///     these tests fail against that behavior and pass now.
///
///     Driven directly against <see cref="InvocationStateMachine" /> over a duplex pipe pair (the
///     <see cref="InvocationStateMachineTests" /> convention), with every wait bounded by
///     <see cref="ProtocolTestHarness.AwaitBounded{T}(System.Threading.Tasks.ValueTask{T})" />.
/// </summary>
public sealed class SignalIndexTests : IDisposable
{
    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly InvocationStateMachine _sm;

    public SignalIndexTests()
    {
        _sm = new InvocationStateMachine(
            new ProtocolReader(_inbound.Reader), new ProtocolWriter(_outbound.Writer));
        // [0xAB, 0xCD] raw id so the decoded awakeable id carries a deterministic invocation prefix.
        _sm.Initialize("inv-signal", [0xAB, 0xCD], "", 0, 1);
    }

    public void Dispose()
    {
        _sm.Dispose();
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
    }

    /// <summary>Writes one already-built SignalNotification frame to the inbound pipe for the pump.</summary>
    private async Task DeliverSignalAsync(Gen.SignalNotificationMessage signal)
    {
        var writer = new ProtocolWriter(_inbound.Writer);
        writer.WriteMessage(MessageType.SignalNotification, signal.ToByteArray());
        await writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // §4.3.1 — two awakeables decode to trailing BE32 = 17 then 18, never the reserved 0..16 range.
    // Async-shaped (with a trivial yield) only so the [Fact(Timeout)] backstop is honored — xunit v2
    // applies Timeout exclusively to async tests.
    [Fact(Timeout = 10_000)]
    public async Task TwoAwakeables_SignalIdsAre17Then18_NeverReservedRange()
    {
        await Task.CompletedTask;
        var (firstId, first) = _sm.Awakeable();
        var (secondId, second) = _sm.Awakeable();

        // First user signal index is 17 (1..16 reserved — CANCEL = 1); ids increment by one.
        Assert.Equal(17u, first);
        Assert.Equal(18u, second);
        Assert.True(first >= InvocationStateMachine.CancelSignalId + 15,
            "user signal ids must skip the reserved 1..16 built-in range");

        // The wire decode id is "sign_1" + Base64Url(rawId + BigEndian32(signalId)).
        Assert.StartsWith("sign_1", firstId);
        Assert.StartsWith("sign_1", secondId);
        Assert.NotEqual(firstId, secondId);
    }

    // §4.3.3 — a SignalNotification at idx 17 with a payload resolves the FIRST awakeable's await.
    [Fact(Timeout = 10_000)]
    public async Task SignalNotificationIdx17_ResolvesFirstAwakeable_WithPayload()
    {
        var pump = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var (_, firstSignalId) = _sm.Awakeable();
        Assert.Equal(17u, firstSignalId);

        // Park on the first awakeable's signal id, then deliver the matching SignalNotification.
        var awaitTask = _sm.AwaitNotificationAsync(firstSignalId, InvocationStateMachine.NotificationKind.Signal);

        var payload = new byte[] { 0x01, 0x02, 0x03 };
        await DeliverSignalAsync(CreateSignalNotification(17u, payload));

        var result = await AwaitBounded(awaitTask);
        Assert.True(result.IsSuccess);
        Assert.Equal(payload, result.Value.ToArray());

        _inbound.Writer.Complete();
        await AwaitBounded(pump);
    }

    // §4.3.2 — inbound CANCEL (Idx=1, Void) cancels THIS invocation: every parked durable await
    // FAULTS with TerminalException(409, "cancelled") — the Restate cross-SDK cancellation convention
    // — rather than resolving as a user signal. This is the DELIBERATE flip of the former
    // "CANCEL is ignored pending cancellation support" expectation (blueprint §5), now that inbound
    // cancellation has landed: CANCEL must surface as a cancellation, not bleed into user signal state.
    [Fact(Timeout = 10_000)]
    public async Task BuiltInCancelSignal_FaultsParkedAwaits_WithTerminal409()
    {
        var pump = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var (_, firstSignalId) = _sm.Awakeable();
        var (_, secondSignalId) = _sm.Awakeable();
        Assert.Equal(17u, firstSignalId);
        Assert.Equal(18u, secondSignalId);

        var firstAwait = _sm.AwaitNotificationAsync(firstSignalId, InvocationStateMachine.NotificationKind.Signal);
        var secondAwait = _sm.AwaitNotificationAsync(secondSignalId, InvocationStateMachine.NotificationKind.Signal);

        // CANCEL shape: reserved Idx=1 with a Void result. It must cancel the invocation, faulting
        // BOTH parked awaits with a 409 TerminalException — not resolve either as a user signal.
        await DeliverSignalAsync(CreateSignalNotification(InvocationStateMachine.CancelSignalId));

        var firstEx = await Assert.ThrowsAsync<TerminalException>(async () => await AwaitBounded(firstAwait.AsTask()));
        var secondEx = await Assert.ThrowsAsync<TerminalException>(async () => await AwaitBounded(secondAwait.AsTask()));
        Assert.Equal(409, firstEx.Code);
        Assert.Equal("cancelled", firstEx.Message);
        Assert.Equal(409, secondEx.Code);

        // The handler CancellationToken is also cancelled so non-awaiting handler code unwinds.
        Assert.True(_sm.CancelToken.IsCancellationRequested);
        Assert.True(_sm.IsCancellationRequested);

        _inbound.Writer.Complete();
        await AwaitBounded(pump);
    }

    // §5 — a RESERVED built-in signal in the 2..16 range (not CANCEL, not a user awakeable) is
    // logged-and-ignored: it must NOT cancel the invocation and must NOT resolve any user awakeable.
    // A real idx-17 signal then still resolves the awakeable, proving the reserved frame stranded
    // no one. (idx==1 is CANCEL and is covered separately above.)
    [Fact(Timeout = 10_000)]
    public async Task ReservedBuiltInSignal_IsIgnored_DoesNotCancelOrResolve()
    {
        var pump = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var (_, firstSignalId) = _sm.Awakeable();
        Assert.Equal(17u, firstSignalId);
        var awaitTask = _sm.AwaitNotificationAsync(firstSignalId, InvocationStateMachine.NotificationKind.Signal);

        // A reserved built-in signal (idx 2) — in the 2..16 range — is ignored, not cancel, not user.
        await DeliverSignalAsync(CreateSignalNotification(2u));

        Assert.False(awaitTask.IsCompleted, "a reserved signal must not resolve a numeric awakeable");
        Assert.False(_sm.IsCancellationRequested, "a reserved (non-CANCEL) signal must not cancel");

        // A real idx-17 signal then resolves the awakeable normally.
        await DeliverSignalAsync(CreateSignalNotification(17u, new byte[] { 0x2A }));
        var result = await AwaitBounded(awaitTask.AsTask());
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x2A }, result.Value.ToArray());

        _inbound.Writer.Complete();
        await AwaitBounded(pump);
    }

    // §5 — a NAMED signal (oneof name, no numeric idx) is handled without crashing: this SDK has no
    // named-signal user API, so it is logged-and-ignored. It must NOT fault the invocation, must NOT
    // resolve any numeric awakeable, and the handler proceeds normally — a real idx-17 signal then
    // still resolves the FIRST awakeable, proving the named frame stranded no one.
    [Fact(Timeout = 10_000)]
    public async Task NamedSignal_IsIgnored_DoesNotResolveOrFaultAwakeable()
    {
        var pump = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var (_, firstSignalId) = _sm.Awakeable();
        Assert.Equal(17u, firstSignalId);
        var awaitTask = _sm.AwaitNotificationAsync(firstSignalId, InvocationStateMachine.NotificationKind.Signal);

        // A named signal with a payload — there is no numeric waiter for a name, so it is ignored.
        await DeliverSignalAsync(CreateNamedSignalNotification("my-named-signal", new byte[] { 0x99 }));

        // A degenerate signal with NEITHER idx NOR name set (oneof unset) also falls to the named
        // branch (Idx is null) with a null Name — it must be ignored just as safely (covers the
        // null-name path of the log). This is a malformed/empty frame defensive case.
        await DeliverSignalAsync(CreateNamedSignalNotification(null, new byte[] { 0x88 }));

        // The numeric awakeable is still unresolved and un-faulted (neither frame touched it).
        Assert.False(awaitTask.IsCompleted, "a named signal must not resolve a numeric awakeable");
        Assert.False(_sm.IsCancellationRequested, "a named signal must not cancel the invocation");

        // A real idx-17 signal then resolves the awakeable normally.
        await DeliverSignalAsync(CreateSignalNotification(17u, new byte[] { 0x2A }));
        var result = await AwaitBounded(awaitTask.AsTask());
        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x2A }, result.Value.ToArray());

        _inbound.Writer.Complete();
        await AwaitBounded(pump);
    }
}
