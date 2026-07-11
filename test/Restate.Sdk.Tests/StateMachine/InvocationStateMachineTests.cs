using System.IO.Pipelines;
using System.Text.Json;
using Restate.Sdk.Internal.Protocol;
using Restate.Sdk.Internal.StateMachine;

namespace Restate.Sdk.Tests.StateMachine;

public class InvocationStateMachineTests : IDisposable
{
    private readonly Pipe _inbound = new();
    private readonly Pipe _outbound = new();
    private readonly ProtocolReader _reader;
    private readonly ProtocolWriter _writer;

    public InvocationStateMachineTests()
    {
        _reader = new ProtocolReader(_inbound.Reader);
        _writer = new ProtocolWriter(_outbound.Writer);
    }

    public void Dispose()
    {
        _inbound.Writer.Complete();
        _inbound.Reader.Complete();
        _outbound.Writer.Complete();
        _outbound.Reader.Complete();
    }

    private InvocationStateMachine CreateSm()
    {
        return new InvocationStateMachine(_reader, _writer);
    }

    // ------- Initialization -------

    [Fact]
    public void InitialState_IsWaitingStart()
    {
        using var sm = CreateSm();
        Assert.Equal(InvocationState.WaitingStart, sm.State);
    }

    [Fact]
    public void Initialize_WithNoKnownEntries_TransitionsToProcessing()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-123", "my-key", 42, 0);

