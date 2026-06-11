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

    // §4.3.2 — the CANCEL built-in (Idx=1, Void) after creating awakeables is logged-and-ignored;
    // it must NOT resolve any user awakeable.
    //
    // DOCUMENTED DIVERGENCE (blueprint §5): Rust buffers CANCEL into async_results for later
    // consumption; this fork logs-and-ignores it pending cancellation support. When cancellation
    // lands, this expectation must be flipped DELIBERATELY (the CANCEL signal should then surface
    // as a cancellation), not patched to silence a regression.
    [Fact(Timeout = 10_000)]
    public async Task BuiltInCancelSignal_IsIgnored_PendingCancellationSupport()
    {
        var pump = _sm.ProcessIncomingMessagesAsync(CancellationToken.None);

        var (_, firstSignalId) = _sm.Awakeable();
        var (_, secondSignalId) = _sm.Awakeable();
        Assert.Equal(17u, firstSignalId);
        Assert.Equal(18u, secondSignalId);

        var firstAwait = _sm.AwaitNotificationAsync(firstSignalId, InvocationStateMachine.NotificationKind.Signal);
        var secondAwait = _sm.AwaitNotificationAsync(secondSignalId, InvocationStateMachine.NotificationKind.Signal);

        // CANCEL shape: reserved Idx=1 with a Void result. Must be routed to neither awakeable.
        await DeliverSignalAsync(CreateSignalNotification(InvocationStateMachine.CancelSignalId));

        // Neither awakeable resolves from CANCEL. A real awakeable signal (idx 17) then only
        // resolves the FIRST — proving the CANCEL frame did not bleed into user signal state.
        await DeliverSignalAsync(CreateSignalNotification(17u, new byte[] { 0x2A }));

        var firstResult = await AwaitBounded(firstAwait.AsTask());
        Assert.True(firstResult.IsSuccess);
        Assert.Equal(new byte[] { 0x2A }, firstResult.Value.ToArray());

        Assert.False(secondAwait.IsCompleted, "CANCEL must not resolve the second awakeable");

        _inbound.Writer.Complete();
        await AwaitBounded(pump);
    }
}