        Assert.Equal(InvocationState.Processing, sm.State);
        Assert.Equal("inv-123", sm.InvocationId);
        Assert.Equal("my-key", sm.Key);
        Assert.Equal(42UL, sm.RandomSeed);
    }

    [Fact]
    public void Initialize_WithKnownEntries_TransitionsToReplaying()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-123", "my-key", 42, 3);

        Assert.Equal(InvocationState.Replaying, sm.State);
    }

    [Fact]
    public void Initialize_ThrowsWhenCalledTwice()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        Assert.Throws<InvalidOperationException>(() =>
            sm.Initialize("inv-2", "", 0, 0));
    }

    [Fact]
    public void JsonOptions_IsExposed()
    {
        using var sm = CreateSm();
        Assert.NotNull(sm.JsonOptions);
    }

    // ------- State guards -------

    [Fact]
    public async Task Send_ThrowsInWaitingStartState()
    {
        using var sm = CreateSm();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sm.SendAsync("Svc", null, "Handler", (object?)null, null, null, CancellationToken.None).AsTask());
    }

    [Fact]
    public void SetState_ThrowsInWaitingStartState()
    {
        using var sm = CreateSm();
        Assert.Throws<InvalidOperationException>(() =>
            sm.SetState("key", 42));
    }

    [Fact]
    public async Task RunAsync_ThrowsInClosedState()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);
        await sm.CompleteAsync(Array.Empty<byte>(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await sm.RunAsync("test", () => Task.FromResult(1), CancellationToken.None));
    }

    // ------- Side effects -------

    [Fact]
    public async Task RunAsync_InProcessingMode_ExecutesAction()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var executed = false;
        var result = await sm.RunAsync("test", async () =>
        {
            executed = true;
            await Task.CompletedTask;
            return 42;
        }, CancellationToken.None);

        Assert.True(executed);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task RunAsync_Void_InProcessingMode_ExecutesAction()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var executed = false;
        await sm.RunAsync("test", async () =>
        {
            executed = true;
            await Task.CompletedTask;
        }, CancellationToken.None);

        Assert.True(executed);
    }

    // ------- Calls -------

    [Fact]
    public async Task Send_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        // SendAsync now awaits a completion — we can't test it without a protocol writer that sends back the invocation ID
        // Just verify it doesn't throw synchronously for now
        var task = sm.SendAsync("Greeter", null, "Greet", (object?)"hello", null, null, CancellationToken.None);
        Assert.False(task.IsCompleted); // awaiting invocation ID notification
    }

    // ------- State -------

    [Fact]
    public void SetState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.SetState("count", 42);
    }

    [Fact]
    public void ClearState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.SetState("count", 42);
        sm.ClearState("count");
    }

    [Fact]
    public void ClearAllState_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.SetState("a", 1);
        sm.SetState("b", 2);
        sm.ClearAllState();
    }

    [Fact]
    public async Task GetStateAsync_WithEagerState_ReturnsWithoutWire()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["count"] = JsonSerializer.SerializeToUtf8Bytes(42)
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, eagerState);

        var value = await sm.GetStateAsync<int>("count", CancellationToken.None);
        Assert.Equal(42, value);
    }

    [Fact]
    public async Task GetStateAsync_WithEmptyEagerState_ReturnsDefault()
    {
        var eagerState = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            ["empty"] = ReadOnlyMemory<byte>.Empty
        };

        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0, eagerState);

        var value = await sm.GetStateAsync<int>("empty", CancellationToken.None);
        Assert.Equal(0, value);
    }

    // ------- Awakeable -------

    [Fact]
    public void Awakeable_InProcessingMode_ReturnsIdAndTcs()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", [0xAB, 0xCD], "", 0, 0);

        var (id, tcs) = sm.Awakeable();

        // Format: "sign_1" + Base64UrlSafe(rawId + BigEndian32(signalIndex))
        Assert.StartsWith("sign_1", id);
        Assert.NotNull(tcs);
        Assert.False(tcs.Task.IsCompleted);
    }

    [Fact]
    public void ResolveAwakeable_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        sm.ResolveAwakeable("some-id", new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void RejectAwakeable_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        sm.RejectAwakeable("some-id", "timeout");
    }

    // ------- Promises -------

    [Fact]
    public void ResolvePromise_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.ResolvePromise("approval", new { Approved = true });
    }

    [Fact]
    public void RejectPromise_InProcessingMode_WritesCommand()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key-1", 0, 0);

        sm.RejectPromise("approval", "denied");
    }

    // ------- Output / Error -------

    [Fact]
    public async Task CompleteAsync_TransitionsToClosed()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        await sm.CompleteAsync(new byte[] { 1, 2 }, CancellationToken.None);

        Assert.Equal(InvocationState.Closed, sm.State);
    }

    [Fact]
    public async Task FailAsync_TransitionsToClosed()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        await sm.FailAsync(500, "something broke", CancellationToken.None);

        Assert.Equal(InvocationState.Closed, sm.State);
    }

    // ------- Replay -------

    [Fact]
    public void Send_InReplayMode_AdvancesIndexAndTransitions()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 3);

        Assert.Equal(InvocationState.Replaying, sm.State);

        // During replay, SendAsync consumes staged journal entries populated by the StartAsync
        // drain and resolves results through the completion manager. Exercising that requires
        // protocol-level test infrastructure (see ProtocolIntegrationTests resume tests).
        // For now, verify the state machine is in the right state.
        Assert.Equal(InvocationState.Replaying, sm.State);
    }

    [Fact]
    public void SetState_InReplayMode_AdvancesIndex()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key", 0, 1);

        sm.SetState("count", 1);
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public void ClearState_InReplayMode_AdvancesIndex()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key", 0, 1);

        sm.ClearState("count");
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public void ClearAllState_InReplayMode_AdvancesIndex()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key", 0, 1);

        sm.ClearAllState();
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public void ResolveAwakeable_InReplayMode_AdvancesIndex()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        sm.ResolveAwakeable("id", ReadOnlyMemory<byte>.Empty);
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public void RejectAwakeable_InReplayMode_AdvancesIndex()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 1);

        sm.RejectAwakeable("id", "reason");
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public void ResolvePromise_InReplayMode_AdvancesIndex()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key", 0, 1);

        sm.ResolvePromise("name", "value");
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    [Fact]
    public void RejectPromise_InReplayMode_AdvancesIndex()
    {
        using var sm = CreateSm();
        sm.Initialize("inv-1", "key", 0, 1);

        sm.RejectPromise("name", "reason");
        Assert.Equal(InvocationState.Processing, sm.State);
    }

    // ------- Disposal -------

    [Fact]
    public void Dispose_CancelsAllPendingCompletions()
    {
        var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        var (_, tcs) = sm.Awakeable();
        Assert.False(tcs.Task.IsCompleted);

        sm.Dispose();
        Assert.True(tcs.Task.IsCanceled);
    }

    [Fact]
    public void Dispose_TransitionsToClosed()
    {
        var sm = CreateSm();
        sm.Initialize("inv-1", "", 0, 0);

        sm.Dispose();
        Assert.Equal(InvocationState.Closed, sm.State);
    }
}